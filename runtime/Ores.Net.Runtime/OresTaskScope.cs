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
    private readonly Channel<Func<ValueTask>> queue;
    private readonly CancellationTokenSource producerStop = new();
    private readonly Task pump;
    private readonly int producerLimit;
    private readonly int observerLimit;
    private int activeProducers;
    private int activeObservers;
    private int producerInvocations;
    private int closed;
    private long nextId;

    public OresTaskScope(int maxProducers = 16, int maxObservers = 32)
    {
        if (maxProducers <= 0 || maxObservers <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxProducers),
                "Task scope limits must be positive");
        producerLimit = maxProducers;
        observerLimit = maxObservers;
        queue = Channel.CreateBounded<Func<ValueTask>>(new BoundedChannelOptions(maxProducers)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        // One fixed host scheduling pump per scope, never Task.Run on conversion.
        // Do not claim actor-lane affinity from this host scheduling mechanism.
        pump = Task.Run(PumpAsync);
    }

    public int ActiveProducers => Volatile.Read(ref activeProducers);
    public int ActiveObservers => Volatile.Read(ref activeObservers);
    public int ProducerInvocations => Volatile.Read(ref producerInvocations);

    /// <summary>Trusted compiler entry only. Each admitted producer executes once.</summary>
    public OresTask<T> Start<T>(Func<CancellationToken, ValueTask<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Volatile.Read(ref closed) != 0)
            throw new InvalidOperationException("Oreslang Task scope closed");
        Reserve(ref activeProducers, producerLimit, "producer");
        var id = Interlocked.Increment(ref nextId);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource<OresProducerRetirement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new OresTask<T>(this, id, result.Task, retired.Task);
        // Quota is acquired BEFORE publishing any runnable work.
        if (!queue.Writer.TryWrite(() => Execute(body, result, retired, id)))
        {
            Interlocked.Decrement(ref activeProducers);
            throw new InvalidOperationException("Oreslang Task scheduler queue closed or full");
        }
        return handle;
    }

    public OresFuture<T> FromTask<T>(OresTask<T> producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        if (!ReferenceEquals(producer.Scope, this))
            throw new InvalidOperationException("Cross-scope Task observation is not admitted");
        if (Volatile.Read(ref closed) != 0)
            throw new InvalidOperationException("Oreslang Task scope closed");
        return new OresFuture<T>(this, producer.ProducerId, producer.Completion);
    }

    internal Task<T> Observe<T>(Task<T> source, CancellationToken cancellation)
    {
        if (Volatile.Read(ref closed) != 0)
            throw new InvalidOperationException("Oreslang Task scope closed");
        Reserve(ref activeObservers, observerLimit, "observer");
        var projected = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Forward(source, projected, cancellation);
        return projected.Task; // A distinct, cancellation-isolated observer.
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
        if (cancelled)
            projection.TrySetCanceled(token.IsCancellationRequested
                ? token : new CancellationToken(canceled: true));
        else if (causes is { Count: > 0 }) projection.TrySetException(causes);
        else if (fault is not null) projection.TrySetException(fault);
        else projection.TrySetResult(value);
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
        catch (OperationCanceledException) when (producerStop.IsCancellationRequested)
        {
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
            await work().ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        producerStop.Cancel(); // Scope owns cancellation; observers do not.
        queue.Writer.TryComplete();
        await pump.ConfigureAwait(false);
        producerStop.Dispose();
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
