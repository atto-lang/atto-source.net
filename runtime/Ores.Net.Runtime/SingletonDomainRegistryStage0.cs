namespace Ores.Net.Runtime;

/// <summary>
/// A bounded trusted-host stage-0 application-domain registry. Object identity
/// of the registry is the domain boundary; module/declaration strings are only
/// compiler-resolved keys within that domain, never authentication tokens.
/// No guest-facing lookup/registration API, cross-isolate transport, or
/// deep-immutability guarantee is provided.
/// </summary>
public sealed class SingletonDomainRegistryStage0<T> : IDisposable
{
    private readonly record struct Declaration(string Module, string Name);

    private sealed class Entry
    {
        internal Entry(Func<ValueTask<T>> initializer, SingletonStage0DependencyGraph graph,
            int maxPendingWaiters)
        {
            Cell = new SingletonCellStage0<T>(initializer, graph, maxPendingWaiters);
        }

        internal SingletonCellStage0<T> Cell { get; }
        internal HashSet<ActorRef> Readers { get; } = new();
    }

    private readonly ActorRuntime _runtime;
    private readonly object _gate = new();
    private readonly SingletonStage0DependencyGraph _graph;
    private readonly Dictionary<Declaration, Entry> _entries = new();
    private readonly int _maxDeclarations;
    private readonly int _maxGrantsPerDeclaration;
    private readonly int _maxPendingWaiters;
    private bool _retired;

