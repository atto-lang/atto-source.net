using System.Security;
using System.Runtime.CompilerServices;
using Wasmtime;

// Experimental real-Store lifecycle; trusted synthetic module only.
// DO NOT substitute this for OresVmHost's unavailable WasmtimeSandbox mode.
// Actual Atto guest ABI validation, aggregate committed-memory limits,
// host I/O broker and Windows CET trap safety remain hard admission blockers.
internal sealed class TenantStoreRegistry : IDisposable
{
    internal sealed class Handle
    {
        internal Handle(TenantStoreRegistry issuer, string id, ulong generation)
        { Issuer = issuer; Id = id; Generation = generation; }
        internal TenantStoreRegistry Issuer { get; }
        internal string Id { get; }
        internal ulong Generation { get; }
    }

    private enum Phase { Running, Quiescing, Stopped }
    private sealed class Entry
    {
        internal required Handle Handle;
        internal required Store Store;
        internal required Instance Instance;
        internal readonly object Execution = new();
        internal Phase Phase = Phase.Running;
        internal int ActiveCalls;
    }

    private readonly object _gate = new();
    private readonly Engine _engine;
    private readonly Module _module;
    private readonly Linker _linker;
    private readonly Dictionary<string, Entry> _tenants = new(StringComparer.Ordinal);
    private readonly int _maxTenants;
    private ulong _nextGeneration = 1;
    private bool _disposed;

