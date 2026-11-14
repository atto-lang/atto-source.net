using Ores.Net.Runtime;
using System.Threading;

internal static class BoundedDemandI64PortSelfTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Demand port: " + message);
    }

    internal static void Run()
    {
        var port = new BoundedDemandI64Port(2, 1, 24, 4);
        Check(port.TryOffer(1) == BoundedDemandI64Port.OfferStatus.NoSubscriber, "no subscriber");
        Check(port.Subscribe() && !port.Subscribe(), "exclusive reader");
        Check(port.TryOffer(1) == BoundedDemandI64Port.OfferStatus.NoDemand, "demand required");
        port.Request(4);
        Check(port.TryOffer(11) == BoundedDemandI64Port.OfferStatus.Accepted, "first");
        Check(port.TryOffer(22) == BoundedDemandI64Port.OfferStatus.Accepted, "second");
        Check(port.TryOffer(33) == BoundedDemandI64Port.OfferStatus.Full, "bounded FIFO");
        var one = port.TryNext();
        Check(one.Status == BoundedDemandI64Port.PollStatus.Item && one.Value == 11, "FIFO first");
        Check(port.TryNext().Status == BoundedDemandI64Port.PollStatus.NotReady, "loan cap");
        Check(port.TryOffer(33) == BoundedDemandI64Port.OfferStatus.Accepted, "available slot");
        Check(port.RetainedBytes == 24, "checked out loan retains budget");
        Check(port.Ack(one.Ticket) && !port.Ack(one.Ticket), "exact once ACK");
        var two = port.TryNext();
        Check(two.Value == 22 && port.Ack(two.Ticket), "second");
        var three = port.TryNext();
        Check(three.Value == 33 && port.Ack(three.Ticket), "third");
        Check(port.RetainedBytes == 0, "releases every byte");

        var closed = new BoundedDemandI64Port(2, 2, 16, 2);
        Check(closed.Subscribe(), "subscription");
        closed.Request(2);
        Check(closed.TryOffer(7) == BoundedDemandI64Port.OfferStatus.Accepted, "first terminal");
        Check(closed.TryOffer(8) == BoundedDemandI64Port.OfferStatus.Accepted, "second terminal");
        Check(closed.Complete() && !closed.Cancel(), "terminal never rewritten");
        var a = closed.TryNext(); var b = closed.TryNext();
        Check(a.Value == 7 && b.Value == 8, "complete preserves FIFO");
        Check(closed.Ack(a.Ticket) && closed.Ack(b.Ticket), "terminal physical loans");
        Check(closed.TryNext().Status == BoundedDemandI64Port.PollStatus.Complete, "EOF once");
        Check(closed.RetainedBytes == 0, "no outstanding retained bytes");

        var cancelled = new BoundedDemandI64Port(2, 1, 16, 2);
        Check(cancelled.Subscribe(), "cancel subscription");
        cancelled.Request(2);
        cancelled.TryOffer(7); cancelled.TryOffer(8);
        var loan = cancelled.TryNext();
        Check(cancelled.Cancel() && !cancelled.Complete(), "cancel irrevocable");
        Check(cancelled.Queued == 0 && cancelled.RetainedBytes == 8, "cancel retains physical loan only");
        Check(cancelled.Ack(loan.Ticket) && cancelled.RetainedBytes == 0, "retired loan");
        Check(cancelled.TryNext().Status == BoundedDemandI64Port.PollStatus.Cancelled, "cancelled terminal");

        for (int run = 0; run < 48; run++)
        {
            var racing = new BoundedDemandI64Port(1, 1, 8, 1);
            racing.Subscribe();
            racing.Request(1);
            racing.TryOffer(9);
            using var gate = new ManualResetEventSlim(false);
            bool complete = false, cancel = false;
            var first = new Thread(() => { gate.Wait(); complete = racing.Complete(); });
            var second = new Thread(() => { gate.Wait(); cancel = racing.Cancel(); });
            first.Start(); second.Start(); gate.Set();
            first.Join(); second.Join();
            Check(complete != cancel, "exactly one terminal");
            if (complete)
            {
                var item = racing.TryNext();
                Check(item.Status == BoundedDemandI64Port.PollStatus.Item && item.Value == 9, "committed item");
                Check(racing.Ack(item.Ticket), "ACK");
                Check(racing.TryNext().Status == BoundedDemandI64Port.PollStatus.Complete, "complete terminal");
            }
            else Check(racing.TryNext().Status == BoundedDemandI64Port.PollStatus.Cancelled, "cancel terminal");
            Check(racing.RetainedBytes == 0, "race cleanup");
        }
    }
}
