namespace Ores.Net.Runtime;

/// <summary>
/// Trusted-host stage-0 dependency tracker. One instance may be owned by an
/// application domain; it is not an actor capability, global singleton
/// registry, production scheduler lease or cross-isolate deadlock oracle.
/// </summary>
public sealed class SingletonStage0DependencyGraph
{
    private sealed record Scope(SingletonStage0DependencyGraph Graph, object Cell);
    internal static readonly SingletonStage0DependencyGraph HostTestDefault = new();
    private static readonly AsyncLocal<Scope?> Current = new();
    private readonly object _gate = new();
    private readonly HashSet<object> _active = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Dictionary<object, int>> _dependencies =
        new(ReferenceEqualityComparer.Instance);
    private readonly int _maxActive;
    private readonly int _maxDependencies;
    private int _pendingDependencies;

    public SingletonStage0DependencyGraph(int maxActive = 1024, int maxDependencies = 4096)
    {
        if (maxActive is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxActive));
        if (maxDependencies is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxDependencies));
        _maxActive = maxActive;
        _maxDependencies = maxDependencies;
    }

    /// <summary>Host accounting only, not a physical quiescence receipt.</summary>
    public int ActiveInitializers
    {
        get { lock (_gate) return _active.Count; }
    }

    public int PendingDependencies
    {
        get { lock (_gate) return _pendingDependencies; }
    }

    internal IDisposable EnterInitializer(object cell)
    {
        lock (_gate)
        {
            if (_active.Count >= _maxActive)
                throw new InvalidOperationException(
                    "ORES-SINGLETON-GRAPH-CAPACITY: active initializer limit exceeded");
            if (!_active.Add(cell))
                throw new InvalidOperationException(
                    "ORES-SINGLETON-INIT-CYCLE: initializer already active");
        }
        var prior = Current.Value;
        Current.Value = new Scope(this, cell);
        return new InitializerLease(this, cell, prior);
    }

    internal void ValidateAmbientGraph()
    {
        var scope = Current.Value;
        if (scope is not null && !ReferenceEquals(scope.Graph, this))
            throw new InvalidOperationException(
                "ORES-SINGLETON-GRAPH-BOUNDARY: initializer crossed stage0 graph domains");
    }

    internal IDisposable? BeginDependency(object target)
    {
        var scope = Current.Value;
        if (scope is null) return null;
        if (!ReferenceEquals(scope.Graph, this))
            throw new InvalidOperationException(
                "ORES-SINGLETON-GRAPH-BOUNDARY: initializer crossed stage0 graph domains");
        lock (_gate)
        {
            if (!_active.Contains(scope.Cell))
                throw new InvalidOperationException(
                    "ORES-SINGLETON-INIT-CONTEXT-RETIRED: stale initializer flow");
            if (ReferenceEquals(scope.Cell, target) || Reachable(target, scope.Cell))
                throw new InvalidOperationException(
                    "ORES-SINGLETON-INIT-CYCLE: bounded wait-for graph contains a cycle");
            if (_pendingDependencies >= _maxDependencies)
                throw new InvalidOperationException(
                    "ORES-SINGLETON-GRAPH-CAPACITY: dependency registration limit exceeded");
            if (!_dependencies.TryGetValue(scope.Cell, out var outbound))
            {
                outbound = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
                _dependencies.Add(scope.Cell, outbound);
            }
            outbound.TryGetValue(target, out int count);
            outbound[target] = checked(count + 1);
            _pendingDependencies++;
        }
        return new DependencyLease(this, scope.Cell, target);
    }

    // All callers hold _gate. The work is bounded by _maxActive and by
    // _maxDependencies; no recursive traversal or unbounded graph growth.
    private bool Reachable(object source, object destination)
    {
        var explored = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (ReferenceEquals(node, destination)) return true;
            if (!explored.Add(node) || !_active.Contains(node)) continue;
            if (_dependencies.TryGetValue(node, out var children))
                foreach (var child in children.Keys)
                    if (!explored.Contains(child)) pending.Push(child);
        }
        return false;
    }

    private void RemoveDependency(object owner, object target)
    {
        lock (_gate)
        {
            if (!_dependencies.TryGetValue(owner, out var outbound) ||
                !outbound.TryGetValue(target, out int count)) return;
            if (count == 1) outbound.Remove(target);
            else outbound[target] = count - 1;
            _pendingDependencies--;
            if (outbound.Count == 0) _dependencies.Remove(owner);
        }
    }

    private void LeaveInitializer(object cell)
    {
        lock (_gate)
        {
            _active.Remove(cell);
            if (_dependencies.Remove(cell, out var outbound))
                foreach (int count in outbound.Values)
                    _pendingDependencies -= count;
        }
    }

    private sealed class DependencyLease(
        SingletonStage0DependencyGraph graph, object owner, object target) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                graph.RemoveDependency(owner, target);
        }
    }

    private sealed class InitializerLease(
        SingletonStage0DependencyGraph graph, object cell, Scope? prior) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            Current.Value = prior;
            graph.LeaveInitializer(cell);
        }
    }
}
