using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ores.Net.Runtime;

/// <summary>
/// Trusted-host AEAD v1. This is deliberately NOT a guest/FFI export:
/// the Atto compiler must authenticate context/actor ownership and capability
/// before admitting an opaque key handle. No generic crypto fallback.
/// </summary>
public static class CryptoAeadHostV1
{
    public enum Algorithm { Aes256Gcm = 1, Chacha20Poly1305 = 2 }

    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int MaxMessageBytes = 16 * 1024 * 1024;
    public const int MaxAadBytes = 64 * 1024;
    public const ulong MaxInvocationsPerKey = 1UL << 32;

    public static bool IsSupported(Algorithm algorithm) => algorithm switch
    {
        Algorithm.Aes256Gcm => AesGcm.IsSupported,
        Algorithm.Chacha20Poly1305 => ChaCha20Poly1305.IsSupported,
        _ => false
    };

    public sealed class Sealed
    {
        private readonly byte[] _nonce;
        private readonly byte[] _ciphertextAndTag;

        internal Sealed(byte[] nonce, byte[] ciphertextAndTag)
        {
            _nonce = (byte[])nonce.Clone();
            _ciphertextAndTag = (byte[])ciphertextAndTag.Clone();
        }

        public byte[] Nonce => (byte[])_nonce.Clone();
        public byte[] CiphertextAndTag => (byte[])_ciphertextAndTag.Clone();
    }

    public static Key GenerateKey(Algorithm algorithm)
    {
        if (!IsSupported(algorithm))
            throw new PlatformNotSupportedException("AEAD algorithm unavailable on this platform");
        byte[] bytes = new byte[KeyBytes];
        byte[] prefix = new byte[4];
        try
        {
            RandomNumberGenerator.Fill(bytes);
            RandomNumberGenerator.Fill(prefix);
            return new Key(algorithm, bytes, prefix);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(prefix);
            throw;
        }
    }

    /// <summary>Non-copyable at the guest boundary; host seal/open is serialized.</summary>
    public sealed class Key : IDisposable
    {
        private readonly object _gate = new();
        private readonly Algorithm _algorithm;
        private readonly byte[] _material;
        private readonly byte[] _prefix;
        private ulong _counter;
        private bool _closed;

        internal Key(Algorithm algorithm, byte[] material, byte[] prefix)
        {
            _algorithm = algorithm;
            _material = material;
            _prefix = prefix;
        }

        private void EnsureOpen()
        {
            if (_closed) throw new ObjectDisposedException(nameof(Key));
        }

        private byte[] ReserveNonce()
        {
            if (_counter >= MaxInvocationsPerKey)
                throw new InvalidOperationException("AEAD per-key invocation limit exhausted");
            byte[] nonce = new byte[NonceBytes];
            _prefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), _counter++);
            return nonce;
        }

        public Sealed Seal(ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
        {
            if (aad.Length > MaxAadBytes || plaintext.Length > MaxMessageBytes)
                throw new ArgumentOutOfRangeException(nameof(plaintext), "AEAD size limit exceeded");
            lock (_gate)
            {
                EnsureOpen();
                byte[] nonce = ReserveNonce();
                byte[] ciphertext = new byte[plaintext.Length + TagBytes];
                try
                {
                    Span<byte> data = ciphertext.AsSpan(0, plaintext.Length);
                    Span<byte> tag = ciphertext.AsSpan(plaintext.Length, TagBytes);
                    if (_algorithm == Algorithm.Aes256Gcm)
                    {
                        using var provider = new AesGcm(_material, TagBytes);
                        provider.Encrypt(nonce, plaintext, data, tag, aad);
                    }
                    else
                    {
                        using var provider = new ChaCha20Poly1305(_material);
                        provider.Encrypt(nonce, plaintext, data, tag, aad);
                    }
                    return new Sealed(nonce, ciphertext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(ciphertext);
                }
            }
        }

        // All malformed envelopes, invalid tags, wrong AAD and wrong keys
        // fail identically (false + null); never publish unverified plaintext.
        public bool TryOpen(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad,
            ReadOnlySpan<byte> ciphertextAndTag, out byte[]? plaintext)
        {
            plaintext = null;
            if (nonce.Length != NonceBytes || aad.Length > MaxAadBytes
                || ciphertextAndTag.Length < TagBytes
                || ciphertextAndTag.Length > MaxMessageBytes + TagBytes)
                return false;
            lock (_gate)
            {
                EnsureOpen();
                int count = ciphertextAndTag.Length - TagBytes;
                byte[] output = new byte[count];
                try
                {
                    ReadOnlySpan<byte> data = ciphertextAndTag[..count];
                    ReadOnlySpan<byte> tag = ciphertextAndTag[count..];
                    if (_algorithm == Algorithm.Aes256Gcm)
                    {
                        using var provider = new AesGcm(_material, TagBytes);
                        provider.Decrypt(nonce, data, tag, output, aad);
                    }
                    else
                    {
                        using var provider = new ChaCha20Poly1305(_material);
                        provider.Decrypt(nonce, data, tag, output, aad);
                    }
                    plaintext = output;
                    return true;
                }
                catch (CryptographicException)
                {
                    CryptographicOperations.ZeroMemory(output);
                    return false;
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(output);
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                CryptographicOperations.ZeroMemory(_material);
                CryptographicOperations.ZeroMemory(_prefix);
            }
            GC.SuppressFinalize(this);
        }
    }
}
