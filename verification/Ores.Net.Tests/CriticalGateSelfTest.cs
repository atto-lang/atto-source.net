using Ores.Net.Runtime;

internal static class CriticalGateSelfTest
{
    private static void Require(bool value, string description)
    {
        if (!value) throw new Exception("CriticalGate: " + description);
    }

    public static void Run()
    {
        TryEnterIsImmediate();
        FifoHandoffAndCancellation();
        OldTicketCannotRevokeReplacement();
        ForeignTicketRejected();
        RetireFencesOldGeneration();
        WaiterBudgetIsBounded();
        CrossCarrierHandoffIsLogical();
        Console.WriteLine("PASS .NET critical gate host-only tickets/FIFO/cancellation/retirement");
    }

    private static void TryEnterIsImmediate()
    {
        var gate = new CriticalGate();
        var first = gate.TryEnter();
        Require(first is not null && gate.Holds(first), "first try admission");
        Require(gate.TryEnter() is null && gate.Waiting == 0, "contended try must not queue");
        Require(gate.Release(first!) is null, "unexpected successor");
        var next = gate.TryEnter();
        Require(next is not null && !ReferenceEquals(next, first), "ticket must not be reused");
        gate.Release(next!);
    }

    private static void FifoHandoffAndCancellation()
    {
        var gate = new CriticalGate();
        var first = gate.Request();
        var cancelled = gate.Request();
        var third = gate.Request();
        var fourth = gate.Request();
        Require(first.Kind == CriticalGate.AdmissionKind.Acquired, "first admitted");
        Require(cancelled.Kind == CriticalGate.AdmissionKind.Queued, "second queued");
        Require(third.Kind == CriticalGate.AdmissionKind.Queued, "third queued");
        Require(fourth.Kind == CriticalGate.AdmissionKind.Queued, "fourth queued");
        Require(gate.Release(cancelled.Ticket) is null, "queued cancellation");
        Require(gate.Waiting == 2, "cancelled ticket removed");
        Require(ReferenceEquals(gate.Release(first.Ticket), third.Ticket), "FIFO handoff to third");
        Require(gate.Holds(third.Ticket), "third owns gate");
        Require(ReferenceEquals(gate.Release(third.Ticket), fourth.Ticket), "FIFO handoff to fourth");
        Require(gate.Release(fourth.Ticket) is null && gate.Waiting == 0, "queue drains");
    }

    private static void OldTicketCannotRevokeReplacement()
    {
        var gate = new CriticalGate();
        var old = gate.Request();
        var next = gate.Request();
        Require(ReferenceEquals(gate.Release(old.Ticket), next.Ticket), "handoff");
        Require(gate.Release(old.Ticket) is null, "stale release is idempotent");
        Require(gate.Holds(next.Ticket), "old token stole new owner");
        gate.Release(next.Ticket);
    }

    private static void ForeignTicketRejected()
    {
        var left = new CriticalGate();
        var right = new CriticalGate();
        var token = left.Request().Ticket;
        bool rejected = false;
        try { right.Release(token); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected && left.Holds(token), "foreign ticket cannot release another gate");
        left.Release(token);
    }

    private static void RetireFencesOldGeneration()
    {
        var old = new CriticalGate();
        var owner = old.Request();
        var waiter = old.Request();
        old.Retire();
        Require(old.Retired && old.Waiting == 0, "retirement clears tickets");
        Require(!old.Holds(owner.Ticket) && !old.Holds(waiter.Ticket), "retired ticket invalid");
        Require(old.Release(owner.Ticket) is null &&
                old.Release(waiter.Ticket) is null, "stale completion cannot hand off");
        Require(old.TryEnter() is null, "retired try-entry rejected");
        bool rejected = false;
        try { old.Request(); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "retired request rejected");
        old.Retire();
        var replacement = new CriticalGate();
        Require(replacement.Request().Kind == CriticalGate.AdmissionKind.Acquired,
            "replacement generation is independent");
    }

    private static void WaiterBudgetIsBounded()
    {
        var gate = new CriticalGate();
        var owner = gate.Request();
        var first = gate.Request();
        for (int i = 1; i < CriticalGate.MaxWaiters; i++)
            Require(gate.Request().Kind == CriticalGate.AdmissionKind.Queued,
                "queued admission within quota");
        Require(gate.Waiting == CriticalGate.MaxWaiters, "queue reaches quota");
        bool rejected = false;
        try { gate.Request(); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && gate.Holds(owner.Ticket), "overflow does not evict owner");
        gate.Release(first.Ticket);
        Require(gate.Request().Kind == CriticalGate.AdmissionKind.Queued,
            "cancellation returns quota");
        gate.Retire();
        Require(gate.Waiting == 0, "retirement reclaims queue");
    }

    private static void CrossCarrierHandoffIsLogical()
    {
        var gate = new CriticalGate();
        var first = gate.Request();
        var second = gate.Request();
        CriticalGate.Ticket? granted = null;
        var thread = new Thread(() => granted = gate.Release(first.Ticket));
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(5)), "cross-carrier handoff completed");
        Require(ReferenceEquals(granted, second.Ticket) && gate.Holds(second.Ticket),
            "tickets are logical, not thread-affine");
        gate.Release(second.Ticket);
    }
}
