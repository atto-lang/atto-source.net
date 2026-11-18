using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using Wasmtime;

// Native AOT execution *probe*, not the production OresVmHost guest pathway.
// All guests here are synthetic, hard-coded WAT fixtures, NOT compiled AttoLang.
// Keep untrusted admission in OresVmHost fail-closed until a verified binary
// ABI, capability manifest, global resource budgets and lifecycle are wired.
// Wasmtime compiles Wasm to native code itself; host Native AOT != guest AOT.

// Upstream https://github.com/bytecodealliance/wasmtime-dotnet/issues/374
// documents Windows x64 CET host fail-fast on hardware-signaled Wasm traps.
// Do NOT turn CET off as a workaround or claim per-tenant fault containment.
// This opt-in native probe refuses Windows until a separate published-binary
// CET-safe trap and neighbor-survival test passes on the pinned Wasmtime.
if (OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("WASMTIME_NATIVEAOT_WINDOWS_TRAP_UNVERIFIED");
    return 78;
}

// Executed tests must use dotnet publish -r linux-x64 (or compatible RID),
// then run the published executable. JIT-host tests are insufficient.
if (RuntimeFeature.IsDynamicCodeSupported)
{
    Console.Error.WriteLine("WASMTIME_PROBE_REQUIRES_PUBLISHED_NATIVE_AOT");
    return 78;
}

using var config = new Config()
    .WithFuelConsumption(true)
    .WithMaximumStackSize(256 * 1024)
    .WithMemory64(false)
    .WithMultiMemory(false)
    .WithWasmThreads(false);
using var engine = new Engine(config);
using var linker = new Linker(engine);

// One bounded, non-shared Wasm32 memory, one private mutable global and
// zero imports; the exact i32 scalar/byte ABI is a host-probe fixture only.
// Real source guest ABI admission is still separately blocked.
const string scalarGuest = """
(module
  (memory (export "memory") 1 1)
  (global $counter (mut i32) (i32.const 0))
  (func (export "increment") (param i32) (result i32)
    global.get $counter
    local.get 0
    i32.add
    global.set $counter
    global.get $counter)
  (func (export "write_byte") (param i32)
    i32.const 0
    local.get 0
    i32.store8)
  (func (export "read_byte") (result i32)
    i32.const 0
    i32.load8_u)
  (func (export "out_of_bounds") (result i32)
    i32.const 65536
    i32.load)
  (func (export "spin_forever")
    (loop (br 0)))
)
""";
// Authorize only this known, in-repository *synthetic* fixture for the probe.
 // Do not treat an expected digest supplied by a tenant as authorization.
byte[] trustedWasm = Module.ConvertText(scalarGuest);
byte[] approvedDigest = SHA256.HashData(trustedWasm);
using var module = TrustedProbeModuleAdmission.Load(engine, trustedWasm, approvedDigest);
RequireNoImports(module);

// Regression gates deliberately exercise the admission function, not only
// validation of a known-good Module produced directly by the test host.
byte[] mismatchedDigest = (byte[])approvedDigest.Clone();
mismatchedDigest[0] ^= 0x80;
ExpectAdmissionDenied(() =>
{
    using var _ = TrustedProbeModuleAdmission.Load(engine, trustedWasm, mismatchedDigest);
}, "mismatched SHA-256 digest");

byte[] malformedHeader = (byte[])trustedWasm.Clone();
malformedHeader[0] = 0x7f;
ExpectAdmissionDenied(() =>
{
    using var _ = TrustedProbeModuleAdmission.Load(
        engine, malformedHeader, SHA256.HashData(malformedHeader));
}, "invalid Wasm header even with a matching digest");

ExpectAdmissionDenied(() =>
{
    using var _ = TrustedProbeModuleAdmission.Load(
        engine, Array.Empty<byte>(), approvedDigest);
}, "empty Wasm module");

byte[] oversizedFixture = new byte[1024 * 1024 + 1];
ExpectAdmissionDenied(() =>
{
    using var _ = TrustedProbeModuleAdmission.Load(
        engine, oversizedFixture, approvedDigest);
}, "oversized Wasm module");

byte[] importedWasm = Module.ConvertText(
    "(module (import \"env\" \"host_ptr\" (func)))");
ExpectAdmissionDenied(() =>
{
    using var _ = TrustedProbeModuleAdmission.Load(
        engine, importedWasm, SHA256.HashData(importedWasm));
}, "validated Wasm must not gain ambient host imports");

// A real Wasmtime Store/instance per tenant. A single immutable Module can
// be shared between stores without sharing its mutable guest memory/globals.
using var a = new Store(engine);
using var b = new Store(engine);
a.AddFuel(100_000);
b.AddFuel(100_000);
var ia = linker.Instantiate(a, module);
var ib = linker.Instantiate(b, module);
var incA = ia.GetFunction<int, int>("increment")
    ?? throw new Exception("missing typed tenant A increment export");
var incB = ib.GetFunction<int, int>("increment")
    ?? throw new Exception("missing typed tenant B increment export");
var writeA = ia.GetAction<int>("write_byte")
    ?? throw new Exception("missing typed tenant A memory write export");