    public SingletonDomainRegistryStage0(ActorRuntime runtime,
        int maxDeclarations = 64, int maxGrantsPerDeclaration = 256,
        int maxPendingWaiters = 128, int maxActiveInitializers = 1024,
        int maxDependencies = 4096)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (maxDeclarations is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxDeclarations));
        if (maxGrantsPerDeclaration is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxGrantsPerDeclaration));
        if (maxPendingWaiters is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxPendingWaiters));
        _runtime = runtime;
        _maxDeclarations = maxDeclarations;
        _maxGrantsPerDeclaration = maxGrantsPerDeclaration;
        _maxPendingWaiters = maxPendingWaiters;
        _graph = new SingletonStage0DependencyGraph(maxActiveInitializers, maxDependencies);
    }

    public int RegisteredDeclarations
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// Trusted compiler/host registration only. The explicit domain owner
    /// supplies the initializer, never a guest actor or a wire request.
    /// </summary>
    public void RegisterCompilerDeclaration(string resolvedModule, string declarationName,
        Func<ValueTask<T>> initializer)
    {
        ActorRuntime.ValidateSingletonHostControl();
        ArgumentNullException.ThrowIfNull(initializer);
        var key = Key(resolvedModule, declarationName);
        lock (_gate)
        {
            EnsureOpen();
            if (_entries.ContainsKey(key))
                throw new InvalidOperationException(
                    "ORES-SINGLETON-DUPLICATE: declaration already registered in domain");
            if (_entries.Count >= _maxDeclarations)
                throw new InvalidOperationException(
                    "ORES-SINGLETON-DOMAIN-CAPACITY: declaration quota exhausted");
            _entries.Add(key, new Entry(initializer, _graph, _maxPendingWaiters));
        }
    }

    /// <summary>
    /// Host-only explicit read grant to an exact ActorRef incarnation. Granting
    /// is NOT accessible from Oreslang source/guest code. The runtime verifies
    /// the actor is currently live and belongs to this registry's runtime.
    /// </summary>
    public void GrantRead(ActorRef actor, string resolvedModule, string declarationName)
    {
        ActorRuntime.ValidateSingletonHostControl();
        var key = Key(resolvedModule, declarationName);
        _runtime.ValidateSingletonGrantActor(actor);
        lock (_gate)
        {
            EnsureOpen();
            var entry = GetEntry(key);
            if (entry.Readers.Contains(actor)) return;
            if (entry.Readers.Count >= _maxGrantsPerDeclaration)
                throw new InvalidOperationException(
                    "ORES-SINGLETON-GRANT-CAPACITY: read-grant quota exhausted");
            entry.Readers.Add(actor);
        }
    }

    /// <summary>
    /// Explicit host revocation. Generation/nonce mismatches are never
    /// implicitly upgraded to new actor incarnations.
    /// </summary>
    public bool RevokeRead(ActorRef actor, string resolvedModule, string declarationName)
    {
        ActorRuntime.ValidateSingletonHostControl();
        var key = Key(resolvedModule, declarationName);
        lock (_gate)
        {
            EnsureOpen();
            return GetEntry(key).Readers.Remove(actor);
        }
    }

    /// <summary>
    /// Trusted host starts a singleton outside the actor carrier. Its Task is
    /// never handed to a physical actor turn as a guest Future; a factory may
    /// run synchronously until its first await. Multiple host starts share one
    /// publication through the cell, with its own bounded waiter quota.
    /// </summary>
    public Task<T> StartForHost(string resolvedModule, string declarationName,
        CancellationToken observerCancellation = default)
    {
        ActorRuntime.ValidateSingletonHostControl();
        var key = Key(resolvedModule, declarationName);
        Entry entry;
        lock (_gate)
        {
            EnsureOpen();
            entry = GetEntry(key);
        }
        return entry.Cell.ResolveAsync(observerCancellation);
    }

    /// <summary>
    /// Ready-only, nonblocking actor turn read. An ActorContext is minted by
    /// the owning ActorRuntime; both issuer identity and physical turn are
    /// checked. A stale/foreign/unguarded caller cannot read a value. Returning
    /// a reference value is not a deep-immutability guarantee; production
    /// guest publication remains disabled until the type/effect checker exists.
    /// </summary>
    public bool TryReadPublished(ActorContext context, string resolvedModule,
        string declarationName, out T value)
    {
        ArgumentNullException.ThrowIfNull(context);
        value = default!;
        context.ValidateIssuedBy(_runtime);
        var key = Key(resolvedModule, declarationName);
        lock (_gate)
        {
            EnsureOpen();
            Entry entry = GetEntry(key);
            if (!entry.Readers.Contains(context.Self))
                throw new InvalidOperationException(
                    "ORES-SINGLETON-ACTOR-DENIED: explicit live-incarnation read grant missing");
            return entry.Cell.TryReadPublished(out value);
        }
    }

    /// <summary>
    /// Terminal application-domain fence. All pending cell reads fail and
    /// late host initializer completion cannot publish. No actor or guest can
    /// revive an old registry or a previous grant.
    /// </summary>
    public bool Retire()
    {
        ActorRuntime.ValidateSingletonHostControl();
        lock (_gate)
        {
            if (_retired) return false;
            _retired = true;
            foreach (var entry in _entries.Values)
            {
                entry.Readers.Clear();
                entry.Cell.Retire();
            }
            return true;
        }
    }

    public void Dispose() => Retire();

    private void EnsureOpen()
    {
        if (_retired)
            throw new InvalidOperationException(
                "ORES-SINGLETON-DOMAIN-RETIRED: registry domain already retired");
    }

    private Entry GetEntry(Declaration key)
    {
        if (!_entries.TryGetValue(key, out var entry))
            throw new InvalidOperationException(
                "ORES-SINGLETON-UNKNOWN: declaration not registered in domain");
        return entry;
    }

    private static Declaration Key(string module, string name)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(name);
        if (module.Length is 0 or > 4096 ||
            module[0] == '/' || module.Contains('\\') || module.Contains('\0') ||
            module.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException(
                "ORES-SINGLETON-KEY: invalid resolved module path", nameof(module));
        if (name.Length is 0 or > 128 ||
            !AsciiHead(name[0]) ||
            name.Skip(1).Any(c => !AsciiHead(c) && c is not (>= '0' and <= '9')))
            throw new ArgumentException(
                "ORES-SINGLETON-KEY: invalid declaration identifier", nameof(name));
        return new Declaration(module, name);
    }

    private static bool AsciiHead(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';
}
