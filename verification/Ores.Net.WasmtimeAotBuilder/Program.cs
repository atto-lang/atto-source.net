using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Wasmtime;

// TRUSTED BUILD-TIME ONLY. The output is a synthetic WebAssembly fixture,
// not compiled AttoLang. Never deploy this builder in an untrusted tenant host.
// Both this builder and the separate runtime host are compiled using .NET
// Native AOT, while Wasmtime compilation runs ONLY during this build stage.
//
// This build fixture generates an ephemeral signing key and writes only the
// PUBLIC key. It proves signature verification plumbing, NOT a production
// trust anchor. Production signed releases must use an externally managed
// signing key, pinned/verifiable identity and independently approved CI.
if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: oresnet-wasmtime-aot-builder <trusted-test-output-directory>");
    return 64;
}

string dir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(dir);
var output = new DirectoryInfo(dir);
// A release builder must never overwrite artifacts from an earlier attempt
// or follow a final-directory symlink to an attacker-controlled location.
// Production deployment also needs a protected, isolated build workspace.
if ((output.Attributes & FileAttributes.ReparsePoint) != 0 ||
    Directory.EnumerateFileSystemEntries(dir).Any())
    throw new IOException("Trusted AOT output directory must be a fresh, empty, non-symlink directory");

const string wat = """
(module
  (memory (export "memory") 1 1)
  (global $counter (mut i32) (i32.const 0))
  (func (export "increment") (param i32) (result i32)
    global.get $counter
    local.get 0
    i32.add
    global.set $counter
    global.get $counter)
  (func (export "out_of_bounds") (result i32)
    i32.const 65536
    i32.load)
  (func (export "spin_forever") (loop (br 0)))
)
""";

using var config = new Config()
    .WithFuelConsumption(true)
    .WithMaximumStackSize(256 * 1024)
    .WithMemory64(false)
    .WithReferenceTypes(false) // no GC/reference-type imports or host-provided GC refs
    .WithMultiMemory(false)
    .WithWasmThreads(false);
using var engine = new Engine(config);
using var module = Module.FromText(engine, "trusted-aot-fixture", wat);
if (module.Imports.Count != 0)
    throw new InvalidDataException("Fixture unexpectedly requires host imports");

// COMPILATION HAPPENS HERE, never in the NativeAOT runtime program.
byte[] nativeArtifact = module.Serialize();
if (nativeArtifact.Length < 16 || nativeArtifact.Length > 16 * 1024 * 1024)
    throw new InvalidDataException("Unexpected native artifact size");

string artifactSha256 = Convert.ToHexString(
    SHA256.HashData(nativeArtifact)).ToLowerInvariant();
string sourceSha256 = Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(wat))).ToLowerInvariant();
string rid = RuntimeInformation.RuntimeIdentifier;

// Exact field names and order are intentional. Signature covers the literal
// manifest bytes; no ad-hoc JSON canonicalization at the verifier.
string manifest =
    "{\n" +
    "  \"schema_version\": 1,\n" +
    "  \"guest_kind\": \"synthetic-ci-only\",\n" +
    "  \"wasmtime_version\": \"50.0.1\",\n" +
    "  \"target_rid\": \"" + rid + "\",\n" +
    "  \"engine_profile\": \"fuel-stack262144-mem64off-reftypesoff-multimemoryoff-threadsoff\",\n" +
    "  \"guest_abi\": \"atto-tenantvm-test-i32-v0\",\n" +
    "  \"source_sha256\": \"" + sourceSha256 + "\",\n" +
    "  \"artifact_sha256\": \"" + artifactSha256 + "\",\n" +
    "  \"artifact_size\": " + nativeArtifact.Length + "\n" +
    "}\n";
byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest);
using var signingKey = RSA.Create(3072);
byte[] signature = signingKey.SignData(
    manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

// The private key is NEVER serialized, logged or copied to output.
WriteNew("guest.cwasm", nativeArtifact);
WriteNew("guest.manifest.json", manifestBytes);
WriteNew("guest.manifest.sig", signature);
WriteNew("ephemeral-ci-public.pem", Encoding.UTF8.GetBytes(
    signingKey.ExportSubjectPublicKeyInfoPem()));

// These create-new writes are intentionally single-purpose for the trusted
// synthetic CI builder. The file name is constant and never guest-controlled.
void WriteNew(string name, byte[] bytes)
{
    using var file = new FileStream(
        Path.Combine(dir, name), FileMode.CreateNew, FileAccess.Write,
        FileShare.None);
    file.Write(bytes);
    file.Flush(flushToDisk: true);
}

Console.WriteLine("NET_WASMTIME_TRUSTED_AOT_BUILDER_PASS");
return 0;