var readB = ib.GetFunction<int>("read_byte")
    ?? throw new Exception("missing typed tenant B memory read export");

Require(incA(7) == 7, "tenant A initial global");
Require(incA(3) == 10, "tenant A updated global");
Require(incB(42) == 42, "tenant B independent initial global");
writeA(123);
Require(readB() == 0, "cross-tenant linear-memory leak");

var failA = ia.GetFunction<int>("out_of_bounds")
    ?? throw new Exception("missing out-of-bounds export");
ExpectTrap(() => _ = failA(), "out-of-bounds tenant A");
Require(incB(1) == 43, "neighbor B survived A memory trap");

// Fresh Store, fresh global and memory, even with the same immutable module.
using var restart = new Store(engine);
restart.AddFuel(100_000);
var ir = linker.Instantiate(restart, module);
var incRestart = ir.GetFunction<int, int>("increment")
    ?? throw new Exception("missing restart export");
Require(incRestart(1) == 1, "restarted tenant inherited stale state");

// Explicitly deny all guest imports, even when the host would be able to
// satisfy one. The test is not evidence of general WASI capability checks.
using var bad = Module.FromText(engine, "untrusted-import",
    "(module (import \"env\" \"host_ptr\" (func)))");
var rejected = false;
try { RequireNoImports(bad); }
catch (SecurityException) { rejected = true; }
Require(rejected, "import allowlist gate incorrectly admitted a host call");

// A separate store gets a tiny budget for adversarial guest spinning.
// The host remains responsive without relying on an unbounded deadline.
using var spinStore = new Store(engine);
spinStore.AddFuel(1000);
var spinInstance = linker.Instantiate(spinStore, module);
var spin = spinInstance.GetAction("spin_forever")
    ?? throw new Exception("missing spin export");
ExpectTrap(spin, "infinite loop fuel exhausted");
Require(incB(1) == 44, "neighbor B survived fuel exhaustion");

// Exercise the new generation-fenced lifecycle on real Wasmtime Stores.
// This stays a synthetic probe, not OresVmHost production untrusted admission.
using (var registry = new TenantStoreRegistry(engine, module, maxTenants: 2))
{
    var tenantA = registry.Create("tenant-A");
    var tenantB = registry.Create("tenant-B");
    Require(registry.Increment(tenantA, 7) == 7, "registry tenant A initial state");
    Require(registry.Increment(tenantB, 42) == 42, "registry tenant B initial state");
    registry.WriteByte(tenantA, 81);
    Require(registry.ReadByte(tenantB) == 0, "registry cross-tenant memory separation");

    var rejectedForeign = false;
    using (var foreign = new TenantStoreRegistry(engine, module, maxTenants: 1))
    {
        try { _ = foreign.Increment(tenantA, 1); }
        catch (InvalidOperationException) { rejectedForeign = true; }
    }
    Require(rejectedForeign, "foreign host handle incorrectly admitted");

    // A guest fault automatically closes that tenant's admission. It must
    // never consume the neighbor's Store or block B's continued execution.
    ExpectTrap(() => registry.TriggerBoundsTrap(tenantA), "registry bounds trap");
    Require(registry.Increment(tenantB, 1) == 43, "registry neighbor after trap");
    registry.CompleteShutdown(tenantA);
    registry.Destroy(tenantA);
    var refusedStale = false;
    try { _ = registry.Increment(tenantA, 1); }
    catch (InvalidOperationException) { refusedStale = true; }
    Require(refusedStale, "destroyed generation handle was reused");

    var tenantA2 = registry.Create("tenant-A", initialFuel: 1000);
    Require(registry.Increment(tenantA2, 1) == 1, "fresh Store did not reset globals");
    Require(registry.ReadByte(tenantA2) == 0, "fresh Store did not reset memory");
    ExpectTrap(() => registry.TriggerFuelTrap(tenantA2), "registry fuel trap");
    registry.CompleteShutdown(tenantA2);
    registry.Destroy(tenantA2);
    Require(registry.Increment(tenantB, 1) == 44, "neighbor after fuel trap");
    registry.BeginShutdown(tenantB);
    registry.CompleteShutdown(tenantB);
    registry.Destroy(tenantB);
}
var importedRegistryDenied = false;
try { using var invalid = new TenantStoreRegistry(engine, bad); }
catch (SecurityException) { importedRegistryDenied = true; }
Require(importedRegistryDenied, "registry incorrectly accepted imported host capability");

Console.WriteLine("NET_WASMTIME_NATIVEAOT_PROBE_PASS");
return 0;

static void Require(bool condition, string why)
{
    if (!condition) throw new Exception("WASMTIME_PROBE_FAILED: " + why);
}

static void RequireNoImports(Module module)
{
    if (module.Imports.Count != 0)
        throw new SecurityException("Wasm guest imports denied");
}

static void ExpectAdmissionDenied(Action action, string why)
{
    try { action(); }
    catch (SecurityException) { return; }
    throw new Exception("WASMTIME_PROBE_FAILED: admitted " + why);
}

static void ExpectTrap(Action action, string why)
{
    try { action(); }
    catch (TrapException) { return; }
    throw new Exception("WASMTIME_PROBE_FAILED: missing Wasm trap: " + why);
}
