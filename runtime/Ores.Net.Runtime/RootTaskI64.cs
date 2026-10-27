namespace Ores.Net.Runtime;

/// <summary>
/// Soft-RT root-only executor for closed, capture-free, non-suspending i64
/// producers. Shared bounded admission; no actor lane or hard-RT claim.
/// </summary>
public static class RootTaskI64
{
    private const int Capacity = 64;
    private static readonly object Gate = new();
    private static readonly Owner?[] Queue = new Owner[Capacity];
    private static int head, count, active;
    private static bool pumping;

    public sealed class Owner
    {
        private readonly object stateGate = new();
        private Func<long>? producer;
        private bool claimed, cancelled;
        private int executions;
        private int observed;
        private readonly TaskCompletionSource<long> result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource retired =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Owner(Func<long> producer) => this.producer = producer;
        public Task PhysicalDone => retired.Task;
        public int ProducerExecutions => Volatile.Read(ref executions);

        // Only the owning root scope receives Owner. Views never receive it.
        // The finite source producer cannot suspend: cancellation before claim
        // prevents execution; a running producer runs to its bounded return.
        public bool CancelBeforeClaim()
        {
            lock (stateGate)
            {
                if (claimed) return false;
                cancelled = true;
                return true;
            }
        }

        public Future Observe()
        {
            if (Interlocked.CompareExchange(ref observed, 1, 0) != 0)
                throw new InvalidOperationException("Root Task-i64 profile permits one observer");
            return new Future(result.Task);
        }

        internal void Execute()
        {
            Func<long>? work;
            lock (stateGate)
            {
                claimed = true;
                work = cancelled ? null : producer;
            }
            if (work is null) { result.TrySetCanceled(); return; }
            try
            {
                Interlocked.Increment(ref executions);
                result.TrySetResult(work());
            }
            catch (Exception error)
            {
                // A producer throwing OperationCanceledException is FAILED.
                // Cancellation can only originate from CancelBeforeClaim.
                result.TrySetException(error);
            }
        }

        internal void Retire()
        {
            // Called only AFTER Execute returned: no producer stack remains.
            lock (stateGate) producer = null;
            retired.TrySetResult();
        }
    }

    /// <summary>Nominal non-owning result capability; contains no Owner/token.</summary>
    public sealed class Future
    {
        private Task<long>? source;
        private int awaited;
        internal Future(Task<long> result)
        {
            source = result;
        }
        public bool IsDistinctProjection { get; private set; }
        public Task<long> AwaitAsync(CancellationToken observerCancellation = default)
        {
            // Cancellation detaches this waiter, not the producer or sibling
            // observers. Guest bindings are closed and cannot export raw Tasks.
            if (Interlocked.CompareExchange(ref awaited, 1, 0) != 0)
                throw new InvalidOperationException("Root Task-i64 profile permits one await");
            // Return the actual faulted Task. An async Task wrapper would turn
            // a faulted user OperationCanceledException into CLR cancellation.
            Task<long> producerResult = Interlocked.Exchange(ref source, null)!;
            Task<long> observation = new HostTaskObservationScope(1).Observe(producerResult, observerCancellation);
            IsDistinctProjection = !ReferenceEquals(observation, producerResult);
            return observation;
        }
    }

    public static Owner Start(Func<long> producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        lock (Gate)
        {
            if (active >= Capacity)
                throw new InvalidOperationException("Root Task-i64 admission quota exhausted");
            var owner = new Owner(producer);
            Queue[(head + count) % Capacity] = owner;
            count++; active++;
            if (!pumping)
            {
                pumping = true;
                try
                {
                    // Runtime carrier only; no ExecutionContext/actor authority
                    // flows into this root producer. Never invoke guest work in
                    // registration or under the admission lock.
                    if (!ThreadPool.UnsafeQueueUserWorkItem(static _ => Pump(), null))
                        throw new InvalidOperationException("Root Task-i64 carrier rejected admission");
                }
                catch
                {
                    pumping = false;
                    Queue[(head + --count) % Capacity] = null;
                    active--;
                    throw;
                }
            }
            return owner;
        }
    }

    private static void Pump()
    {
        for (;;)
        {
            Owner owner;
            lock (Gate)
            {
                if (count == 0) { pumping = false; return; }
                owner = Queue[head]!;
                Queue[head] = null;
                head = (head + 1) % Capacity;
                count--;
            }
            try { owner.Execute(); }
            finally
            {
                // Result settlement may precede this physical body-return
                // receipt. No producer can execute after this point.
                lock (Gate) active--;
                owner.Retire();
            }
        }
    }

    public sealed record Receipt(long Value, int ProducerExecutions,
        bool ObserverDistinct, bool ProducerBodyRetired);

    // Static native entry shim for the exact source profile. The emitted CIL
    // method supplies the verified producer, never source text or an evaluator.
    public static Task<Receipt> RunAsync(Func<long> compiledProducer)
    {
        var completion = new TaskCompletionSource<Receipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        Owner owner = Start(compiledProducer);
        _ = CompleteRoot(owner, completion);
        return completion.Task;
    }

    private static async Task CompleteRoot(Owner owner, TaskCompletionSource<Receipt> completion)
    {
        long value = 0;
        Future? future = null;
        Exception? failure = null;
        try
        {
            future = owner.Observe();
            value = await future.AwaitAsync().ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // This is the actual owned producer's receipt, not the view result.
            await owner.PhysicalDone.ConfigureAwait(false);
        }
        if (failure is not null) completion.TrySetException(failure);
        else completion.TrySetResult(new Receipt(value, owner.ProducerExecutions,
            future!.IsDistinctProjection, owner.PhysicalDone.IsCompletedSuccessfully));
    }
}
