using Atto.Runtime.Proxy;
using System;

static class Program
{
    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("proxy receipt gate regression");
    }
    private static ReceiptIdentityV1 Route(ulong id, ulong incarnation = 14) =>
        new(1, 11, 12, 13, incarnation, 17, 15, id, 1);

    private static HostMailboxTicketV1 Admit(
        HostProxyMailboxTicketsV1 mailbox, ReceiptIdentityV1 route, long value)
    {
        var a = mailbox.TrySendTicket(route, value);
        Check(a.Status == HostTicketAdmissionStateV1.Admitted && a.Ticket is not null);
        return a.Ticket!;
    }

    private static void HostMailboxTickets()
    {
        var a = new HostProxyMailboxTicketsV1(13, 14, 1);
        var b = new HostProxyMailboxTicketsV1(13, 14, 1);
        var first = Admit(a, Route(101), 11);
        var firstTurn = a.Begin()!;
        Check(firstTurn.Value == 11);
        var queued = Admit(a, Route(102), 12);
        Check(a.TrySendTicket(Route(103), 13).Status == HostTicketAdmissionStateV1.Full);
        var other = Admit(b, Route(104), 70);
        var otherTurn = b.Begin()!;
        Check(b.Commit(otherTurn, 140));
        Check(b.Snapshot(other)!.Result == 140);
        Check(a.Snapshot(first)!.State == HostTicketStateV1.Started);
        Check(a.Snapshot(queued)!.State == HostTicketStateV1.Admitted);
        Check(!a.Commit(otherTurn, 999));
        Check(!b.Commit(firstTurn, 999));
        Check(a.Snapshot(other) is null);
        Check(a.TrySendTicket(Route(105, 99), 1).Status == HostTicketAdmissionStateV1.Stale);
        Check(a.TrySendTicket(Route(105) with { Tenant = 0 }, 1).Status ==
            HostTicketAdmissionStateV1.Invalid);
        Check(a.Commit(firstTurn, 111));
        var queuedTurn = a.Begin()!;
        Check(queuedTurn.Value == 12 && a.Commit(queuedTurn, 112));
        Check(a.Snapshot(first)!.Result == 111 && a.Snapshot(queued)!.Result == 112);
        Check(a.CheckConservation() && b.CheckConservation());

        var parallel = new HostProxyMailboxTicketsV1(13, 14, 256);
        var delivered = new System.Collections.Concurrent.ConcurrentBag<
            (long Input, HostMailboxTicketV1 Ticket)>();
        var producers = new System.Threading.Tasks.Task[4];
        for (int p = 0; p < 4; p++)
        {
            int partition = p;
            producers[p] = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 32; i++)
                {
                    long value = partition * 1000L + i + 1;
                    ulong id = (ulong)(partition * 1000 + i + 3000);
                    delivered.Add((value, Admit(parallel, Route(id), value)));
                }
            });
        }
        System.Threading.Tasks.Task.WaitAll(producers);
        Check(delivered.Count == 128);
        for (int i = 0; i < 128; i++)
        {
            var turn = parallel.Begin()!;
            Check(parallel.Commit(turn, turn.Value * 3));
        }
        foreach (var sent in delivered)
        {
            var snap = parallel.Snapshot(sent.Ticket)!;
            Check(snap.State == HostTicketStateV1.Processed &&
                snap.HasResult && snap.Result == sent.Input * 3);
        }
        Check(parallel.CheckConservation());

        var doomed = new HostProxyMailboxTicketsV1(13, 14, 2);
        var fault = Admit(doomed, Route(201), -1);
        var faultTurn = doomed.Begin()!;
        var abandoned = Admit(doomed, Route(202), 20);
        Check(doomed.Panic(faultTurn));
        Check(doomed.Snapshot(fault)!.State == HostTicketStateV1.Failed);
        Check(doomed.Snapshot(abandoned)!.State == HostTicketStateV1.Abandoned);
        Check(!doomed.Commit(faultTurn, 999));
        Check(doomed.TrySendTicket(Route(203), 1).Status ==
            HostTicketAdmissionStateV1.Closed);
        Check(doomed.CheckConservation());

        var restart = new HostProxyMailboxTicketsV1(13, 15, 1);
        Check(restart.TrySendTicket(Route(204), 1).Status ==
            HostTicketAdmissionStateV1.Stale);
        Check(restart.Snapshot(fault) is null);
        var fresh = Admit(restart, Route(205, 15), 8);
        Check(restart.Commit(restart.Begin()!, 50));
        Check(restart.Snapshot(fresh)!.Result == 50 && restart.CheckConservation());

        var stopping = new HostProxyMailboxTicketsV1(13, 14, 1);
        var stopTicket = Admit(stopping, Route(300), 3);
        stopping.Close();
        Check(stopping.TrySendTicket(Route(301), 3).Status ==
            HostTicketAdmissionStateV1.Closed);
        Check(stopping.Commit(stopping.Begin()!, 4));
        Check(stopping.Snapshot(stopTicket)!.Result == 4 && stopping.CheckConservation());
        Console.WriteLine("ATTO_NET_HOST_PER_MAIL_TICKETS_128_CONCURRENT=PASS");
    }

    private static int Main()
    {
        var id = new ReceiptIdentityV1(1, 11, 12, 13, 14, 17, 15, 16, 1);
        var gate = new ProxyReceiptGateV1(id);
        Check(!gate.DestinationAdmitted);
        Check(!gate.ApplyVerified(id, ReceiptEventV1.Started));
        Check(!gate.ApplyVerified(id, ReceiptEventV1.Processed));
        Check(!gate.ApplyVerified(id, ReceiptEventV1.DurableInbox));
        Check(!gate.ApplyVerified(id, ReceiptEventV1.DurableResult));
        Check(!gate.ApplyVerified(id with {Tenant = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {CallerIncarnation = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {TargetIncarnation = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {AssociationGeneration = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {PlacementEpoch = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {Attempt = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {ProtocolMajor = 2}, ReceiptEventV1.Admitted));
        Check(gate.ApplyVerified(id, ReceiptEventV1.Admitted));
        Check(gate.DestinationAdmitted);
        Check(!gate.ApplyVerified(id, ReceiptEventV1.DurableResult));
        Check(gate.ApplyVerified(id, ReceiptEventV1.DurableInbox));
        Check(gate.DurableInbox && gate.Stage == ReceiptStageV1.Admitted);
        Check(gate.ApplyVerified(id, ReceiptEventV1.Started));
        Check(gate.ApplyVerified(id, ReceiptEventV1.Admitted));
        Check(gate.Stage == ReceiptStageV1.Started);
        Check(gate.ApplyVerified(id, ReceiptEventV1.Processed));
        Check(gate.ApplyVerified(id, ReceiptEventV1.DurableResult));
        Check(gate.DurableResult);
        Check(!gate.ApplyVerified(id, ReceiptEventV1.Failed));
        var missing = new ProxyReceiptGateV1(id);
        Check(missing.ApplyVerified(id, ReceiptEventV1.Indeterminate));
        Check(missing.Outcome == ReceiptOutcomeV1.Indeterminate);
        Check(!missing.ApplyVerified(id, ReceiptEventV1.Admitted));
        var rejected = new ProxyReceiptGateV1(id);
        Check(rejected.ApplyVerified(id, ReceiptEventV1.Rejected));
        Check(!rejected.ApplyVerified(id, ReceiptEventV1.Admitted));
        var failed = new ProxyReceiptGateV1(id);
        Check(failed.ApplyVerified(id, ReceiptEventV1.Admitted));
        Check(failed.ApplyVerified(id, ReceiptEventV1.Failed));
        Check(!failed.ApplyVerified(id, ReceiptEventV1.Processed));
        var invalid = new ProxyReceiptGateV1(id with {Tenant = 0});
        Check(!invalid.ApplyVerified(id with {Tenant = 0}, ReceiptEventV1.Admitted));
        HostMailboxTickets();
        Console.WriteLine("HOST_MODEL_ONLY proxy receipt v1 assertions passed");
        return 0;
    }
}
