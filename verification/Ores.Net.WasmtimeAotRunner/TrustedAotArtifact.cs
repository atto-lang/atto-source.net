using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

// Strict precompiled artifact gate used exclusively by the NativeAOT probe.
// The artifact is *native machine code*: Wasmtime cannot validate it as
// untrusted Wasm. Do not deserialize until the signature, exact metadata,
// source identity, RID and executable bytes have been authenticated.
//
// The trust anchor (public RSA key) is supplied by a trusted test harness.
// Production must pin the approved signing identity in its immutable host
// configuration or binary; never accept a tenant-supplied verification key,
// manifest, or self-signed artifact as authority.
internal static class TrustedAotArtifact
{
    internal const int MaxArtifactBytes = 16 * 1024 * 1024;
    private const int MaxManifestBytes = 4096;
    private const string ExpectedEngineProfile =
        "fuel-stack262144-mem64off-reftypesoff-multimemoryoff-threadsoff";
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schema_version", "guest_kind", "wasmtime_version", "target_rid",
        "engine_profile", "guest_abi", "source_sha256", "artifact_sha256",
        "artifact_size"
    };

    internal static byte[] ReadBoundedFile(string path, int maxLength)
    {
        // The input may be concurrently replaced, truncated, or extended.
        // Open it ONCE and never allocate based on a separate FileInfo stat:
        // File.ReadAllBytes after a length check can allocate arbitrarily
        // large memory when an adversary expands the path between the calls.
        // FileShare.Read is additional protection on Windows, but must not
        // be relied on as an exclusive writer lock on Unix.
        if (maxLength <= 0)
            throw new SecurityException("Invalid AOT artifact read limit");
        using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, options: FileOptions.SequentialScan);
        long length = input.Length;
        if (length <= 0 || length > maxLength)
            throw new SecurityException("AOT artifact file exceeds its admission limit");

        // Allocation is bounded by maxLength. A short read or an appended
        // byte is rejected; exactly these in-memory bytes are authenticated
        // and passed to Module.Deserialize (never reopen the original path).
        byte[] bytes = new byte[checked((int)length)];
        try
        {
            input.ReadExactly(bytes);
        }
        catch (EndOfStreamException e)
        {
            throw new SecurityException("AOT artifact truncated during admission", e);
        }
        if (input.ReadByte() != -1)
            throw new SecurityException("AOT artifact grew during admission");
        return bytes;
    }

    internal static void Verify(
        byte[] manifestBytes,
        byte[] signature,
        byte[] artifact,
        string trustedPublicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(artifact);
        if (manifestBytes.Length is < 1 or > MaxManifestBytes ||
            signature.Length is < 1 or > 1024 ||
            artifact.Length is < 1 or > MaxArtifactBytes)
            throw new SecurityException("Invalid signed AOT artifact lengths");
        if (string.IsNullOrWhiteSpace(trustedPublicKeyPem) ||
            trustedPublicKeyPem.Length > 8192)
            throw new SecurityException("Missing pinned AOT verifier identity");

        using var verifier = RSA.Create();
        verifier.ImportFromPem(trustedPublicKeyPem);
        if (verifier.KeySize < 3072 ||
            !verifier.VerifyData(manifestBytes, signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new SecurityException("AOT manifest signer not trusted");

        // Parse ONLY after verifying signature; reject duplicated or unknown
        // JSON properties so a second parser cannot reinterpret policy.
        using var doc = JsonDocument.Parse(manifestBytes);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new SecurityException("AOT manifest must be a JSON object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!ExactFields.Contains(property.Name) || !seen.Add(property.Name))
                throw new SecurityException("Unexpected/duplicate AOT manifest property");
        }
        if (seen.Count != ExactFields.Count)
            throw new SecurityException("Missing signed AOT manifest property");

        if (root.GetProperty("schema_version").GetInt32() != 1 ||
            root.GetProperty("guest_kind").GetString() != "synthetic-ci-only" ||
            root.GetProperty("wasmtime_version").GetString() != "50.0.1" ||
            root.GetProperty("target_rid").GetString() != RuntimeInformation.RuntimeIdentifier ||
            root.GetProperty("engine_profile").GetString() != ExpectedEngineProfile ||
            root.GetProperty("guest_abi").GetString() != "atto-tenantvm-test-i32-v0" ||
            root.GetProperty("artifact_size").GetInt32() != artifact.Length)
            throw new SecurityException("AOT guest ABI/runtime/RID/profile mismatch");

        string? sourceSha256 = root.GetProperty("source_sha256").GetString();
        if (!IsLowerHexSha256(sourceSha256))
            throw new SecurityException("Source digest is not a SHA-256 identity");

        string? artifactHex = root.GetProperty("artifact_sha256").GetString();
        if (!IsLowerHexSha256(artifactHex))
            throw new SecurityException("Artifact digest has invalid syntax");
        byte[] approvedDigest = Convert.FromHexString(artifactHex!);
        byte[] actualDigest = SHA256.HashData(artifact);
        if (!CryptographicOperations.FixedTimeEquals(approvedDigest, actualDigest))
            throw new SecurityException("Native AOT artifact differs from signed manifest");
    }

    private static bool IsLowerHexSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (char c in value)
            if (!(c is >= '0' and <= '9') &&
                !(c is >= 'a' and <= 'f')) return false;
        return true;
    }
}
