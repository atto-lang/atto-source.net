namespace Ores.Net.Runtime;

/// <summary>
/// Host-only composition of one owned F32 CPU slice with an independent
/// CPU-only I64 execution graph. No guest syntax, device transfer, implicit
/// F32/I64 conversion, actor carrier, or implicit CPU fallback is provided.
/// </summary>
public static class OwnedF32GraphV1
{
    public sealed class Ticket
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<long> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _cancelled;
        private bool _settled;
        private int _claimed;

        internal bool ClaimExecution() =>
            Interlocked.Exchange(ref _claimed, 1) == 0;

        internal bool Cancelled => _cancelled;
        public Task<long> Task => _result.Task;

        public bool Cancel()
        {
            lock (_gate)
            {
                if (_settled) return false;
                _settled = true;
                _cancelled = true;
                _result.TrySetCanceled();
                return true;
            }
        }

        internal void Complete(long value)
        {
            lock (_gate)
            {
                if (_settled) return;
                _settled = true;
                _result.TrySetResult(value);
            }
        }

        internal void Fail(Exception failure)
        {
            lock (_gate)
            {
                if (_settled) return;
                _settled = true;
                _result.TrySetException(failure);
            }
        }

        internal void RejectExecutor(Exception failure)
        {
            lock (_gate)
            {
                // A scheduler may enqueue a callback and then throw. Mark it
                // cancelled even after terminal settlement so a late callback
                // cannot acquire physical F32 loans or evaluate the graph.
                _cancelled = true;
                if (_settled) return;
                _settled = true;
                _result.TrySetException(failure);
            }
        }
    }

    /// <summary>
    /// Submit one checked F32 region followed by an independent host-I64 plan.
    /// The caller supplies a trusted bounded executor. Until schedule returns
    /// successfully, a worker callback cannot mutate the output allocation.
    /// Inline dispatch is rejected to avoid parking the submitting thread.
    /// </summary>
    public static Ticket Submit(
        OwnedF32RegionV1.HostBuffer input,
        OwnedF32RegionV1.HostBuffer output,
        OwnedF32RegionV1.Area source,
        OwnedF32RegionV1.Area destination,
        float scale,
        float bias,
        ExecutionGraphV1.Plan<long> scalar,
        Action<Action> schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var ticket = new Ticket();
        var admission = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int inlineAttempted = 0;
        bool accepted = false;
        int submittingThread = Environment.CurrentManagedThreadId;
        // Admission decision (0 pending, 1 accepted, 2 rejected), published before the
        // completion signal; callbacks never block on or read a Task result.
        int admittedState = 0;
        void RunAdmitted()
        {
            try
            {
                if (Volatile.Read(ref admittedState) != 1 || ticket.Cancelled) return;

                OwnedF32RegionV1.ExecuteCpuRegion(
                    input, output, source, destination, scale, bias);

                // Cancellation after the worker began cannot roll back
                // committed F32 bytes, but it suppresses graph/result
                // publication.
                if (ticket.Cancelled) return;
                long value = scalar.ExecuteCpu();
                if (!ticket.Cancelled) ticket.Complete(value);
            }
            catch (Exception ex) { ticket.Fail(ex); }
        }

        try
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(scalar);

            if (!scalar.CpuOnly ||
                scalar.OutputKind != ExecutionGraphV1.Kind.HostI64)
                throw new NotSupportedException(
                    "device/transfer graph or non-I64 result unsupported; no CPU fallback");

            if (ReferenceEquals(input, output) ||
                input.Shape != output.Shape ||
                !float.IsFinite(scale) || !float.IsFinite(bias) ||
                !input.Owns(source, OwnedF32RegionV1.Mode.Read) ||
                !output.Owns(destination, OwnedF32RegionV1.Mode.Write) ||
                source.Field != destination.Field ||
                source.Begin != destination.Begin ||
                source.End != destination.End)
                throw new ArgumentException("invalid owned F32 graph footprint");

            // The closure strongly owns both buffers. Area itself holds only a
            // WeakReference to backing storage and cannot extend allocation
            // lifetime without the explicit queued work owner.
            schedule(() =>
            {
                if (!ticket.ClaimExecution() || ticket.Cancelled) return;
                try
                {
                    if (!admission.Task.IsCompleted &&
                        Environment.CurrentManagedThreadId == submittingThread)
                    {
                        Interlocked.Exchange(ref inlineAttempted, 1);
                        return;
                    }

                    if (!admission.Task.IsCompleted)
                    {
                        // Async-only runtime boundary: never park this executor thread.
                        // The submitting call settles admission in its finally block;
                        // re-enter through the same executor once it has.
                        admission.Task.ContinueWith(
                            _ =>
                            {
                                try { schedule(RunAdmitted); }
                                catch (Exception ex) { ticket.RejectExecutor(ex); }
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        return;
                    }
                    RunAdmitted();
                }
                catch (Exception ex) { ticket.Fail(ex); }
            });

            if (Volatile.Read(ref inlineAttempted) != 0)
                ticket.RejectExecutor(new InvalidOperationException(
                    "owned F32 graph requires queued executor; inline dispatch rejected"));
            else
                accepted = true;
        }
        catch (Exception ex) { ticket.RejectExecutor(ex); }
        finally
        {
            Volatile.Write(ref admittedState, accepted ? 1 : 2);
            admission.TrySetResult(accepted);
        }

        return ticket;
    }
}
