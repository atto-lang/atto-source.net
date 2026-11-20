using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using Wasmtime;

// Full-AOT execution *candidate*: published .NET Native AOT binary, and a
// separately Wasmtime-precompiled guest. This executable contains no calls
// to any Wasm module compiler API.
//
// NOT production: the public key is a command-line fixture input and is NOT
// a pinned organizational signing identity. The builder generates an
// ephemeral CI key; untrusted guest admission MUST remain disabled.
if (OperatingSystem.IsWindows())
{
    // An unresolved upstream Windows CET hardware-trap problem can crash
    // every tenant sharing this process. No disabling CET or silent bypass.
    Console.Error.WriteLine("WASMTIME_STRICT_AOT_WINDOWS_CET_UNVERIFIED");
    return 78;
}
if (RuntimeFeature.IsDynamicCodeSupported)
{
    Console.Error.WriteLine("WASMTIME_STRICT_AOT_REQUIRES_PUBLISHED_NATIVE_AOT");
    return 78;
}
// Deliberate test-only opt-in. Wasmtime 50.0.1 carries fixed-in-50.0.2
// upstream advisories, and this CLI accepts an ephemeral test trust key.
// Never allow it to be mistaken for a production untrusted tenant service.
if (Environment.GetEnvironmentVariable("ORES_WASMTIME_SYNTHETIC_CI_ONLY") != "1")
{
    Console.Error.WriteLine("WASMTIME_SYNTHETIC_PROBE_NOT_AUTHORIZED");
    return 78;
}
if (args.Length != 4)
{
    Console.Error.WriteLine(
        "Usage: oresnet-wasmtime-aot-runner <signed-manifest> <signature> <cwasm> <trusted-CI-public-key>");
    return 64;
}

// A bounded-read regression that executes in the published NativeAOT runner.
// Synthetic files are created privately in the runner temp directory: this is
// NOT an attacker-controlled Oreslang guest admission path.
string boundedReadFixture = Path.Combine(
    Path.GetTempPath(), "oresnet-aot-read-" + Guid.NewGuid().ToString("N"));
try
{
    File.WriteAllBytes(boundedReadFixture, new byte[65]);
    ExpectRefused(
        () => _ = TrustedAotArtifact.ReadBoundedFile(boundedReadFixture, 64),
        "oversized signed-artifact input");
    File.WriteAllBytes(boundedReadFixture, Array.Empty<byte>());
    ExpectRefused(
        () => _ = TrustedAotArtifact.ReadBoundedFile(boundedReadFixture, 64),
        "empty signed-artifact input");
    bool deniedInvalidLimit = false;
    try { _ = TrustedAotArtifact.ReadBoundedFile(boundedReadFixture, 0); }
    catch (ArgumentOutOfRangeException) { deniedInvalidLimit = true; }
    Require(deniedInvalidLimit, "invalid artifact read limit rejected");
}
finally
{
    File.Delete(boundedReadFixture);
}

byte[] manifest = TrustedAotArtifact.ReadBoundedFile(args[0], 4096);
byte[] signature = TrustedAotArtifact.ReadBoundedFile(args[1], 1024);
byte[] artifact = TrustedAotArtifact.ReadBoundedFile(
    args[2], TrustedAotArtifact.MaxArtifactBytes);
string ephemeralCiVerifier = System.Text.Encoding.UTF8.GetString(
    TrustedAotArtifact.ReadBoundedFile(args[3], 8192));

// File admission must remain genuinely bounded even if the file changes
// size after an initial stat. The one-handle reader must not use
// File.ReadAllBytes or allocate an attacker-selected amount of memory.
string admissionFixture = Path.Combine(
    Path.GetTempPath(), "atto-aot-bounded-" + Guid.NewGuid().ToString("N"));
try
{
    File.WriteAllBytes(admissionFixture, new byte[] { 1, 2, 3, 4 });
    Require(TrustedAotArtifact.ReadBoundedFile(admissionFixture, 4)
        .AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
        "bounded file reader changed valid bytes");
    ExpectRefused(() => _ = TrustedAotArtifact.ReadBoundedFile(admissionFixture, 3),
        "oversized file before native artifact deserialization");
    File.WriteAllBytes(admissionFixture, Array.Empty<byte>());
    ExpectRefused(() => _ = TrustedAotArtifact.ReadBoundedFile(admissionFixture, 4),
        "empty file before native artifact deserialization");
}
finally
{
    if (File.Exists(admissionFixture)) File.Delete(admissionFixture);
}

