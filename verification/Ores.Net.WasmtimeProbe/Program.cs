using System.Runtime.CompilerServices;
using System.Security;
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
    (loop br 0))
)
""";
using var module = Module.FromText(engine, "probe-scalar", scalarGuest);
RequireNoImports(module);

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

static void ExpectTrap(Action action, string why)
{
    try { action(); }
    catch (TrapException) { return; }
    throw new Exception("WASMTIME_PROBE_FAILED: missing Wasm trap: " + why);
}
