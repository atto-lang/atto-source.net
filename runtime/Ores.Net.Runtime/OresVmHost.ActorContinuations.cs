namespace Ores.Net.Runtime;

public sealed partial class OresVmHost
{
    // Host-only, one-shot continuation authority. Not serializable guest data.
    internal sealed class ActorContinuation
    {
        internal ActorContinuation(ActorScheduler scheduler, ActorTurnTicket turn)
        {
            Scheduler = scheduler;
            Turn = turn;
        }
        internal ActorScheduler Scheduler { get; }
        internal ActorTurnTicket Turn { get; }
    }

    // Each segment runs synchronously on its current carrier. A true result
    // requests suspension AFTER the delegate returns; it does not park a
    // carrier or retain a guest state loan. The scheduler owns the continuation.
    private ActorContinuation? StartScheduledSegment(ActorScheduler scheduler,
        IsolateHandle handle, ActorTicket actor, string expectedLayoutIdentity,
        Func<ActorDispatchScope, bool> segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentException.ThrowIfNullOrEmpty(expectedLayoutIdentity);
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        var lease = new object();
        ActorTurnTicket turn;
        lock (_gate) {
            var state = Check(handle);
            CheckActor(state, actor);
            if (state.Phase != Phase.Running || actor.ActiveTurn is not null ||
                actor.NextTurnGeneration == ulong.MaxValue ||
                actor.StateKinds is null ||
                !string.Equals(actor.StateLayoutIdentity, expectedLayoutIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException("scheduled segment admission denied");
            CheckNewReceiveAdmission(actor);
            CheckOrdinaryDispatchPriority(state, actor);
            if (!actor.Mailbox.TryReceiveEmptyDispatch())
                throw new InvalidOperationException("scheduled segment requires FIFO dispatch receipt");
            turn = BeginActorTurn(handle, actor);
            turn.ManagedContinuation = true;
            actor.SupervisorControlBurst = 0;
            InstallSegmentCarrier(turn, lease);
        }
        return ExecuteScheduledSegment(scheduler, handle, actor, turn, lease, segment);
    }

    // Trusted host-handler dispatch, not compiler lowering. The terminal
    // selection and logical admission are atomic; the handler runs outside
    // the host gate. Once selected the notice is not replayed on handler fault.
    private bool TryDispatchSupervisorSegment(ActorScheduler scheduler,
        IsolateHandle handle, ActorTicket actor, string expectedLayoutIdentity,
        Action<ActorDispatchScope, SupervisorTerminalNotice> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrEmpty(expectedLayoutIdentity);
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        var lease = new object();
        ActorTurnTicket turn;
        SupervisorTerminalNotice notice;
        lock (_gate) {
            var state = Check(handle);
            CheckActor(state, actor);
            if (state.Phase != Phase.Running || actor.ActiveTurn is not null ||
                actor.NextTurnGeneration == ulong.MaxValue ||
                actor.StateKinds is null ||
                !string.Equals(actor.StateLayoutIdentity, expectedLayoutIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException("supervisor handler admission denied");
            CheckNewReceiveAdmission(actor);
            if (!TryTakeScheduledSupervisorTerminal(scheduler, handle, actor, out notice))
                return false;
            turn = BeginActorTurn(handle, actor);
            turn.ManagedContinuation = true;
            InstallSegmentCarrier(turn, lease);
        }
        ExecuteScheduledSegment(scheduler, handle, actor, turn, lease, scope => {
            handler(scope, notice);
            return false;
        });
        return true;
    }

    private static void InstallSegmentCarrier(ActorTurnTicket turn, object lease)
    {
        turn.ScheduledCarrierLease = lease;
        turn.ScheduledCarrierThread = Environment.CurrentManagedThreadId;
    }

    private static void ValidateContinuation(ActorScheduler scheduler,
        ActorTicket actor, ActorContinuation continuation)
    {
        if (!ReferenceEquals(continuation.Scheduler, scheduler) ||
            !actor.LiveTurns.Contains(continuation.Turn) ||
            !ReferenceEquals(continuation.Turn.PendingContinuation, continuation) ||
            !continuation.Turn.ManagedContinuation || !continuation.Turn.Suspended ||
            continuation.Turn.ScheduledCarrierLease is not null)
            throw new InvalidOperationException("stale or foreign continuation");
    }

    private ActorContinuation? ResumeScheduledSegment(ActorScheduler scheduler,
        IsolateHandle handle, ActorTicket actor, ActorContinuation continuation,
        Func<ActorDispatchScope, bool> segment)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(segment);
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        var lease = new object();
        ActorTurnTicket turn = continuation.Turn;
        lock (_gate) {
            var state = Check(handle);
            CheckTurn(state, actor, turn);
            CheckPumpDispatchAuthority(actor);
            ValidateContinuation(scheduler, actor, continuation);
            if (turn.CancellationRequested || state.Phase != Phase.Running ||
                actor.ActiveTurn is not null)
                throw new InvalidOperationException("continuation admission closed");
            // Consume the old ticket and install fresh physical authority in
            // one critical section. A concurrent replay cannot run a delegate.
            turn.PendingContinuation = null;
            turn.Suspended = false;
            actor.ActiveTurn = turn;
            InstallSegmentCarrier(turn, lease);
        }
        return ExecuteScheduledSegment(scheduler, handle, actor, turn, lease, segment);
    }

    // Distinguish a trusted retirement acknowledgement from a guest
    // throwing OperationCanceledException as its own observable outcome.
    private sealed class ScheduledActorTurnCancelledException : OperationCanceledException
    {
        internal ScheduledActorTurnCancelledException()
            : base("scheduled actor segment cancelled") { }
    }

    private ActorContinuation? ExecuteScheduledSegment(ActorScheduler scheduler,
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn, object lease,
        Func<ActorDispatchScope, bool> segment)
    {
        ActorStateLoan? loan = null;
        ActorDispatchScope? scope = null;
        ActorContinuation? next;
        try {
            loan = BeginActorStateLoan(handle, actor, turn, writable: true);
            scope = new ActorDispatchScope(this, handle, actor, turn, loan);
            bool suspend = segment(scope);
            scope.Revoke();
            EndActorStateLoan(handle, actor, turn, loan);
            next = RetireSegment(scheduler, handle, actor, turn, lease, suspend);
        }
        catch {
            scope?.Revoke();
            CancelActorTurn(handle, actor, turn);
            if (loan is not null && !loan.Released)
                EndActorStateLoan(handle, actor, turn, loan);
            RetireSegment(scheduler, handle, actor, turn, lease, suspend: false);
            throw;
        }
        // A cancellation racing a normal return must not look like success.
        // If a new suspended ticket was published, return that exact ticket;
        // its owner must explicitly discard it if cancellation wins later.
        if (next is null && turn.CancellationRequested)
            throw new ScheduledActorTurnCancelledException();
        return next;
    }

    private ActorContinuation? RetireSegment(ActorScheduler scheduler,
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn, object lease,
        bool suspend)
    {
        lock (_gate) {
            var state = Check(handle);
            CheckTurn(state, actor, turn);
            if (!turn.ManagedContinuation || !ReferenceEquals(actor.ActiveTurn, turn) ||
                !ReferenceEquals(turn.ScheduledCarrierLease, lease) ||
                turn.ScheduledCarrierThread != Environment.CurrentManagedThreadId ||
                turn.ActiveStateLoan is not null || turn.StateLoanDepth != 0)
                throw new InvalidOperationException("scheduled segment has not retired");
            bool cancelled = turn.CancellationRequested;
            // Allocate the next continuation before changing authority.
            var next = suspend && !cancelled ? new ActorContinuation(scheduler, turn) : null;
            turn.ScheduledCarrierLease = null;
            turn.ScheduledCarrierThread = 0;
            if (next is not null) {
                turn.Suspended = true;
                turn.PendingContinuation = next;
                actor.ActiveTurn = null;
                SignalActorPump(actor);
                return next;
            }
            turn.ManagedContinuation = false;
            turn.PendingContinuation = null;
            if (cancelled) AcknowledgeActorTurnCancellation(handle, actor, turn);
            else CompleteActorTurn(handle, actor, turn);
            return null;
        }
    }

    // A suspended continuation has no live physical carrier. Only its exact
    // owning scheduler/ticket may discard it after cancellation is requested.
    private void DiscardScheduledContinuation(ActorScheduler scheduler,
        IsolateHandle handle, ActorTicket actor, ActorContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        lock (_gate) {
            var state = Check(handle);
            var turn = continuation.Turn;
            CheckTurn(state, actor, turn);
            ValidateContinuation(scheduler, actor, continuation);
            if (!turn.CancellationRequested || turn.ActiveStateLoan is not null ||
                turn.StateLoanDepth != 0)
                throw new InvalidOperationException("continuation cancellation not ready");
            turn.PendingContinuation = null;
            turn.ManagedContinuation = false;
            AcknowledgeActorTurnCancellation(handle, actor, turn);
        }
    }
}
