using Ores.Net.Runtime;

internal static class SupervisorStopLedgerSelfTest
{
    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception("STOP ledger: " + why);
    }

    private static void Reject(Action operation, string why)
    {
        try { operation(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("STOP ledger admitted: " + why);
    }

    internal static void Run()
    {
        var supervisor = Guid.NewGuid();
        var group = Guid.NewGuid();
        var ledger = new SupervisorStopLedger(supervisor, group, 2);
        var mailbox = new ActorMailbox(617);
        var actor = mailbox.CurrentRef;
        var child = ledger.Register(actor);
        Require(ledger.Snapshot(child, actor) == SupervisorStopLedger.Phase.Active,
            "registration not active");
        Reject(() => ledger.ReserveStop(child, actor), "STOP before finalization");
        var wrong = new ActorMailbox(618).CurrentRef;
        Reject(() => ledger.AssertPhysicallyFinalized(child, wrong), "foreign actor finalization");
        ledger.AssertPhysicallyFinalized(child, actor);
        Reject(() => ledger.AssertPhysicallyFinalized(child, actor), "duplicate finalization");
        var permit = ledger.ReserveStop(child, actor);
        Require(permit.Incarnation == actor.Generation && permit.Turn == 1,
            "incorrect STOP incarnation/turn");
        Reject(() => ledger.ReserveStop(child, actor), "duplicate STOP reservation");
        Reject(() => ledger.MarkApplied(permit), "host receipt without invocation");
        ledger.ClaimHostInvocation(permit);
        Reject(() => ledger.ClaimHostInvocation(permit), "duplicate host invocation");
        ledger.MarkUncertain(permit);
        ledger.MarkUncertain(permit);
        Require(ledger.StopReceipt(permit) == SupervisorStopLedger.Receipt.Uncertain,
            "uncertainty not retained");
        var forged = permit with { Nonce = Guid.NewGuid() };
        Reject(() => ledger.ReconcileVerifiedStop(forged), "forged STOP nonce");
        var wrongTurn = permit with { Turn = permit.Turn + 1 };
        Reject(() => ledger.ReconcileVerifiedStop(wrongTurn), "replayed/foreign turn");
        Reject(() => ledger.AcknowledgeApplied(permit), "uncertain STOP falsely acknowledged");
        ledger.ReconcileVerifiedStop(permit);
        Require(ledger.Snapshot(child, actor) == SupervisorStopLedger.Phase.Stopped,
            "confirmed STOP not terminal");
        Reject(() => ledger.ReconcileVerifiedStop(permit), "STOP receipt replay");
        Reject(() => ledger.ReserveStop(child, actor), "reopened stopped incarnation");

        var secondMailbox = new ActorMailbox(619);
        var secondActor = secondMailbox.CurrentRef;
        var second = ledger.Register(secondActor);
        ledger.AssertPhysicallyFinalized(second, secondActor);
        var secondPermit = ledger.ReserveStop(second, secondActor);
        Require(secondPermit.Turn == 2, "CONTROL turn must monotonically increase");
        ledger.ClaimHostInvocation(secondPermit);
        ledger.MarkApplied(secondPermit);
        Reject(() => ledger.MarkUncertain(secondPermit), "confirmed receipt downgraded");
        ledger.AcknowledgeApplied(secondPermit);
        Require(ledger.Snapshot(second, secondActor) == SupervisorStopLedger.Phase.Stopped,
            "applied STOP receipt not acknowledged");
        Reject(() => ledger.Register(new ActorMailbox(620).CurrentRef),
            "bounded child capacity");
        Reject(() => ledger.AssertPhysicallyFinalized(
            new SupervisorStopLedger.Child(Guid.NewGuid(), group, child.LogicalId), actor),
            "forged supervisor domain");
        Require(!permit.ToString().Contains(permit.Nonce.ToString(),
            StringComparison.OrdinalIgnoreCase), "STOP nonce leaked through ToString");
        // Competing host CONTROL callbacks must linearize through one
        // invocation claim. Every loser sees the exact same reserved ticket
        // but cannot call the host effect a second time.
        var competing = new SupervisorStopLedger(supervisor, Guid.NewGuid(), 1);
        var competingActor = new ActorMailbox(711).CurrentRef;
        var competingChild = competing.Register(competingActor);
        competing.AssertPhysicallyFinalized(competingChild, competingActor);
        var competingTicket = competing.ReserveStop(competingChild, competingActor);
        int winners = 0;
        Parallel.For(0, 64, _ =>
        {
            try
            {
                competing.ClaimHostInvocation(competingTicket);
                Interlocked.Increment(ref winners);
            }
            catch (InvalidOperationException) { }
        });
        Require(winners == 1, "concurrent callers claimed duplicate host STOP");
        competing.MarkUncertain(competingTicket);
        competing.ReconcileVerifiedStop(competingTicket);
        Require(competing.Snapshot(competingChild, competingActor)
                == SupervisorStopLedger.Phase.Stopped, "contended STOP did not reconcile");
        Console.WriteLine("NET_SUPERVISOR_STOP_LEDGER=PASS cases=3");
    }
}
