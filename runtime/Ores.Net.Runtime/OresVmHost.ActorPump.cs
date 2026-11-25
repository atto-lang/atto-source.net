namespace Ores.Net.Runtime;

public sealed partial class OresVmHost
{
    // Trusted host state-machine ABI: null completes the receive; otherwise
    // the next segment waits on a task without retaining a physical carrier.
    // The readiness task is not execution authority or a guest Future ABI.
    internal sealed record ActorPumpStep(Task Ready, Func<ActorDispatchScope, ActorPumpStep?> Next);
    private sealed record PendingPumpStep(ActorContinuation Ticket, ActorPumpStep Step);

    private static void SignalActorPump(ActorTicket actor)
    {
        var wake = actor.PumpReady;
        actor.PumpReady = null;
        wake?.TrySetResult();
    }

    // One opt-in pump per configured actor. External trusted hosts must not
    // concurrently drive the same actor manually. All actual selection and
    // admission are atomic with the host gate regardless of wakeup order.
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
        lock (_gate) {
            var state = Check(handle);
            CheckActor(state, actor);
            if (state.Phase != Phase.Running || actor.PumpOwner is not null ||
                actor.LiveTurns.Count != 0 || actor.StateKinds is null ||
                !string.Equals(actor.StateLayoutIdentity, layout, StringComparison.Ordinal))
                throw new InvalidOperationException("actor pump admission denied");
            actor.PumpOwner = scheduler;
        }

        bool preferResume = false;
        int quantum = 0;
        try {
            // Do not run guest/host handler code inline with pump startup.
            await Task.Yield();
            while (true) {
                stop.ThrowIfCancellationRequested();
                ActorTurnTicket? turn = null;
                object? lease = null;
                Func<ActorDispatchScope, ActorPumpStep?>? segment = null;
                Task[]? waits = null;
                lock (_gate) {
                    var state = Check(handle);
                    CheckActor(state, actor);
                    if (state.Phase != Phase.Running) break;
                    // A cancelled suspended receive can be discarded without
                    // waiting for its external readiness task or running sibling.
                    for (int i = pending.Count - 1; i >= 0; i--) {
                        var item = pending[i];
                        if (!item.Ticket.Turn.CancellationRequested) continue;
                        DiscardScheduledContinuation(scheduler, handle, actor, item.Ticket);
                        pending.RemoveAt(i);
                    }
                    int readyIndex = pending.FindIndex(item => item.Step.Ready.IsCompleted);
                    if (actor.ActiveTurn is null) {
                        bool canStart = actor.LiveTurns.Count < actor.MaxLiveReceives &&
                            actor.NextTurnGeneration != ulong.MaxValue;
                        bool ordinaryReady = canStart && actor.Mailbox.HasRunnableOrdinaryHead(true);
                        bool controlReady = canStart && HasReadySupervisorTerminal(state, actor) &&
                            (actor.SupervisorControlBurst < SupervisorControlBurstLimit || !ordinaryReady);
                        bool chooseResume = readyIndex >= 0 &&
                            (preferResume || (!controlReady && !ordinaryReady));
                        if (chooseResume) {
                            var item = pending[readyIndex];
                            // Completed fault/cancellation terminates this pump;
                            // finally discards all of its still-pending tickets.
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
                    if (turn is null) {
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

                if (turn is null) {
                    await Task.WhenAny(waits!).WaitAsync(stop).ConfigureAwait(false);
                    quantum = 0;
                    continue;
                }

                ActorPumpStep? next = null;
                var continuation = ExecuteScheduledSegment(scheduler, handle, actor, turn, lease!, scope => {
                    next = segment!(scope);
                    if (next is null) return false;
                    ArgumentNullException.ThrowIfNull(next.Ready);
                    ArgumentNullException.ThrowIfNull(next.Next);
                    return true;
                });
                if (continuation is not null)
                    pending.Add(new PendingPumpStep(continuation, next!));
                if (++quantum == 64) {
                    quantum = 0;
                    await Task.Yield();
                }
            }
        }
        finally {
            lock (_gate) {
                try {
                    // No guest code runs here; each tracked continuation
                    // has already physically suspended. Never clear a running
                    // sibling's state authority or abandon a live ticket.
                    foreach (var item in pending) {
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
