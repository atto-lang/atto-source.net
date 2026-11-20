namespace Ores.Net.Runtime;

/// <summary>
/// Bounded trusted-host observation of an already-started System Task.
/// Not an Oreslang Future/Task/Observable binder or guest callback scheduler.
/// Does not grant producer cancellation or physical-retirement authority.
/// Guest affine/Send/Sync, effects and generation admission remain unsupported.
/// </summary>
public sealed class HostTaskObservationScope
{
    private readonly int capacity;
    private int pending;

    public HostTaskObservationScope(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    // Accounting only: zero is NOT producer or actor physical quiescence.
    public int PendingObservations => Volatile.Read(ref pending);

    public Task<T> Observe<T>(Task<T> source, CancellationToken observerCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Reserve(); // Reject before a source waiter becomes visible.
        TaskCompletionSource<T> projection;
        try
        {
            // Always distinct, even for cached/already-settled System Tasks.
            projection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        catch
        {
            Interlocked.Decrement(ref pending);
            throw;
        }
        _ = Forward(source, projection, observerCancellation);
        return projection.Task;
    }

    private void Reserve()
    {
        for (;;)
        {
            int before = Volatile.Read(ref pending);
            if (before >= capacity)
                throw new InvalidOperationException("Host Task observation quota exhausted");
            if (Interlocked.CompareExchange(ref pending, before + 1, before) == before)
                return;
        }
    }

    private async Task Forward<T>(Task<T> source, TaskCompletionSource<T> projection,
        CancellationToken observerCancellation)
    {
        try
        {
            // WaitAsync cancellation revokes this wait, never the source Task.
            // No Task.Run, new executor, guest callback or blocking carrier wait.
            Task<T> wait = source.WaitAsync(observerCancellation);
            try
            {
                T value = await wait.ConfigureAwait(false);
                projection.TrySetResult(value);
            }
            catch (Exception) when (wait.IsCanceled)
            {
                // Preserve authentic terminal cancellation without exporting
                // the source producer's CancellationToken through the view.
                projection.TrySetCanceled(observerCancellation.IsCancellationRequested
                    ? observerCancellation : new CancellationToken(canceled: true));
            }
            catch (Exception error)
            {
                // A faulted Task containing OperationCanceledException stays
                // FAULTED; exception spelling does not authenticate cancellation.
                // Keep all causes for a multi-fault System Task.
                if (wait.Exception is { } failure)
                    projection.TrySetException(failure.InnerExceptions);
                else projection.TrySetException(error);
            }
        }
        catch (Exception error)
        {
            projection.TrySetException(error);
        }
        finally
        {
            Interlocked.Decrement(ref pending);
        }
    }
}
