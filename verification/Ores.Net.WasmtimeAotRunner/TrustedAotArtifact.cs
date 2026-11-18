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
        // Do not call File.ReadAllBytes after a separate FileInfo.Length stat:
        // an attacker can grow the file between those operations and make
        // ReadAllBytes allocate far beyond the claimed resource limit.
        //
        // Keep one file handle from start to finish, reject non-seekable
        // inputs, and never read more than maxLength + 1 bytes regardless
        // of concurrent modifications. The later signature/digest check
        // authenticates the exact in-memory bytes supplied to Wasmtime.
        if (maxLength < 1 || maxLength > MaxArtifactBytes)
            throw new ArgumentOutOfRangeException(nameof(maxLength));

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, options: FileOptions.SequentialScan);
        if (!stream.CanSeek || stream.Length < 1 || stream.Length > maxLength)
            throw new SecurityException("AOT artifact exceeds its admission limit");

        using var output = new MemoryStream(capacity: (int)stream.Length);
        byte[] buffer = new byte[Math.Min(8192, maxLength + 1)];
        while (true)
        {
            int remaining = maxLength - checked((int)output.Length);
            int toRead = Math.Min(buffer.Length, remaining + 1);
            int count = stream.Read(buffer.AsSpan(0, toRead));
            if (count == 0) break;
            if (count > remaining)
                throw new SecurityException("AOT artifact grew during bounded read");
            output.Write(buffer.AsSpan(0, count));
        }
        if (output.Length == 0)
            throw new SecurityException("Empty AOT artifact");
        return output.ToArray();
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
        try
        {
            verifier.ImportFromPem(trustedPublicKeyPem);
            if (verifier.KeySize < 3072 ||
                !verifier.VerifyData(manifestBytes, signature,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new SecurityException("AOT manifest signer not trusted");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new SecurityException("Invalid trusted AOT signer key", ex);
        }

        // Parse ONLY after verifying signature; reject duplicated or unknown
        // JSON properties so a second parser cannot reinterpret policy.
        // Set a low maximum depth; this schema has no nested values.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        }
        catch (JsonException ex)
        {
            throw new SecurityException("Invalid signed AOT manifest JSON", ex);
        }
        using (doc)
        {
        try
        {
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
        catch (Exception ex) when (ex is InvalidOperationException or
            FormatException or OverflowException)
        {
            throw new SecurityException("Malformed signed AOT policy metadata", ex);
        }
        }
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