    internal TenantStoreRegistry(Engine engine, Module module, int maxTenants = 4)
    {
        // Do not let a new caller bypass Program's Windows CET fail-closed
        // check or silently switch the proof from Native AOT to CoreCLR JIT.
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Wasmtime CET trap recovery not certified");
        if (RuntimeFeature.IsDynamicCodeSupported)
            throw new PlatformNotSupportedException("Wasmtime tenant probe requires a published Native AOT host");
        // Defense in depth: do not permit direct callers of the registry to
        // bypass the synthetic-only CLI consent guard. This environment flag
        // is not a production security authorization or sandbox boundary.
        if (Environment.GetEnvironmentVariable("ORES_WASMTIME_SYNTHETIC_CI_ONLY") != "1")
            throw new SecurityException("Wasmtime 50.0.1 registry is synthetic-CI-only");
        if (maxTenants is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maxTenants));
        if (module.Imports.Count != 0)
            throw new SecurityException("untrusted Wasm must not import host APIs");
        _engine = engine;
        _module = module;
        _linker = new Linker(engine); // Empty by construction: zero host functions.
        _maxTenants = maxTenants;
    }

    // One real Store and instance per tenant, under a fixed initial fuel cap.
    internal Handle Create(string id, ulong initialFuel = 100_000)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Length > 128 ||
            !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new ArgumentException("invalid tenant identity", nameof(id));
        if (initialFuel is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(initialFuel));
        lock (_gate)
        {
            EnsureOpen();
            if (_tenants.ContainsKey(id)) throw new InvalidOperationException("duplicate tenant");
            if (_tenants.Count >= _maxTenants) throw new InvalidOperationException("tenant quota");
            if (_nextGeneration == ulong.MaxValue) throw new InvalidOperationException("generation exhausted");
            var store = new Store(_engine);
            try
            {
                store.AddFuel(initialFuel); // Also bounds guest start functions.
                var instance = _linker.Instantiate(store, _module);
                var memory = instance.GetMemory("memory")
                    ?? throw new SecurityException("required bounded memory export missing");
                if (memory.Is64Bit || memory.Maximum != 1)
                    throw new SecurityException("tenant memory must have exact one-page maximum");
                // Admit only the exact deliberately narrow synthetic test ABI.
                if (instance.GetFunction<int, int>("increment") is null)
                    throw new SecurityException("required typed increment export missing");
                var handle = new Handle(this, id, _nextGeneration);
                _tenants.Add(id, new Entry { Handle = handle, Store = store, Instance = instance });
                ++_nextGeneration;
                return handle;
            }
            catch { store.Dispose(); throw; }
        }
    }

    internal int Increment(Handle handle, int delta) =>
        Invoke(handle, i => (i.GetFunction<int, int>("increment")
            ?? throw new SecurityException("missing increment export"))(delta));

    internal void WriteByte(Handle handle, int value) =>
        Invoke(handle, i => { (i.GetAction<int>("write_byte")
            ?? throw new SecurityException("missing write export"))(value); return 0; });

    internal int ReadByte(Handle handle) =>
        Invoke(handle, i => (i.GetFunction<int>("read_byte")
            ?? throw new SecurityException("missing read export"))());

    internal void TriggerBoundsTrap(Handle handle) =>
        Invoke(handle, i => { _ = (i.GetFunction<int>("out_of_bounds")
            ?? throw new SecurityException("missing bounds export"))(); return 0; });

    internal void TriggerFuelTrap(Handle handle) =>
        Invoke(handle, i => { (i.GetAction("spin_forever")
            ?? throw new SecurityException("missing loop export"))(); return 0; });

    private T Invoke<T>(Handle handle, Func<Instance, T> body)
    {
        Entry entry;
        lock (_gate)
        {
            entry = Checked(handle);
            if (entry.Phase != Phase.Running) throw new InvalidOperationException("tenant admission closed");
            ++entry.ActiveCalls; // physical execution pin prevents shutdown/free
        }
        try
        {
            // Wasmtime Store instances are not concurrently reentered.
            // Other tenants retain independent locks and may execute concurrently.
            lock (entry.Execution)
            {
                lock (_gate)
                    if (entry.Phase != Phase.Running)
                        throw new InvalidOperationException("queued guest admission cancelled");
                try { return body(entry.Instance); }
                catch (Exception)
                {
                    // Any failure while entering/executing a guest closes
                    // its admission. Only its owner may drain and dispose it;
                    // the neighbor's Store must remain independent.
                    lock (_gate) entry.Phase = Phase.Quiescing;
                    throw;
                }
            }
        }
        finally
        {
            lock (_gate) --entry.ActiveCalls;
        }
    }

    internal void BeginShutdown(Handle handle)
    {
        lock (_gate)
        {
            var e = Checked(handle);
            if (e.Phase != Phase.Running) throw new InvalidOperationException("shutdown phase");
            e.Phase = Phase.Quiescing;
        }
    }

    internal void CompleteShutdown(Handle handle)
    {
        lock (_gate)
        {
            var e = Checked(handle);
            if (e.Phase != Phase.Quiescing || e.ActiveCalls != 0)
                throw new InvalidOperationException("guest calls still active or shutdown not started");
            e.Phase = Phase.Stopped;
        }
    }

    internal void Destroy(Handle handle)
    {
        lock (_gate)
        {
            var e = Checked(handle);
            if (e.Phase != Phase.Stopped || e.ActiveCalls != 0)
                throw new InvalidOperationException("tenant not drained");
            _tenants.Remove(handle.Id);
            e.Store.Dispose(); // physical store teardown; no references escape this class
        }
    }

    private Entry Checked(Handle handle)
    {
        EnsureOpen();
        if (handle is null || !ReferenceEquals(handle.Issuer, this) ||
            !_tenants.TryGetValue(handle.Id, out var e) ||
            !ReferenceEquals(e.Handle, handle) || e.Handle.Generation != handle.Generation)
            throw new InvalidOperationException("foreign, stale or destroyed tenant");
        return e;
    }

    private void EnsureOpen()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TenantStoreRegistry));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_tenants.Values.Any(e => e.ActiveCalls != 0))
                throw new InvalidOperationException("cannot dispose with live guest calls");
            foreach (var e in _tenants.Values) e.Store.Dispose();
            _tenants.Clear();
            _disposed = true;
            _linker.Dispose();
        }
    }
}
