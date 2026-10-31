namespace Ores.Net.Runtime;

/// <summary>
/// Trusted-host, asynchronous stage-0 singleton publication cell. This is not
/// guest singleton syntax, a tenant registry, a capability or a verified
/// deep-immutability boundary. A domain owner must construct and retire it.
/// Waiter cancellation never cancels its single initializer attempt.
/// </summary>
public sealed class SingletonCellStage0<T>
{
    private enum Phase : byte { Uninitialized, Initializing, Ready, Failed, Retired }

    private readonly object _gate = new();
    private readonly Func<ValueTask<T>> _initialize;
    private readonly TaskCompletionSource<T> _outcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncLocal<bool> _insideInitializer = new();
    private Phase _phase;

    public SingletonCellStage0(Func<ValueTask<T>> initializer)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        _initialize = initializer;
    }

    /// <summary>
    /// Admits at most one initializer and returns only its fully published
    /// outcome. Does not block a carrier thread or create an executor.
    /// The cancellation token revokes only this caller's wait.
    /// </summary>
    public Task<T> ResolveAsync(CancellationToken waiterCancellation = default)
    {
        // AsyncLocal travels with the initializer's ExecutionContext across
        // awaits: reject direct and nested same-flow dependency cycles before
        // awaiting our own incomplete publication task.
        if (_insideInitializer.Value)
            return Task.FromException<T>(new InvalidOperationException(
                "ORES-SINGLETON-INIT-CYCLE: initializer depends on its own publication"));
        if (waiterCancellation.IsCancellationRequested)
            return Task.FromCanceled<T>(waiterCancellation);

        bool start = false;
        lock (_gate)
        {
            if (_phase == Phase.Retired)
                return Task.FromException<T>(new InvalidOperationException(
                    "ORES-SINGLETON-RETIRED: domain retired the singleton cell"));
            if (_phase == Phase.Uninitialized)
            {
                _phase = Phase.Initializing;
                start = true;
            }
        }

        // Never execute caller-provided initializer code under the cell lock.
        if (start) _ = InitializeAsync();
        Task<T> result = _outcome.Task;
        return waiterCancellation.CanBeCanceled
            ? result.WaitAsync(waiterCancellation) : result;
    }

    private async Task InitializeAsync()
    {
        // Retirement may win after admission and before scheduling begins.
        lock (_gate)
        {
            if (_phase != Phase.Initializing) return;
        }
        bool previous = _insideInitializer.Value;
        _insideInitializer.Value = true;
        try
        {
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
                    // Even an OperationCanceledException from the factory is
                    // a sticky initialization FAILURE, not a canceled waiter
                    // and never a reason to retry the factory.
                    _phase = Phase.Failed;
                    _outcome.TrySetException(error);
                }
            }
        }
        finally
        {
            _insideInitializer.Value = previous;
        }
    }

    /// <summary>
    /// Domain-owner fence. Denies future reads and fails pending readers; an
    /// initializer finishing later can never publish or restart this cell.
    /// Already returned immutable value leases are not forcibly disposed.
    /// This does not cancel an in-flight host initializer or prove quiescence.
    /// </summary>
    public bool Retire()
    {
        lock (_gate)
        {
            if (_phase == Phase.Retired) return false;
            bool pending = _phase == Phase.Initializing;
            _phase = Phase.Retired;
            // An uninitialized cell has never issued an outcome task. Do not
            // fault an orphaned task; new reads are denied by the phase fence.
            if (pending)
                _outcome.TrySetException(new InvalidOperationException(
                    "ORES-SINGLETON-RETIRED: domain retired the singleton cell"));
            return true;
        }
    }
}
