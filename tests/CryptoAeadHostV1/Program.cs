using Ores.Net.Runtime;
using System.Security.Cryptography;

static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Exercise(CryptoAeadHostV1.Algorithm algorithm)
    {
        using var key = CryptoAeadHostV1.GenerateKey(algorithm);
        using var foreign = CryptoAeadHostV1.GenerateKey(algorithm);
        byte[] aad = [1, 2, 3];
        byte[] payload = [3, 7, 10, 42];
        var nonces = new HashSet<string>();
        for (int i = 0; i < 128; i++)
        {
            var sealedValue = key.Seal(aad, payload);
            Check(sealedValue.Nonce.Length == 12, "AEAD nonce size");
            Check(sealedValue.CiphertextAndTag.Length == payload.Length + 16, "AEAD tag size");
            Check(nonces.Add(Convert.ToHexString(sealedValue.Nonce)), "reused AEAD nonce");
            Check(key.TryOpen(sealedValue.Nonce, aad, sealedValue.CiphertextAndTag, out byte[]? opened)
                    && opened is not null && opened.SequenceEqual(payload), "failed round trip");
            if (opened is not null) CryptographicOperations.ZeroMemory(opened);

            byte[] tampered = sealedValue.CiphertextAndTag;
            tampered[0] ^= 1;
            Check(!key.TryOpen(sealedValue.Nonce, aad, tampered, out byte[]? leaked) && leaked is null,
                "tampering was accepted");
            Check(!key.TryOpen(sealedValue.Nonce, [2], sealedValue.CiphertextAndTag, out leaked)
                    && leaked is null, "wrong AAD accepted");
            Check(!foreign.TryOpen(sealedValue.Nonce, aad, sealedValue.CiphertextAndTag, out leaked)
                    && leaked is null, "wrong key accepted");
            byte[] wrongNonce = sealedValue.Nonce;
            wrongNonce[11] ^= 1;
            Check(!key.TryOpen(wrongNonce, aad, sealedValue.CiphertextAndTag, out leaked)
                    && leaked is null, "wrong nonce accepted");
        }
        var empty = key.Seal([], []);
        Check(empty.CiphertextAndTag.Length == 16, "empty encrypted message tag size");
        Check(key.TryOpen(empty.Nonce, [], empty.CiphertextAndTag, out byte[]? emptyRecovered)
                && emptyRecovered is not null && emptyRecovered.Length == 0, "empty round trip");
        key.Dispose();
        bool closedRejected = false;
        try { key.Seal([], []); }
        catch (ObjectDisposedException) { closedRejected = true; }
        Check(closedRejected, "closed key reuse accepted");
    }

    private static void CheckKnownAnswerVectors()
    {
        byte[] zeroKey = new byte[32];
        byte[] zeroNonce = new byte[12];
        Span<byte> zeroOutput = Span<byte>.Empty;
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(zeroKey, 16))
        {
            aes.Encrypt(zeroNonce, ReadOnlySpan<byte>.Empty, zeroOutput, tag);
        }
        Check(Convert.ToHexString(tag) == "530F8AFBC74536B9A963B4F1C4CB738B",
            "NIST AES-256-GCM zero-key vector mismatch");

        if (!ChaCha20Poly1305.IsSupported)
            throw new PlatformNotSupportedException("ChaCha20-Poly1305 required for full parity");
        tag.AsSpan().Clear();
        using (var chacha = new ChaCha20Poly1305(zeroKey))
        {
            chacha.Encrypt(zeroNonce, ReadOnlySpan<byte>.Empty, zeroOutput, tag);
        }
        Check(Convert.ToHexString(tag) == "4EB972C9A8FB3A1B382BB4D36F5FFAD1",
            "IETF ChaCha20-Poly1305 zero-key vector mismatch");
        CryptographicOperations.ZeroMemory(zeroKey);
        CryptographicOperations.ZeroMemory(tag);
    }

    private static int Main()
    {
        CheckKnownAnswerVectors();
        int executed = 0;
        foreach (var algorithm in new[] {
            CryptoAeadHostV1.Algorithm.Aes256Gcm,
            CryptoAeadHostV1.Algorithm.Chacha20Poly1305
        })
        {
            if (!CryptoAeadHostV1.IsSupported(algorithm))
            {
                Console.WriteLine($"{algorithm}: UNSUPPORTED (not a pass)");
                continue;
            }
            Exercise(algorithm);
            executed++;
            Console.WriteLine($"{algorithm}: host test PASS");
        }
        if (executed != 2)
        {
            Console.Error.WriteLine("Both AEAD algorithms must be supported for full parity");
            return 2;
        }
        Console.WriteLine("Host-only crypto tests PASS; compiler/guest/AOT unverified");
        return 0;
    }
}
