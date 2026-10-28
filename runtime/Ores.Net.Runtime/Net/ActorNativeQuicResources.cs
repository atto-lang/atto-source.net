using System.Security;

namespace Ores.Net.Runtime.Net;

/// <summary>
/// CONTROL-owned, bounded registry. Only runtime-issued ActorContext can
/// register physical native work. Stop is synchronous and fail-closed; native
/// cancellation and disposal are asynchronous and must actually finish
/// before Retired completes. Does not provide guest QUIC lowering.
/// </summary>
internal sealed class ActorNativeQuicResources
{
    internal const int MaxConnectionsPerActor = 16;
    private readonly object _gate = new();
    private readonly HashSet<ActorQuicOperation> _operations = new();
    private readonly TaskCompletionSource<bool> _retired =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;
    private Exception? _retirementFailure;

    internal Task Retired => _retired.Task;
    internal int LiveOperations
    {
        get { lock (_gate) return _operations.Count; }
    }

    internal ActorQuicOperation Register(ActorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // This validates physical turn authority and pins arena storage.
        // Stop can run concurrently: both paths serialize publication here.
        var pin = context.PinLifetime();
        ActorQuicOperation? operation = null;
        try
        {
            lock (_gate)
            {
                if (_stopping || context.OwnerArena.IsRetired)
                    throw new SecurityException("Actor QUIC admission revoked");
                if (_operations.Count >= MaxConnectionsPerActor)
                    throw new InvalidOperationException("Actor QUIC connection quota exceeded");
                operation = new ActorQuicOperation(this, pin);
                _operations.Add(operation);
                return operation;
            }
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }

    // Called under the actor cell gate, without awaiting native operations.
    internal Task Stop()
    {
        ActorQuicOperation[] active;
        lock (_gate)
        {
            if (_stopping) return _retired.Task;
            _stopping = true;
            active = _operations.ToArray();
            if (active.Length == 0) _retired.TrySetResult(true);
        }
        foreach (var operation in active) operation.Stop();
        return _retired.Task;
    }

    internal void Completed(ActorQuicOperation operation, Exception? failure)
    {
        lock (_gate)
        {
            if (!_operations.Remove(operation))
                throw new InvalidOperationException("Unregistered native QUIC retirement");
            _retirementFailure ??= failure;
            if (_stopping && _operations.Count == 0)
            {
                if (_retirementFailure is null) _retired.TrySetResult(true);
                else _retired.TrySetException(_retirementFailure);
            }
        }
    }
}

/// <summary>
/// A pending handshake is a real outstanding resource obligation even before
/// it has returned a native handle. Publication, stop and disposal are
/// linearized under _gate. A pin releases only after pending work settles
/// and its owned resource physically completes DisposeAsync.
/// </summary>
internal sealed class ActorQuicOperation : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ActorNativeQuicResources _owner;
    private readonly ActorArenaLifetimePin _pin;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<bool> _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IAsyncDisposable? _resource;
    private bool _pending = true;
    private bool _stopping;
    private bool _cleanupScheduled;

    internal ActorQuicOperation(ActorNativeQuicResources owner, ActorArenaLifetimePin pin)
    {
        _owner = owner;
        _pin = pin;
    }

    internal CancellationToken StopToken => _cancel.Token;
    internal Task Retired => _completed.Task;
    internal bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

    /// <summary>
    /// Ownership transfer from handshake to actor ledger. On a losing stop
    /// race the handle is disposed before PublishAsync throws.
    /// </summary>
    internal async ValueTask PublishAsync(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        bool rejected;
        lock (_gate)
        {
            if (!_pending)
                throw new InvalidOperationException("Native QUIC handle published twice");
            _resource = resource;
            _pending = false;
            rejected = _stopping;
        }
        if (rejected)
        {
            ScheduleCleanup();
            await _completed.Task.ConfigureAwait(false);
            throw new OperationCanceledException(
                "Actor retired before QUIC native connection publication");
        }
    }

    // ConnectAsync threw before returning a handle. This is a physical
    // completion edge only after the provider's async connect has settled.
    internal void FailConnect()
    {
        lock (_gate)
        {
            if (!_pending) return;
            _pending = false;
        }
        Stop();
    }

    internal void Stop()
    {
        bool changed;
        lock (_gate)
        {
            changed = !_stopping;
            _stopping = true;
        }
        if (changed)
        {
            // Never invoke arbitrary cancellation callbacks on actor carriers.
            _ = Task.Run(async () =>
            {
                try { await _cancel.CancelAsync().ConfigureAwait(false); }
                catch (Exception) { /* cleanup still owns retirement */ }
            });
        }
        ScheduleCleanup();
    }

    private void ScheduleCleanup()
    {
        IAsyncDisposable? resource;
        lock (_gate)
        {
            if (!_stopping || _pending || _cleanupScheduled) return;
            _cleanupScheduled = true;
            resource = _resource;
        }
        // Managed background control work only; this is not a hard-RT API.
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                if (resource is not null)
                    await resource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try { _pin.Dispose(); }
                catch (Exception error) { failure ??= error; }
                _cancel.Dispose();
                _owner.Completed(this, failure);
                if (failure is null) _completed.TrySetResult(true);
                else _completed.TrySetException(failure);
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return new ValueTask(_completed.Task);
    }
}
