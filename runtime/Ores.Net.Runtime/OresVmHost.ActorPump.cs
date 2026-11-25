namespace Ores.Net.Runtime;

public sealed partial class OresVmHost
{
    // Trusted host state-machine ABI: null completes the receive; otherwise
    // the next segment waits on a task without retaining a physical carrier.
    // The readiness task is not execution authority or a guest Future ABI.
    internal sealed record ActorPumpStep(Task Ready, Func<ActorDispatchScope, ActorPumpStep?> Next,
        ActorScalarTask? Producer = null, bool ObserveFaultInSegment = false);

    // A single capture-free scalar producer per suspended receive, executed
    // as its own work item on the same bounded actor domain. No Task.Run,
    // blocking wait, guest state scope or additional carrier reservation.
    internal sealed class ActorScalarTask
    {
        private readonly Func<long> _body;
        private readonly TaskCompletionSource<long> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimed;
        internal ActorScalarTask(Func<long> body) =>
            _body = body ?? throw new ArgumentNullException(nameof(body));
        internal Task<long> Future => _completion.Task;
        internal void Claim()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
                throw new InvalidOperationException("scalar producer replay");
        }
        internal void Run()
        {
            Claim();
            RunClaimed();
        }
        internal void RunClaimed()
        {
            if (Interlocked.CompareExchange(ref _claimed, 2, 1) != 1)
                throw new InvalidOperationException("scalar producer not admitted or replayed");
            try { _completion.TrySetResult(_body()); }
            catch (Exception error) { _completion.TrySetException(error); }
        }
        internal void CancelUnstarted()
        {
            // Cancelled is distinct from admitted: RunClaimed must not
            // execute a producer whose cancellation won admission.
            if (Interlocked.CompareExchange(ref _claimed, 3, 0) == 0)
                _completion.TrySetCanceled();
        }
    }
    private sealed record PendingPumpStep(ActorContinuation Ticket, ActorPumpStep Step);

    private static void SignalActorPump(ActorTicket actor)
    {
        var wake = actor.PumpReady;
        actor.PumpReady = null;
        wake?.TrySetResult();
    }

    private static void CheckPumpDispatchAuthority(ActorTicket actor)
    {
        if (actor.PumpOwner is not null && actor.PumpCarrierThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("actor is owned by automatic scheduler pump");
    }

    // One opt-in pump per configured actor. Manual dispatch from another
    // thread cannot consume its DATA, receipts, or reserved CONTROL.
    private async Task RunActorPumpAsync(ActorScheduler scheduler,
        IsolateHandle handle, ActorTicket actor, string layout,
        Func<ActorDispatchScope, ReadOnlyMemory<byte>, ActorPumpStep?> onData,
        Func<ActorDispatchScope, ActorPumpStep?> onReceipt,
        Action<ActorDispatchScope, SupervisorTerminalNotice> onTerminal,
        CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(onData);
        ArgumentNullException.ThrowIfNull(onReceipt);
        ArgumentNullException.ThrowIfNull(onTerminal);
        ArgumentException.ThrowIfNullOrEmpty(layout);
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        var pending = new List<PendingPumpStep>();
        ActorDispatchDomains domains;
        lock (_gate) {
            var state = Check(handle);
            CheckActor(state, actor);
            if (state.Phase != Phase.Running || actor.PumpOwner is not null ||
                actor.LiveTurns.Count != 0 || actor.StateKinds is null ||
                !string.Equals(actor.StateLayoutIdentity, layout, StringComparison.Ordinal))
                throw new InvalidOperationException("actor pump admission denied");
            // One queued/running work item per actor pump, with queue capacity
            // equal to the tenant actor limit. No suspended receive owns a lane.
            domains = state.PumpDomains ??= new ActorDispatchDomains(new ActorDispatcherOptions {
                PrivateWorkers = state.ActorWorkerLimit,
                SharedWorkers = 1,
                UntrustedWorkers = 1,
                QueueCapacityPerDomain = state.ActorLimit
            });
            actor.PumpOwner = scheduler;
        }

        bool preferResume = false;
        bool preferProducer = false;
        Task[]? RunOne()
        {
            stop.ThrowIfCancellationRequested();
            ActorTurnTicket? turn = null;
            object? lease = null;
            Func<ActorDispatchScope, ActorPumpStep?>? segment = null;
            Task[]? waits = null;
            ActorScalarTask? producer = null;
            lock (_gate) {
                var state = Check(handle);
                CheckActor(state, actor);
                if (state.Phase != Phase.Running) return Array.Empty<Task>();
                // A cancelled suspended receive can be discarded without
                // waiting for its external readiness task or running sibling.
                for (int i = pending.Count - 1; i >= 0; i--) {
                    var item = pending[i];
                    if (!item.Ticket.Turn.CancellationRequested) continue;
                    item.Step.Producer?.CancelUnstarted();
                    DiscardScheduledContinuation(scheduler, handle, actor, item.Ticket);
                    pending.RemoveAt(i);
                }
                int readyIndex = pending.FindIndex(item => item.Step.Ready.IsCompleted);
                int producerIndex = pending.FindIndex(item => item.Step.Producer is not null && !item.Step.Ready.IsCompleted);
                if (actor.ActiveTurn is null) {
                    bool canStart = actor.LiveTurns.Count < actor.MaxLiveReceives &&
                        actor.NextTurnGeneration != ulong.MaxValue;
                    bool ordinaryReady = canStart && actor.Mailbox.HasRunnableOrdinaryHead(true);
                    bool controlReady = canStart && HasReadySupervisorTerminal(state, actor) &&
                        (actor.SupervisorControlBurst < SupervisorControlBurstLimit || !ordinaryReady);
                    bool chooseResume = readyIndex >= 0 &&
                        (preferResume || (!controlReady && !ordinaryReady));
                    bool chooseProducer = producerIndex >= 0 &&
                        (preferResume || (!controlReady && !ordinaryReady));
                    if (chooseResume && (!chooseProducer || !preferProducer)) {
                        var item = pending[readyIndex];
                        // Opt-in generated await observes the original typed CLR
                        // outcome on its fresh carrier, outside the host gate.
                        // Legacy callbacks retain the fail-closed success gate.
                        if (!item.Step.ObserveFaultInSegment)
                            item.Step.Ready.GetAwaiter().GetResult();
                        ValidateContinuation(scheduler, actor, item.Ticket);
                        lease = new object();
                        turn = item.Ticket.Turn;
                        turn.PendingContinuation = null;
                        turn.Suspended = false;
                        actor.ActiveTurn = turn;
                        InstallSegmentCarrier(turn, lease);
                        segment = item.Step.Next;
                        pending.RemoveAt(readyIndex);
                        preferResume = false;
                        preferProducer = true;
                    } else if (chooseProducer) {
                        producer = pending[producerIndex].Step.Producer!;
                        // Admission and parent cancellation are ordered by _gate.
                        producer.Claim();
                        preferResume = false;
                        preferProducer = false;
                    } else if (controlReady || ordinaryReady) {
                        lease = new object();
                        if (controlReady) {
                            if (!TryTakeScheduledSupervisorTerminal(scheduler, handle, actor, out var notice))
                                throw new InvalidOperationException("terminal selection changed under host gate");
                            segment = scope => { onTerminal(scope, notice); return null; };
                        } else {
                            if (TryReceiveActor(handle, actor, out var payload) && payload is not null)
                                segment = scope => onData(scope, payload);
                            else if (actor.Mailbox.TryReceiveEmptyDispatch())
                                segment = onReceipt;
                            else throw new InvalidOperationException("ordinary selection changed under host gate");
                            actor.SupervisorControlBurst = 0;
                        }
                        turn = BeginActorTurn(handle, actor);
                        turn.ManagedContinuation = true;
                        InstallSegmentCarrier(turn, lease);
                        preferResume = true;
                    }
                }
                if (turn is null && producer is null) {
                    // Register under the same gate used for publication:
                    // no empty-check/subscribe race. Do not wait on an
                    // already-completed task while a different turn runs.
                    actor.PumpReady ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    var wake = actor.PumpReady.Task;
                    waits = actor.ActiveTurn is not null
                        ? new[] { wake }
                        : pending.Select(item => item.Step.Ready).Append(wake).ToArray();
                }
            }

            if (producer is not null) {
                producer.RunClaimed(); // outside gate; no active actor state lease
                return null;
            }
            if (turn is null) return waits!;

            ActorPumpStep? next = null;
            var continuation = ExecuteScheduledSegment(scheduler, handle, actor, turn, lease!, scope => {
                next = segment!(scope);
                if (next is null) return false;
                ArgumentNullException.ThrowIfNull(next.Ready);
                ArgumentNullException.ThrowIfNull(next.Next);
                if (next.Producer is not null && !ReferenceEquals(next.Ready, next.Producer.Future))
                    throw new InvalidOperationException("producer readiness identity mismatch");
                return true;
            });
            if (continuation is not null)
                pending.Add(new PendingPumpStep(continuation, next!));
            return null; // exactly one execution segment per queued item
        }

        try {
            while (true) {
                stop.ThrowIfCancellationRequested();
                var completion = new TaskCompletionSource<Task[]?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!domains.TryDispatch(ActorKind.Private, () => {
                    Task[]? result = null;
                    Exception? failure = null;
                    lock (_gate) actor.PumpCarrierThread = Environment.CurrentManagedThreadId;
                    try { result = RunOne(); }
                    catch (Exception error) { failure = error; }
                    finally {
                        lock (_gate) actor.PumpCarrierThread = 0;
                    }
                    // Publish only after clearing physical pump authority.
                    if (failure is null) completion.TrySetResult(result);
                    else completion.TrySetException(failure);
                }))
                    throw new InvalidOperationException("tenant actor carrier admission denied");

                // An admitted work item must retire even when stop is requested.
                // Cancellation is observed inside RunOne, never by abandoning
                // its completion while a physical worker may still execute.
                var waits = await completion.Task.ConfigureAwait(false);
                if (waits is null) continue;
                if (waits.Length == 0) break;
                await Task.WhenAny(waits).WaitAsync(stop).ConfigureAwait(false);
            }
        }
        finally {
            lock (_gate) {
                try {
                    // No guest code runs here; each tracked continuation
                    // has already physically suspended. Never clear a running
                    // sibling's state authority or abandon a live ticket.
                    foreach (var item in pending) {
                        item.Step.Producer?.CancelUnstarted();
                        CancelActorTurn(handle, actor, item.Ticket.Turn);
                        DiscardScheduledContinuation(scheduler, handle, actor, item.Ticket);
                    }
                }
                finally {
                    actor.PumpOwner = null;
                    SignalActorPump(actor);
                }
            }
        }
    }
}
