using System.Security.Cryptography;
using Wasmtime;

// This is a *candidate* Wasmtime transport, NOT the shipping TenantVM.
// C# -> Wasmtime.Dotnet -> P/Invoke -> libwasmtime C ABI -> validated wasm.
// No guest CIL, AssemblyLoadContext, native pointers, WASI, or host imports.
// The Wasm fixture is not AttoLang source compiler lowering.
const string counterWat = """
    (module
      (memory 1 2)
      (func (export "write") (param i32)
        i32.const 0
        local.get 0
        i32.store)
      (func (export "read") (result i32)
        i32.const 0
        i32.load)
      (func (export "fault") (result i32)
        i32.const 131072
        i32.load)
      (func (export "spin")
        (loop $again (br $again))))
    """;

using var config = new Config()
    .WithFuelConsumption(true)
    .WithMaximumStackSize(64 * 1024);
using var engine = new Engine(config);

// Only for this developer fixture: convert WAT to Wasm. A production
// admission path must take verified compiler-emitted Wasm, NOT source WAT
// or precompiled native modules supplied by an untrusted tenant.
byte[] wasm = Module.ConvertText(counterWat);
byte[] expectedDigest = SHA256.HashData(wasm);
using var module = WasmtimeTenantSession.LoadCheckedModule(
    engine, wasm, expectedDigest);

using var alpha = new WasmtimeTenantSession(engine, module, fuel: 50_000);
using var beta = new WasmtimeTenantSession(engine, module, fuel: 50_000);
Require(alpha.Read() == 0 && beta.Read() == 0,
    "fresh generations must not share writable linear memory");
alpha.Write(41);
Require(alpha.Read() == 41 && beta.Read() == 0,
    "a tenant must not observe a neighbor's mutable Wasm memory");
beta.Write(7);
Require(alpha.Read() == 41 && beta.Read() == 7,
    "both tenants must preserve independent state");

Expect<WasmtimeException>(alpha.Fault, "out-of-bounds guest memory trap");
Expect<InvalidOperationException>(() => _ = alpha.Read(),
    "a trapped tenant must be retired, never resumed");
Require(beta.Read() == 7, "the neighbor must survive an OOB trap");

using (var replacement = new WasmtimeTenantSession(engine, module, fuel: 50_000))
{
    Require(replacement.Read() == 0, "a new generation must start clean");
    Expect<WasmtimeException>(replacement.Spin, "fuel must terminate a spin");
    Expect<InvalidOperationException>(() => _ = replacement.Read(),
        "fuel-exhausted generation must be retired");
}
Require(beta.Read() == 7, "neighbor remains available after fuel trap");

byte[] badDigest = (byte[])expectedDigest.Clone();
badDigest[0] ^= 0x80;
Expect<InvalidDataException>(() =>
{
    using var _ = WasmtimeTenantSession.LoadCheckedModule(engine, wasm, badDigest);
}, "untrusted mismatched module digest");

// Import-policy check: even a valid Wasm module requesting host functions is
// denied, not given an implicit WASI, filesystem, env, net, or FFI capability.
using var withImport = Module.FromText(engine, "forbidden-import", """
    (module (import "env" "borrow_host_memory" (func)))
    """);
Expect<InvalidOperationException>(() =>
{
    using var _ = new WasmtimeTenantSession(engine, withImport, fuel: 10_000);
}, "module imports must be explicitly denied");

Console.WriteLine("Wasmtime TenantVM C-ABI candidate: smoke PASS (not release certification)");

static void Require(bool condition, string why)
{
    if (!condition) throw new Exception("FAIL: " + why);
}

static void Expect<T>(Action action, string why) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("FAIL: expected " + typeof(T).Name + ": " + why);
}

// Each instance owns an independent Wasmtime Store. An Engine/Module may be
// shared as immutable code, but Store, linear memory and guest globals may not.
// The lock serializes access because Wasmtime stores cannot be entered from
// multiple threads simultaneously; a future scheduler must use actor turns.
internal sealed class WasmtimeTenantSession : IDisposable
{
    private readonly object gate = new();
    private readonly Store store;
    private readonly Linker linker;
    private readonly Action<int> write;
    private readonly Func<int> read;
    private readonly Func<int> fault;
    private readonly Action spin;
    private bool retired;

    public static Module LoadCheckedModule(
        Engine engine, ReadOnlySpan<byte> wasm, ReadOnlySpan<byte> expectedSha256)
    {
        if (wasm.IsEmpty || wasm.Length > 1024 * 1024 ||
            expectedSha256.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(wasm), expectedSha256))
            throw new InvalidDataException("No admitted Wasm module with this digest.");

        // FromBytes validates Wasm but may compile machine code at runtime.
        // NativeAOT host deployment alone is NOT a zero-JIT guest guarantee.
        // The strict AOT release path needs trusted build-time compilation,
        // authenticated artifacts, and an independently reviewed loader.
        var module = Module.FromBytes(engine, "tenant-fixture", wasm);
        if (module.Imports.Count != 0)
        {
            module.Dispose();
            throw new InvalidOperationException("Untrusted guest imports are denied.");
        }
        return module;
    }

    public WasmtimeTenantSession(Engine engine, Module module, ulong fuel)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(module);
        if (fuel == 0 || fuel > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(fuel));
        if (module.Imports.Count != 0)
            throw new InvalidOperationException("Untrusted guest imports are denied.");

        store = new Store(engine);
        linker = new Linker(engine);
        try
        {
            store.AddFuel(fuel);
            var instance = linker.Instantiate(store, module);
            write = instance.GetAction<int>("write")
                ?? throw new InvalidOperationException("Missing typed write export.");
            read = instance.GetFunction<int>("read")
                ?? throw new InvalidOperationException("Missing typed read export.");
            fault = instance.GetFunction<int>("fault")
                ?? throw new InvalidOperationException("Missing typed fault export.");
            spin = instance.GetAction("spin")
                ?? throw new InvalidOperationException("Missing typed spin export.");
        }
        catch
        {
            linker.Dispose();
            store.Dispose();
            throw;
        }
    }

    public void Write(int value) => Invoke(() => { write(value); return 0; });
    public int Read() => Invoke(read);
    public void Fault() => Invoke(fault);
    public void Spin() => Invoke(() => { spin(); return 0; });

    private T Invoke<T>(Func<T> call)
    {
        lock (gate)
        {
            if (retired) throw new InvalidOperationException("Tenant generation retired.");
            try { return call(); }
            catch (WasmtimeException)
            {
                // Fail closed on guest traps, exhausted fuel, and runtime errors.
                // A future VM host must also revoke capabilities and actor handles.
                retired = true;
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (retired)
            {
                // Even a trapped tenant owns native Store and Linker resources.
                // Release them on every path, including after its first fault.
            }
            retired = true;
            linker.Dispose();
            store.Dispose();
        }
    }
}
