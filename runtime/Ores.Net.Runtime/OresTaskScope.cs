using System.Threading.Channels;

namespace Ores.Net.Runtime;

/// <summary>
/// Bounded, scope-owned execution substrate for the first .NET source Task slice.
/// OresTask and OresFuture are nominal guest handles; neither inherits System.Task.
/// This currently has one serialized scheduler lane, NOT an actor-affine scheduler.
/// Only compiler-verified, nonblocking guest work may be admitted. This is not a
/// general-purpose callback, Send/Sync, ownership, or hot-reload implementation.
/// </summary>
public sealed class OresTaskScope : IAsyncDisposable
{
    // Host-only bootstrap limits. Do not allow caller-selected int.MaxValue
    // capacities to turn a bounded implementation into an exhaustion primitive.
    private const int MaxProducers = 4096;
    private const int MaxObservers = 4096;
    private readonly Channel<Func<ValueTask>> queue;
    // Serializes the admit/close boundary, including work published to the
    // queue and observer forwarders registered before shutdown.
    private readonly object admissionGate = new();
    private int pendingObserverForwarders;
    private TaskCompletionSource<bool> forwardersDrained = CompletedSignal();
    private readonly CancellationTokenSource producerStop = new();
    private readonly Task pump;
    private readonly TaskCompletionSource<bool> disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncLocal<bool> executingOnPump = new();
    private readonly int producerLimit;
    private readonly int observerLimit;
    private int activeProducers;
    private int activeObservers;
    private int producerInvocations;
    private int closed;
    private long nextId;

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var ready = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ready.SetResult(true);
        return ready;
    }

    public OresTaskScope(int maxProducers = 16, int maxObservers = 32)
    {
        if (maxProducers <= 0 || maxProducers > MaxProducers)
            throw new ArgumentOutOfRangeException(nameof(maxProducers),
                "Task producer capacity must be within 1..4096");
        if (maxObservers <= 0 || maxObservers > MaxObservers)
            throw new ArgumentOutOfRangeException(nameof(maxObservers),
                "Task observer capacity must be within 1..4096");
        producerLimit = maxProducers;
        observerLimit = maxObservers;
        queue = Channel.CreateBounded<Func<ValueTask>>(new BoundedChannelOptions(maxProducers)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        // One channel reader, resumed by asynchronous bounded channel writes.
        // No eager worker allocation and no scheduling on conversion.
        // Do not claim actor-lane affinity from this host scheduling mechanism.
        pump = PumpAsync();
    }

    public int ActiveProducers => Volatile.Read(ref activeProducers);
    public int ActiveObservers => Volatile.Read(ref activeObservers);
    public int ProducerInvocations => Volatile.Read(ref producerInvocations);

    /// <summary>Trusted compiler entry only. Each admitted producer executes once.</summary>
    public OresTask<T> Start<T>(Func<CancellationToken, ValueTask<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (admissionGate)
        {
            if (closed != 0)
                throw new InvalidOperationException("Oreslang Task scope closed");
            Reserve(ref activeProducers, producerLimit, "producer");
            try
            {
                // The admission and queue publication precede shutdown
                // atomically. All failures roll back producer quota.
                long id = NextProducerId();
                var result = new TaskCompletionSource<T>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var retired = new TaskCompletionSource<OresProducerRetirement>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var handle = new OresTask<T>(this, id, result.Task, retired.Task);
                if (!queue.Writer.TryWrite(() => Execute(body, result, retired, id)))
                    throw new InvalidOperationException(
                        "Oreslang Task scheduler queue closed or full");
                return handle;
            }
            catch
            {
                Interlocked.Decrement(ref activeProducers);
                throw;
            }
        }
    }

    private long NextProducerId()
    {
        for (;;)
        {
            long before = Interlocked.Read(ref nextId);
            if (before == long.MaxValue)
                throw new OverflowException("Oreslang Task producer identity exhausted");
            if (Interlocked.CompareExchange(ref nextId, before + 1, before) == before)
                return before + 1;
        }
    }

    public OresFuture<T> FromTask<T>(OresTask<T> producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        if (!ReferenceEquals(producer.Scope, this))
            throw new InvalidOperationException("Cross-scope Task observation is not admitted");
        lock (admissionGate)
        {
            if (closed != 0)
                throw new InvalidOperationException("Oreslang Task scope closed");
            // Conversion is admitted atomically against scope close.
            // This does not reserve an observer until ObserveAsync is called.
            return new OresFuture<T>(this, producer.ProducerId, producer.Completion);
        }
    }

    internal Task<T> Observe<T>(Task<T> source, CancellationToken cancellation)
    {
        TaskCompletionSource<T> projected;
        lock (admissionGate)
        {
            if (closed != 0)
                throw new InvalidOperationException("Oreslang Task scope closed");
            Reserve(ref activeObservers, observerLimit, "observer");
            try
            {
                projected = new TaskCompletionSource<T>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                // Allocate before mutating forwarding counters so OOM or
                // constructor failure cannot strand the disposal drain.
                TaskCompletionSource<bool>? nextDrain = null;
                if (pendingObserverForwarders == 0)
                    nextDrain = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                if (pendingObserverForwarders == int.MaxValue)
                    throw new OverflowException("Observer forwarding capacity overflow");
                pendingObserverForwarders++;
                if (nextDrain is not null) forwardersDrained = nextDrain;
            }
            catch
            {
                Interlocked.Decrement(ref activeObservers);
                throw;
            }
        }
        // Counts are reserved before the forwarding continuation is published.
        // In particular, a concurrent DisposeAsync cannot miss the forwarder.
        _ = Forward(source, projected, cancellation);
        return projected.Task; // A distinct, cancellation-isolated observer.
    }

    private void RetireObserverForwarder()
    {
        lock (admissionGate)
        {
            if (--pendingObserverForwarders == 0)
                forwardersDrained.TrySetResult(true);
        }
    }

    private async Task Forward<T>(
        Task<T> source, TaskCompletionSource<T> projection, CancellationToken token)
    {
        T value = default!;
        Exception? fault = null;
        IReadOnlyCollection<Exception>? causes = null;
        bool cancelled = false;
        try
        {
            var wait = source.WaitAsync(token); // NEVER cancel producer from observer.
            try { value = await wait.ConfigureAwait(false); }
            catch (Exception) when (wait.IsCanceled) { cancelled = true; }
            catch (Exception error)
            {
                fault = error;
                causes = wait.Exception?.InnerExceptions;
            }
        }
        catch (Exception error) { fault = error; }
        finally
        {
            // Release the continuation quota BEFORE publishing the observed
            // completion. A completed observer is never still counted pending.
            Interlocked.Decrement(ref activeObservers);
        }
        try
        {
            if (cancelled)
                projection.TrySetCanceled(token.IsCancellationRequested
                    ? token : new CancellationToken(canceled: true));
            else if (causes is { Count: > 0 }) projection.TrySetException(causes);
            else if (fault is not null) projection.TrySetException(fault);
            else projection.TrySetResult(value);
        }
        finally
        {
            // The observer's quota is released before publication, but the
            // owning scope cannot retire while forwarding is still executing.
            RetireObserverForwarder();
        }
    }

    private async ValueTask Execute<T>(
        Func<CancellationToken, ValueTask<T>> body,
        TaskCompletionSource<T> result,
        TaskCompletionSource<OresProducerRetirement> retired, long id)
    {
        string terminal = "succeeded";
        try
        {
            producerStop.Token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref producerInvocations);
            var value = await body(producerStop.Token).ConfigureAwait(false);
            result.TrySetResult(value);
        }
        catch (OperationCanceledException error) when (
            producerStop.IsCancellationRequested &&
            error.CancellationToken == producerStop.Token)
        {
            // Only the scope's actual cancellation token authenticates
            // producer cancellation; a user-thrown OCE is a normal fault.
            terminal = "cancelled";
            result.TrySetCanceled(producerStop.Token);
        }
        catch (Exception error)
        {
            terminal = "faulted";
            // A cancellation-named error without producer authority remains a fault.
            result.TrySetException(error);
        }
        finally
        {
            // Retirement is a SEPARATE witness issued only after execution exits,
            // distinct from observer detachment and Task result completion.
            Interlocked.Decrement(ref activeProducers);
            retired.TrySetResult(new OresProducerRetirement(id, terminal));
        }
    }

    private async Task PumpAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Disallow self-joining the pump from the currently executing
            // producer, which otherwise deadlocks physical retirement.
            executingOnPump.Value = true;
            try { await work().ConfigureAwait(false); }
            finally { executingOnPump.Value = false; }
        }
    }

    private static void Reserve(ref int counter, int limit, string kind)
    {
        for (;;)
        {
            int before = Volatile.Read(ref counter);
            if (before >= limit)
                throw new InvalidOperationException("Oreslang Task " + kind + " quota exhausted");
            if (Interlocked.CompareExchange(ref counter, before + 1, before) == before)
                return;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (executingOnPump.Value)
            throw new InvalidOperationException(
                "Scope producer cannot await its own physical retirement");
        bool shouldStop;
        lock (admissionGate)
        {
            shouldStop = closed == 0;
            if (shouldStop) closed = 1;
        }
        if (shouldStop)
            _ = StopAndDrainAsync();
        // Every concurrent caller joins the SAME completion witness. A second
        // caller never reports success while a producer is still executing.
        return new ValueTask(disposed.Task);
    }

    private async Task StopAndDrainAsync()
    {
        Exception? failure = null;
        try { producerStop.Cancel(); }
        catch (Exception error) { failure = error; }
        finally { queue.Writer.TryComplete(); }

        try
        {
            await pump.ConfigureAwait(false);
            // Pump completion proves producer bodies exited, not necessarily
            // that independent observer forwarding continuations retired.
            // Admission is closed under admissionGate, so this task's
            // completion cannot be followed by a newly admitted forwarder.
            Task observerDrain;
            lock (admissionGate) observerDrain = forwardersDrained.Task;
            await observerDrain.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = failure is null ? error : new AggregateException(failure, error);
        }
        finally { producerStop.Dispose(); }

        if (failure is null) disposed.TrySetResult(true);
        else disposed.TrySetException(failure);
    }
}

/// <summary>Physical exit from the scope scheduler, not observer completion.</summary>
public sealed record OresProducerRetirement(long ProducerId, string TerminalState);

/// <summary>Nominal Oreslang Task handle with no public producer-cancel authority.</summary>
public sealed class OresTask<T>
{
    internal OresTask(OresTaskScope scope, long id, Task<T> completion,
        Task<OresProducerRetirement> retirement)
    {
        Scope = scope;
        ProducerId = id;
        Completion = completion;
        Retirement = retirement;
    }

    internal OresTaskScope Scope { get; }
    internal Task<T> Completion { get; }
    public long ProducerId { get; }
    public Task<OresProducerRetirement> Retirement { get; }
}

/// <summary>Nominal, observer-only Oreslang Future; cancellations detach views only.</summary>
public sealed class OresFuture<T>
{
    private readonly OresTaskScope scope;
    private readonly Task<T> completion;
    internal OresFuture(OresTaskScope scope, long id, Task<T> completion)
    {
        this.scope = scope;
        this.completion = completion;
        ProducerId = id;
    }

    public long ProducerId { get; }
    public Task<T> ObserveAsync(CancellationToken observerCancellation = default) =>
        scope.Observe(completion, observerCancellation);
}