// All of these must reject before Wasmtime ever receives an artifact. A
// malicious serialized native-code blob is not safely validated by Wasmtime.
TrustedAotArtifact.Verify(manifest, signature, artifact, ephemeralCiVerifier);

ExpectRefused(() => TrustedAotArtifact.Verify(
    manifest, signature, artifact, "not-a-public-key"),
    "malformed PEM public key before native-code admission");

byte[] swappedArtifact = (byte[])artifact.Clone();
swappedArtifact[0] ^= 1;
ExpectRefused(() => TrustedAotArtifact.Verify(
    manifest, signature, swappedArtifact, ephemeralCiVerifier),
    "same signed manifest with a modified native-code artifact");

byte[] changedManifest = (byte[])manifest.Clone();
changedManifest[0] ^= 1;
ExpectRefused(() => TrustedAotArtifact.Verify(
    changedManifest, signature, artifact, ephemeralCiVerifier),
    "tampered signed manifest");

byte[] badSignature = (byte[])signature.Clone();
badSignature[0] ^= 1;
ExpectRefused(() => TrustedAotArtifact.Verify(
    manifest, badSignature, artifact, ephemeralCiVerifier),
    "altered RSA-PSS signature");

using (var wrongIssuer = RSA.Create(3072))
{
    ExpectRefused(() => TrustedAotArtifact.Verify(
        manifest, signature, artifact, wrongIssuer.ExportSubjectPublicKeyInfoPem()),
        "untrusted signing identity");
}

