namespace Ores.Net.Runtime;

/// <summary>
/// Actor-facing, read-only view: its only operation requires an owning-runtime
/// ActorContext from the current physical dispatch turn. This object contains
/// no registration, grant, host initializer or retirement methods.
/// </summary>
public sealed class SingletonDomainRegistryStage0<T>
{
    private readonly SingletonDomainHostControlStage0<T> _owner;

    internal SingletonDomainRegistryStage0(SingletonDomainHostControlStage0<T> owner) =>
        _owner = owner;

    public bool TryReadPublished(ActorContext context, string resolvedModule,
        string declarationName, out T value) =>
        _owner.TryReadPublished(context, resolvedModule, declarationName, out value);
}

/// <summary>
/// Bounded trusted-host stage-0 application-domain controller. This object must
/// never be passed into a guest actor closure. It requires an opaque issuer
/// installed in the creating host's ExecutionContext; actor-originated child
/// tasks cannot use it, even if they capture its CLR reference. A trusted host
/// can deliberately delegate a controller: this is not a sandbox.
/// </summary>
public sealed class SingletonDomainHostControlStage0<T> : IDisposable
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

    private sealed record HostIssuerScope(object Issuer, HostIssuerScope? Previous);

    private static readonly AsyncLocal<HostIssuerScope?> s_hostIssuer = new();
    private readonly object _issuer = new();
    private readonly ActorRuntime _runtime;
    private readonly object _gate = new();
    private int _disposed;
    private readonly SingletonStage0DependencyGraph _graph;
    private readonly Dictionary<Declaration, Entry> _entries = new();
    private readonly int _maxDeclarations;
    private readonly int _maxGrantsPerDeclaration;
    private readonly int _maxPendingWaiters;
    private bool _retired;

    // Issued solely through ActorRuntime.CreateSingletonDomainHostControlStage0.
    // A public constructor would let a suppressed-flow child task mint its
    // own issuer for a captured runtime without host bootstrap permission.
    internal SingletonDomainHostControlStage0(ActorRuntime runtime,
        int maxDeclarations = 64, int maxGrantsPerDeclaration = 256,
        int maxPendingWaiters = 128, int maxActiveInitializers = 1024,
        int maxDependencies = 4096)
    {
        ActorRuntime.ValidateSingletonHostControl();
        ArgumentNullException.ThrowIfNull(runtime);
        if (maxDeclarations is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxDeclarations));
        if (maxGrantsPerDeclaration is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxGrantsPerDeclaration));
        if (maxPendingWaiters is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxPendingWaiters));
        // The only checked stage-0 initializer IR returns i64. Until the
        // guest ownership/type checker is implemented, returning an arbitrary
        // CLR reference (or a custom value struct containing references)
        // would allow shared mutable identity across isolated actor arenas.
        // Do not pretend generics are a deep-immutability guarantee.
        if (typeof(T) != typeof(long))
            throw new NotSupportedException(
                "ORES-SINGLETON-STAGE0-TYPE-UNSUPPORTED: only i64 publication is admitted");
        _runtime = runtime;
        _maxDeclarations = maxDeclarations;
        _maxGrantsPerDeclaration = maxGrantsPerDeclaration;
        _maxPendingWaiters = maxPendingWaiters;
        _graph = new SingletonStage0DependencyGraph(maxActiveInitializers, maxDependencies);
        Reader = new SingletonDomainRegistryStage0<T>(this);
        // Never expose this issuer through Reader or actor context.
        // AsyncLocal survives legitimate host awaits; actor dispatch also
        // carries its own independent denial marker across Task.Run.
        s_hostIssuer.Value = new HostIssuerScope(_issuer, s_hostIssuer.Value);
    }

    public SingletonDomainRegistryStage0<T> Reader { get; }

    public int RegisteredDeclarations
    {
        get
        {
            RequireHostControl();
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>
    /// Trusted compiler/host registration only. The explicit domain owner
    /// supplies the initializer, never a guest actor or a wire request.
    /// </summary>
    public void RegisterCompilerDeclaration(string resolvedModule, string declarationName,
        Func<ValueTask<T>> initializer)
    {
        RequireHostControl();
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
        RequireHostControl();
        var key = Key(resolvedModule, declarationName);
        _runtime.ValidateSingletonGrantActor(actor);
        lock (_gate)
        {
            EnsureOpen();
            var entry = GetEntry(key);
            // Re-check inside the registry lock: Stop/Dispose could have
            // invalidated this exact incarnation after the initial check.
            _runtime.ValidateSingletonGrantActor(actor);
            if (entry.Readers.Contains(actor)) return;
            if (entry.Readers.Count >= _maxGrantsPerDeclaration)
                ReclaimStaleReaderGrantsLocked(entry);
            if (entry.Readers.Count >= _maxGrantsPerDeclaration)
                throw new InvalidOperationException(
                    "ORES-SINGLETON-GRANT-CAPACITY: read-grant quota exhausted");
            // In particular, the bounded stale-grant scan must never turn
            // a concurrently stopped candidate into a new valid grant.
            _runtime.ValidateSingletonGrantActor(actor);
            entry.Readers.Add(actor);
        }
    }

    // Called only under _gate and only when a declaration is at its grant
    // capacity. The scan is bounded by maxGrantsPerDeclaration (<= 65536),
    // uses runtime-authenticated incarnation checks rather than numeric IDs,
    // and never implicitly authorizes a successor or another domain.
    private void ReclaimStaleReaderGrantsLocked(Entry entry)
    {
        foreach (ActorRef existing in entry.Readers.ToArray())
        {
            try
            {
                _runtime.ValidateSingletonGrantActor(existing);
            }
            catch (InvalidOperationException)
            {
                // Stopped, retired, replaced or foreign runtime references
                // cannot be active grants; reject and reclaim their slots.
                entry.Readers.Remove(existing);
            }
        }
    }

    /// <summary>
    /// Explicit host revocation. Generation/nonce mismatches are never
    /// implicitly upgraded to new actor incarnations.
    /// </summary>
    public bool RevokeRead(ActorRef actor, string resolvedModule, string declarationName)
    {
        RequireHostControl();
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
        RequireHostControl();
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
    internal bool TryReadPublished(ActorContext context, string resolvedModule,
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
        RequireHostControl();
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

    public void Dispose()
    {
        // Idempotent after an already-completed disposal. A second caller
        // does not acquire any new administrative authority.
        if (Volatile.Read(ref _disposed) != 0) return;
        RequireHostControl();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Retire(); }
        finally
        {
            // Reconstruct the inherited token chain rather than mutating a
            // shared collection that a different async child might inherit.
            s_hostIssuer.Value = WithoutIssuer(s_hostIssuer.Value, _issuer);
        }
    }

    private void RequireHostControl()
    {
        ActorRuntime.ValidateSingletonHostControl();
        // The object reference alone is not a host authorization token.
        // SuppressFlow loses this issuer, so a detached child cannot recover
        // host privileges by discarding its actor-origin marker.
        for (var scope = s_hostIssuer.Value; scope is not null; scope = scope.Previous)
            if (ReferenceEquals(scope.Issuer, _issuer)) return;
        throw new InvalidOperationException(
            "ORES-SINGLETON-HOST-CONTROL-DENIED: unissued control context");
    }

    private static HostIssuerScope? WithoutIssuer(HostIssuerScope? head, object issuer)
    {
        var prefix = new List<object>();
        var node = head;
        while (node is not null && !ReferenceEquals(node.Issuer, issuer))
        {
            prefix.Add(node.Issuer);
            node = node.Previous;
        }
        if (node is null) return head;
        var remaining = node.Previous;
        for (int i = prefix.Count - 1; i >= 0; i--)
            remaining = new HostIssuerScope(prefix[i], remaining);
        return remaining;
    }

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
