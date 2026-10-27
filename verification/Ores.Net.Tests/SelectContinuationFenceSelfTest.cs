using Ores.Net.Runtime;

// No actor scheduler integration is assumed: this verifies the fence's
// bounded, generation-owned ticket admission and terminal shutdown contract.
internal static class SelectContinuationFenceSelfTest
{
    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception("select continuation fence: " + why);
    }

    private static void Reject<T>(Action action, string why) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("select continuation fence admitted " + why);
    }

    public static void Run()
    {
        Reject<ArgumentOutOfRangeException>(
            () => _ = new SelectContinuationFence(generation: 0),
            "zero generation");
        Reject<ArgumentOutOfRangeException>(
            () => _ = new SelectContinuationFence(maxTickets: 0),
            "zero ticket capacity");
        Reject<ArgumentOutOfRangeException>(
            () => _ = new SelectContinuationFence(maxTickets: 4097),
            "oversized ticket capacity");

        var fence = new SelectContinuationFence(generation: 3, maxTickets: 2);
        Require(fence.Generation == 3 && !fence.Closed && fence.PendingTickets == 0,
            "new fence identity and state");
        var first = fence.Register(3);
        var second = fence.Register(3);
        Require(first.Active && second.Active && fence.PendingTickets == 2,
            "ticket identity and bounded admission");
        Reject<InvalidOperationException>(() => _ = fence.Register(3),
            "over-capacity ticket");
        Reject<InvalidOperationException>(() => _ = fence.Register(2),
            "stale registration");
        Require(fence.PendingTickets == 2, "failed registrations did not mutate slots");

        Require(fence.TryConsume(first), "first consumption");
        Require(!fence.TryConsume(first) && !first.Active,
            "ticket may not be consumed twice");
        var reused = fence.Register(3);
        Require(!fence.TryConsume(first) && reused.Active,
            "reused slot cannot resurrect an old ticket");
        Require(second.Close() && !second.Active && !second.Close(),
            "close releases one ticket exactly once");
        Require(fence.PendingTickets == 1, "close releases only its own ticket");

        Require(!fence.Advance(2) && fence.Generation == 3,
            "stale generation cannot advance");
        Require(fence.Advance(3) && fence.Generation == 4,
            "generation advances exactly once");
        Require(!reused.Active && !fence.TryConsume(reused) &&
                fence.PendingTickets == 0,
            "generation advance invalidates outstanding tickets");
        Reject<InvalidOperationException>(() => _ = fence.Register(3),
            "stale generation after advance");

        var current = fence.Register(4);
        Require(current.Active && fence.PendingTickets == 1,
            "new generation admits new ticket");
        Require(fence.Close() && fence.Closed && fence.PendingTickets == 0,
            "terminal close retires pending tickets");
        Require(!fence.Close() && !current.Close() &&
                !fence.TryConsume(current) && !fence.Advance(4),
            "terminal close is idempotent, non-reopening");
        Reject<InvalidOperationException>(() => _ = fence.Register(4),
            "registration after terminal close");

        var terminal = new SelectContinuationFence(ulong.MaxValue, 1);
        Reject<OverflowException>(
            () => _ = terminal.Advance(ulong.MaxValue),
            "generation overflow");
        Require(!terminal.Closed && terminal.Generation == ulong.MaxValue,
            "generation overflow must not close or mutate generation");
        // C# nullability is part of the public admission contract:
        // a missing completion must never change a live ticket.
        var nullGuard = new SelectContinuationFence(maxTickets: 1);
        var nullGuardTicket = nullGuard.Register(1);
        Require(!nullGuard.TryConsume(null) && nullGuardTicket.Active &&
                nullGuard.PendingTickets == 1,
            "null completion cannot consume a real ticket");
        Require(nullGuardTicket.Close(), "null-guard cleanup");

        // Same-ticket consumption and cancellation have one linearization
        // point under the fence lock. Exactly one contender may win.
        var race = new SelectContinuationFence(generation: 8, maxTickets: 1);
        var racedTicket = race.Register(8);
        int consumed = 0;
        int cancelled = 0;
        Parallel.Invoke(
            () => { if (race.TryConsume(racedTicket))
                Interlocked.Increment(ref consumed); },
            () => { if (racedTicket.Close())
                Interlocked.Increment(ref cancelled); });
        Require(consumed + cancelled == 1 &&
                !racedTicket.Active && race.PendingTickets == 0,
            "consume/close race must have exactly one winner");

        // Concurrent duplicate delivery cannot replay a single ticket,
        // even when many producer threads observe the same receipt.
        var replayFence = new SelectContinuationFence(generation: 9, maxTickets: 1);
        var replay = replayFence.Register(9);
        int replayWinners = 0;
        Parallel.For(0, 128, _ =>
        {
            if (replayFence.TryConsume(replay))
                Interlocked.Increment(ref replayWinners);
        });
        Require(replayWinners == 1 && replayFence.PendingTickets == 0,
            "128 competing deliveries consumed one ticket exactly once");

        // Capacity checks are atomic even under concurrently registering
        // producers. Losing registrations must neither allocate extra slots
        // nor invalidate the eight winners.
        var parallelFence = new SelectContinuationFence(generation: 10, maxTickets: 8);
        var accepted = new System.Collections.Concurrent.ConcurrentBag<
            SelectContinuationFence.Ticket>();
        int rejected = 0;
        Parallel.For(0, 128, _ =>
        {
            try { accepted.Add(parallelFence.Register(10)); }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref rejected);
            }
        });
        Require(accepted.Count == 8 && rejected == 120 &&
                parallelFence.PendingTickets == 8 &&
                accepted.All(ticket => ticket.Active),
            "concurrent admission must preserve exact capacity");
        Parallel.ForEach(accepted, ticket =>
        {
            if (!ticket.Close())
                throw new Exception("concurrent ticket retirement failed");
        });
        Require(parallelFence.PendingTickets == 0,
            "concurrent cancellation retires every admitted ticket");

        // Terminal close and delivery may race: either a delivery consumes
        // before close or it loses after close, but nothing remains live.
        var closingFence = new SelectContinuationFence(generation: 11, maxTickets: 1);
        var closingTicket = closingFence.Register(11);
        int deliveredBeforeClose = 0;
        int terminalCloses = 0;
        Parallel.Invoke(
            () => { if (closingFence.TryConsume(closingTicket))
                Interlocked.Increment(ref deliveredBeforeClose); },
            () => { if (closingFence.Close())
                Interlocked.Increment(ref terminalCloses); });
        Require(terminalCloses == 1 && deliveredBeforeClose <= 1 &&
                closingFence.Closed && closingFence.PendingTickets == 0 &&
                !closingTicket.Active && !closingFence.TryConsume(closingTicket),
            "terminal close/delivery race leaves no reusable completion");
        Reject<InvalidOperationException>(() => _ = closingFence.Register(11),
            "terminal close reopened by late producer");

        Console.WriteLine("Select continuation fence lifecycle checks passed");
    }
}