// Re-sign *malformed metadata* with a fresh test issuer to prove that
// correct cryptography alone does not override the ABI/feature/RID policy.
// These ephemeral local keys are never a production trust authority.
string signedJson = System.Text.Encoding.UTF8.GetString(manifest);
using (var metadataSigner = RSA.Create(3072))
{
    string signedByLocalTestKey = metadataSigner.ExportSubjectPublicKeyInfoPem();
    void DenySignedMetadata(string changed, string reason)
    {
        if (changed == signedJson)
            throw new Exception("Malformed metadata test fixture did not change: " + reason);
        byte[] content = System.Text.Encoding.UTF8.GetBytes(changed);
        byte[] proof = metadataSigner.SignData(content,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        ExpectRefused(() => TrustedAotArtifact.Verify(
            content, proof, artifact, signedByLocalTestKey), reason);
    }

    DenySignedMetadata(
        signedJson.Replace("\"target_rid\":", "\"target_rid\": \"wrong-arch\", \"unused\":", StringComparison.Ordinal),
        "unapproved target architecture or unknown JSON property");
    DenySignedMetadata(
        signedJson.Replace("\"wasmtime_version\": \"50.0.1\"",
            "\"wasmtime_version\": \"unknown-native-engine\"", StringComparison.Ordinal),
        "signed artifact from unapproved Wasmtime native engine");
    DenySignedMetadata(
        signedJson.Replace("\"guest_kind\": \"synthetic-ci-only\"",
            "\"guest_kind\": \"production-untrusted\"", StringComparison.Ordinal),
        "synthetic test signer cannot authorize production guest");
    DenySignedMetadata(
        signedJson.Replace("\"engine_profile\": \"fuel-stack262144-mem64off-reftypesoff-multimemoryoff-threadsoff\"",
            "\"engine_profile\": \"unapproved-engine-features\"", StringComparison.Ordinal),
        "signed but incompatible Wasmtime engine profile");
    DenySignedMetadata(
        signedJson.Replace("\"guest_abi\": \"atto-tenantvm-test-i32-v0\"",
            "\"guest_abi\": \"unapproved-guest-abi\"", StringComparison.Ordinal),
        "signed but incompatible actor entry ABI");
    DenySignedMetadata(
        signedJson.Replace("\"schema_version\": 1,",
            "\"schema_version\": [],", StringComparison.Ordinal),
        "signed schema_version with invalid JSON type");
    DenySignedMetadata(
        signedJson.Replace("\"artifact_size\": " + artifact.Length,
            "\"artifact_size\": \"not-a-number\"", StringComparison.Ordinal),
        "signed artifact_size with invalid JSON type");
    DenySignedMetadata(
        signedJson.Replace("\"schema_version\": 1,",
            "\"schema_version\": 1, \"guest_kind\": \"duplicated\",", StringComparison.Ordinal),
        "duplicated signed JSON key");
    DenySignedMetadata(
        signedJson.Replace("\"source_sha256\": \"",
            "\"source_sha256\": \"bad-", StringComparison.Ordinal),
        "invalid compiler source identity");
}

using var config = new Config()
    .WithFuelConsumption(true)
    .WithMaximumStackSize(256 * 1024)
    .WithMemory64(false)
    .WithReferenceTypes(false) // match signed precompile profile; deny GC reference types
    .WithMultiMemory(false)
    .WithWasmThreads(false);
using var engine = new Engine(config);

// Deserialize only the exact authenticated in-memory native code bytes.
// Never call DeserializeFile(path): a file could change after verification.
// No Wasm->native compilation is invoked anywhere in this runtime source.
using var module = Module.Deserialize(engine, "signed-synthetic-ci-guest", artifact);
if (module.Imports.Count != 0)
    throw new SecurityException("Signed native artifact requested host imports");

// Two independent Stores and instances. Immutable compiled module may be
// shared; tenant-writable memory and globals must never be shared.
using var linker = new Linker(engine); // NO WASI, FFI or host-defined imports.
using var a = new Store(engine);
using var b = new Store(engine);
a.AddFuel(100_000);
b.AddFuel(100_000);
var tenantA = linker.Instantiate(a, module);
var tenantB = linker.Instantiate(b, module);

var ma = tenantA.GetMemory("memory");
var mb = tenantB.GetMemory("memory");
if (ma is null || mb is null || ma.Is64Bit || mb.Is64Bit ||
    ma.Maximum != 1 || mb.Maximum != 1)
    throw new SecurityException("Signed fixture memory schema not admitted");

var incA = tenantA.GetFunction<int, int>("increment")
    ?? throw new SecurityException("Missing verified increment export");
var incB = tenantB.GetFunction<int, int>("increment")
    ?? throw new SecurityException("Missing verified increment export");
Require(incA(9) == 9, "tenant A independent mutable global");
Require(incB(4) == 4, "tenant B independent mutable global");
Require(incA(1) == 10 && incB(1) == 5, "tenant globals remained isolated");

// A mutable Wasm linear-memory witness across independently allocated Stores.
// Isolated counter globals alone are insufficient evidence of memory isolation.
var writeA = tenantA.GetAction<int>("write_byte")
    ?? throw new SecurityException("Missing signed memory-write export");
var writeB = tenantB.GetAction<int>("write_byte")
    ?? throw new SecurityException("Missing signed memory-write export");
var readA = tenantA.GetFunction<int>("read_byte")
    ?? throw new SecurityException("Missing signed memory-read export");
var readB = tenantB.GetFunction<int>("read_byte")
    ?? throw new SecurityException("Missing signed memory-read export");
Require(readA() == 0 && readB() == 0, "separate zeroed guest memories");
writeA(0xA1);
writeB(0xB2);
Require(readA() == 0xA1 && readB() == 0xB2, "separate linear memory writes");
writeA(0xC3);
Require(readA() == 0xC3 && readB() == 0xB2,
    "neighbor linear memory remains independent");

var oobA = tenantA.GetFunction<int>("out_of_bounds")
    ?? throw new SecurityException("Missing OOB test export");
ExpectTrap(() => _ = oobA(), "memory out-of-bounds from tenant A");
Require(incB(2) == 7, "neighbor survived tenant A memory trap");

using var spinStore = new Store(engine);
spinStore.AddFuel(1000);
var spinner = linker.Instantiate(spinStore, module).GetAction("spin_forever")
    ?? throw new SecurityException("Missing bounded spin export");
ExpectTrap(spinner, "infinite loop stopped by fuel quota");
Require(incB(1) == 8, "neighbor survived fuel exhaustion");

Console.WriteLine("NET_WASMTIME_STRICT_AOT_RUNTIME_PASS");
return 0;

static void Require(bool condition, string description)
{
    if (!condition)
        throw new Exception("STRICT_AOT_FAILED: " + description);
}

static void ExpectRefused(Action action, string description)
{
    try { action(); }
    catch (SecurityException) { return; }
    throw new Exception("STRICT_AOT_FAILED: admitted " + description);
}

static void ExpectTrap(Action action, string description)
{
    try { action(); }
    catch (TrapException) { return; }
    throw new Exception("STRICT_AOT_FAILED: no trap on " + description);
}
