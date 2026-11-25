namespace Ores.Net.Runtime;

public sealed partial class OresVmHost
{
    // Trusted host substrate only. These types are not DATA, guest-mintable
    // capabilities, or an implementation of source-level supervisor receive.
    internal enum SupervisorTerminalKind { Stopped, Faulted }

    internal readonly record struct SupervisorTerminalNotice(
        ulong ChildId, ulong ChildEpoch, SupervisorTerminalKind Outcome);

    private sealed class SupervisorTerminalReservation
    {
        internal SupervisorTerminalReservation(ActorTicket parent) => Parent = parent;
        internal ActorTicket Parent { get; }
        internal SupervisorTerminalKind? Outcome { get; set; }
    }

    /// <summary>
    /// Register a child and reserve exactly one terminal record under the
    /// tenant gate. Undelivered notices count against the same bounded
    /// reservation quota, even after the child leaves the live actor table.
    /// A replacement with a reused ID obtains a distinct incarnation ticket.
    /// </summary>
    internal ActorTicket RegisterSupervisedActor(IsolateHandle handle,
        ActorTicket parent, ulong childId, bool serial = false, int maxLiveReceives = 32)
    {
        lock (_gate) {
            var state = Check(handle);
            CheckActor(state, parent);
            if (state.Phase != Phase.Running ||
                state.SupervisorTerminals.Count >= state.ActorLimit)
                throw new InvalidOperationException("supervisor terminal reservation capacity exceeded");

            // Allocate the reservation and dictionary capacity before child
            // admission, so ordinary admission rejection cannot leak a slot.
            var reservation = new SupervisorTerminalReservation(parent);
            state.SupervisorTerminals.EnsureCapacity(state.SupervisorTerminals.Count + 1);
            var child = RegisterActor(handle, childId, serial, maxLiveReceives);
            state.SupervisorTerminals.Add((child.Id, child.Epoch), reservation);
            return child;
        }
    }

    /// <summary>
    /// Scheduler-facing level-triggered readiness. One shared pending task
    /// per parent, never one waiter allocation per call. It carries no
    /// redemption authority and must be followed by exact-parent redemption.
    /// Continuations are queued asynchronously; no user code runs under _gate.
    /// </summary>
    internal Task WaitForSupervisorTerminalAsync(IsolateHandle handle, ActorTicket parent)
    {
        lock (_gate) {
            var state = Check(handle);
            CheckActor(state, parent);
            foreach (var reservation in state.SupervisorTerminals.Values)
                if (ReferenceEquals(reservation.Parent, parent) && reservation.Outcome.HasValue)
                    return Task.CompletedTask;
            parent.TerminalReady ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return parent.TerminalReady.Task;
        }
    }

    private static void SignalSupervisorTerminal(ActorTicket parent)
    {
        var ready = parent.TerminalReady;
        parent.TerminalReady = null;
        ready?.TrySetResult();
        SignalActorPump(parent);
    }

    private static void CloseSupervisorTerminalWait(ActorTicket parent)
    {
        var ready = parent.TerminalReady;
        parent.TerminalReady = null;
        ready?.TrySetCanceled();
    }

    // Match the bounded CONTROL preference used by the Java routing proposal.
    // This counts successful selections, not wall-clock time or guest work.
    internal const int SupervisorControlBurstLimit = 64;

    private static bool HasReadySupervisorTerminal(TenantState state, ActorTicket parent)
    {
        foreach (var reservation in state.SupervisorTerminals.Values)
            if (ReferenceEquals(reservation.Parent, parent) && reservation.Outcome.HasValue)
                return true;
        return false;
    }

    private static void CheckOrdinaryDispatchPriority(TenantState state, ActorTicket actor)
    {
        if (actor.SupervisorControlBurst < SupervisorControlBurstLimit &&
            HasReadySupervisorTerminal(state, actor))
            throw new InvalidOperationException("reserved supervisor CONTROL must be selected first");
    }

    // Scheduler-facing selection, atomic with the actor admission and FIFO
    // gates. A pending ordinary message/receipt gets a chance after 64
    // successful CONTROL selections. Raw teardown redemption remains a
    // separate trusted host API and is not subject to running dispatch policy.
    private bool TryTakeScheduledSupervisorTerminal(ActorScheduler scheduler,
        IsolateHandle handle, ActorTicket parent, out SupervisorTerminalNotice notice)
    {
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        lock (_gate) {
            var state = Check(handle);
            CheckActor(state, parent);
            if (state.Phase != Phase.Running || parent.ActiveTurn is not null)
                throw new InvalidOperationException("supervisor CONTROL selection admission closed");
            CheckNewReceiveAdmission(parent);
            notice = default;
            var canDispatchReceipt = parent.StateKinds is not null &&
                parent.StateLayoutIdentity is not null &&
                parent.NextTurnGeneration != ulong.MaxValue;
            if (parent.SupervisorControlBurst >= SupervisorControlBurstLimit &&
                parent.Mailbox.HasRunnableOrdinaryHead(canDispatchReceipt))
                return false;
            if (!TryTakeSupervisorTerminal(handle, parent, out notice))
                return false;
            if (parent.SupervisorControlBurst < SupervisorControlBurstLimit)
                parent.SupervisorControlBurst++;
            return true;
        }
    }

    /// <summary>
    /// Direct, exactly-once host redemption by the registered parent ticket.
    /// No DATA dequeue or bytes are required. This bounded scan does not
    /// promise FIFO terminal order or implement scheduler dispatch/fairness.
    /// May drain during quiescence before retiring the parent.
    /// </summary>
    internal bool TryTakeSupervisorTerminal(IsolateHandle handle,
        ActorTicket parent, out SupervisorTerminalNotice notice)
    {
        lock (_gate) {
            var state = Check(handle);
            CheckActor(state, parent);
            CheckPumpDispatchAuthority(parent);
            (ulong Id, ulong Epoch)? readyChild = null;
            notice = default;
            foreach (var entry in state.SupervisorTerminals) {
                if (ReferenceEquals(entry.Value.Parent, parent) &&
                    entry.Value.Outcome is { } outcome) {
                    readyChild = entry.Key;
                    notice = new SupervisorTerminalNotice(entry.Key.Id, entry.Key.Epoch, outcome);
                    break;
                }
            }
            if (readyChild is null) return false;
            state.SupervisorTerminals.Remove(readyChild.Value);
            return true;
        }
    }
}
