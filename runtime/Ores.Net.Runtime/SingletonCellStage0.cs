namespace Ores.Net.Runtime;

/// <summary>
/// Trusted-host, asynchronous stage-0 singleton cell. The dependency graph is
/// host-owned; it is neither a guest API nor a tenant/actor authority boundary.
/// An initializer may finish after retirement but may never publish afterward.
/// </summary>
public sealed class SingletonCellStage0<T>
{
    private enum Phase : byte { Uninitialized, Initializing, Ready, Failed, Retired }

    private readonly object _gate = new();
    private readonly object _identity = new();
    private readonly SingletonStage0DependencyGraph _graph;
    private readonly Func<ValueTask<T>> _initialize;
    private readonly TaskCompletionSource<T> _outcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _maxPendingWaiters;
    private int _pendingWaiters;
    private Phase _phase;

    public SingletonCellStage0(Func<ValueTask<T>> initializer,
        SingletonStage0DependencyGraph? graph = null, int maxPendingWaiters = 128)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        if (maxPendingWaiters is <= 0 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxPendingWaiters));
        _initialize = initializer;
        // Stage-0 default only; production domain registry must inject a
        // domain-specific graph and separately authenticate actor authority.
        _graph = graph ?? SingletonStage0DependencyGraph.HostTestDefault;
        _maxPendingWaiters = maxPendingWaiters;
    }

    public int PendingWaiters
    {
        get { lock (_gate) return _pendingWaiters; }
    }

    /// <summary>
    /// Exactly one initializer attempt. Each pending caller has a bounded
    /// registration; cancellation revokes only the caller's waiting task.
    /// </summary>
    public Task<T> ResolveAsync(CancellationToken waiterCancellation = default)
    {
        if (waiterCancellation.IsCancellationRequested)
            return Task.FromCanceled<T>(waiterCancellation);
        try { _graph.ValidateAmbientGraph(); }
        catch (InvalidOperationException error) { return Task.FromException<T>(error); }
        lock (_gate)
        {
            if (_phase == Phase.Retired)
                return Retired();
            if (_phase is Phase.Ready or Phase.Failed)
                return _outcome.Task; // Completed task needs no waiter slot.
        }

        IDisposable? dependency;
        try { dependency = _graph.BeginDependency(_identity); }
        catch (InvalidOperationException error) { return Task.FromException<T>(error); }

        bool start = false;
        Task<T> shared;
        lock (_gate)
        {
            if (_phase == Phase.Retired)
            {
                dependency?.Dispose();
                return Retired();
            }
            if (_phase is Phase.Ready or Phase.Failed)
            {
                dependency?.Dispose();
                return _outcome.Task;
            }
            if (_pendingWaiters >= _maxPendingWaiters)
            {
                dependency?.Dispose();
                return Task.FromException<T>(new InvalidOperationException(
                    "ORES-SINGLETON-WAITER-CAPACITY: pending wait quota exhausted"));
            }
            _pendingWaiters++;
            if (_phase == Phase.Uninitialized)
            {
                _phase = Phase.Initializing;
                start = true;
            }
            shared = _outcome.Task;
        }

        // An edge is installed before a new initializer can execute, so an
        // A->B->A cycle is detected even when independent tasks start A and B.
        if (start) _ = InitializeAsync();
        return AwaitRegistered(shared, dependency, waiterCancellation);
    }

    private async Task<T> AwaitRegistered(
        Task<T> outcome, IDisposable? dependency, CancellationToken cancellation)
    {
        try
        {
            return cancellation.CanBeCanceled
                ? await outcome.WaitAsync(cancellation).ConfigureAwait(false)
                : await outcome.ConfigureAwait(false);
        }
        finally
        {
            dependency?.Dispose();
            lock (_gate) _pendingWaiters--;
        }
    }

    private async Task InitializeAsync()
    {
        IDisposable? scope = null;
        try
        {
            // The graph bounds active initializers and owns the ambient
            // per-attempt scope that flows across awaited continuations.
            scope = _graph.EnterInitializer(_identity);
            lock (_gate)
            {
                if (_phase != Phase.Initializing) return;
            }
            T value = await _initialize().ConfigureAwait(false);
            if (value is null)
                throw new InvalidOperationException(
                    "ORES-SINGLETON-NULL-PUBLICATION: a singleton value cannot be null");
            lock (_gate)
            {
                if (_phase == Phase.Initializing)
                {
                    _phase = Phase.Ready;
                    _outcome.TrySetResult(value);
                }
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_phase == Phase.Initializing)
                {
                    // Factory faults (even OperationCanceledException) are
                    // sticky failures and cannot restart the factory.
                    _phase = Phase.Failed;
                    _outcome.TrySetException(error);
                }
            }
        }
        finally { scope?.Dispose(); }
    }

    private static Task<T> Retired() => Task.FromException<T>(
        new InvalidOperationException("ORES-SINGLETON-RETIRED: domain retired the singleton cell"));

    /// <summary>
    /// Owner-only terminal fence. Pending waits fail. Old immutable leases
    /// remain caller-owned. Does not cancel the factory or prove quiescence.
    /// </summary>
    public bool Retire()
    {
        lock (_gate)
        {
            if (_phase == Phase.Retired) return false;
            bool pending = _phase == Phase.Initializing;
            _phase = Phase.Retired;
            if (pending)
                _outcome.TrySetException(new InvalidOperationException(
                    "ORES-SINGLETON-RETIRED: domain retired the singleton cell"));
            return true;
        }
    }
}
