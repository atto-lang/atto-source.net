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
if (args.Length != 4)
{
    Console.Error.WriteLine(
        "Usage: oresnet-wasmtime-aot-runner <signed-manifest> <signature> <cwasm> <trusted-CI-public-key>");
    return 64;
}

byte[] manifest = TrustedAotArtifact.ReadBoundedFile(args[0], 4096);
byte[] signature = TrustedAotArtifact.ReadBoundedFile(args[1], 1024);
byte[] artifact = TrustedAotArtifact.ReadBoundedFile(
    args[2], TrustedAotArtifact.MaxArtifactBytes);
string ephemeralCiVerifier = System.Text.Encoding.UTF8.GetString(
    TrustedAotArtifact.ReadBoundedFile(args[3], 8192));

// All of these must reject before Wasmtime ever receives an artifact. A
// malicious serialized native-code blob is not safely validated by Wasmtime.
TrustedAotArtifact.Verify(manifest, signature, artifact, ephemeralCiVerifier);

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

using var config = new Config()
    .WithFuelConsumption(true)
    .WithMaximumStackSize(256 * 1024)
    .WithMemory64(false)
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
