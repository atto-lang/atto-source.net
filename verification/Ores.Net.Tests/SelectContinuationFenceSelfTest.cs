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

        // A receipt from another fence has the same initial slot/id and
        // generation, but may never consume this fence's live ticket.
        var ownerA = new SelectContinuationFence(generation: 19, maxTickets: 1);
        var ownerB = new SelectContinuationFence(generation: 19, maxTickets: 1);
        var ticketA = ownerA.Register(19);
        var ticketB = ownerB.Register(19);
        Require(!ownerA.TryConsume(ticketB) && !ownerB.TryConsume(ticketA) &&
                ticketA.Active && ticketB.Active &&
                ownerA.PendingTickets == 1 && ownerB.PendingTickets == 1,
            "foreign fence tickets cannot retire local slots");
        Require(ticketA.Close() && ticketB.Close(),
            "foreign-ticket isolation preserves independent cleanup");

        // A stale ticket must not close a newly admitted ticket reusing
        // its slot after generation advancement.
        var reuseFence = new SelectContinuationFence(generation: 20, maxTickets: 1);
        var staleTicket = reuseFence.Register(20);
        Require(reuseFence.Advance(20), "slot-reuse generation advances");
        var freshTicket = reuseFence.Register(21);
        Require(!staleTicket.Close() && !reuseFence.TryConsume(staleTicket) &&
                freshTicket.Active && reuseFence.PendingTickets == 1,
            "stale close cannot retire a successor generation ticket");
        Require(reuseFence.TryConsume(freshTicket) && reuseFence.PendingTickets == 0,
            "successor ticket survives stale receipt");

        // Exercise the four competing linearization points repeatedly.
        // Consumption and ticket close are mutually exclusive; advance may
        // win either side, but terminal close always leaves no live receipts.
        for (var round = 0; round < 256; round++)
        {
            var stressedFence = new SelectContinuationFence(
                generation: 22, maxTickets: 1);
            var stressedTicket = stressedFence.Register(22);
            int ticketWinners = 0;
            int fenceClosers = 0;
            Parallel.Invoke(
                () => { if (stressedFence.TryConsume(stressedTicket))
                    Interlocked.Increment(ref ticketWinners); },
                () => { if (stressedTicket.Close())
                    Interlocked.Increment(ref ticketWinners); },
                () => { _ = stressedFence.Advance(22); },
                () => { if (stressedFence.Close())
                    Interlocked.Increment(ref fenceClosers); });
            Require(ticketWinners <= 1 && fenceClosers == 1 &&
                    stressedFence.Closed && stressedFence.PendingTickets == 0 &&
                    !stressedTicket.Active &&
                    !stressedFence.TryConsume(stressedTicket) &&
                    !stressedFence.Advance(stressedFence.Generation),
                "256-round terminal lifecycle race preserves ticket isolation");
        }

        // A logical claim is not physical retirement. Stop and rollover
        // must respect the live execution lease, even after the slot clears.
        var leased = new SelectContinuationFence(maxTickets: 2);
        var claimedTicket = leased.Register(1);
        var pendingTicket = leased.Register(1);
        var execution = leased.TryClaim(claimedTicket)
            ?? throw new Exception("execution claim unexpectedly failed");
        Require(leased.Executing == 1 && leased.PendingTickets == 1,
            "claim preserves physical credit");
        Require(!leased.Advance(1) && leased.TryClaim(claimedTicket) is null,
            "cannot advance or replay while executing");
        Reject<InvalidOperationException>(() => _ = leased.Register(1),
            "physical capacity retained during execution");
        Require(leased.Close() && !pendingTicket.Active && !leased.RetirementReady,
            "terminal close revokes pending but waits for physical execution");
        int releases = 0;
        Parallel.For(0, 128, _ =>
        {
            if (execution.Close()) Interlocked.Increment(ref releases);
        });
        Require(releases == 1 && leased.Executing == 0 && leased.RetirementReady,
            "lease retires exactly once under concurrent close");
        var reusable = new SelectContinuationFence(maxTickets: 1);
        var previous = reusable.Register(1);
        using (var lease = reusable.TryClaim(previous)
            ?? throw new Exception("claim failed"))
        {
            Require(!reusable.Advance(1), "advance fenced by live lease");
        }
        Require(reusable.Advance(1), "advance succeeds after disposal");
        var successor = reusable.Register(2);
        Require(reusable.TryClaim(previous) is null && successor.Active,
            "old ticket cannot claim successor generation");
        Require(successor.Close(), "successor cleanup");

        // Same bounded mixed-lease churn as the JavaScript profile.
        var mixed = new SelectContinuationFence(maxTickets: 64);
        var prior = Array.Empty<SelectContinuationFence.Ticket>();
        for (var round = 0; round < 256; round++)
        {
            var tickets = Enumerable.Range(0, 64)
                .Select(_ => mixed.Register(mixed.Generation)).ToArray();
            var leases = tickets.Take(32)
                .Select(t => mixed.TryClaim(t) ?? throw new Exception("claim failed")).ToArray();
            Require(mixed.Executing == 32 && mixed.PendingTickets == 32,
                "mixed physical and pending credit");
            Reject<InvalidOperationException>(() => _ = mixed.Register(mixed.Generation),
                "mixed over-capacity");
            Require(!mixed.Advance(mixed.Generation), "live execution fences rollover");
            foreach (var stale in prior)
                Require(mixed.TryClaim(stale) is null && !stale.Close(), "stale ticket cannot retire new slot");
            foreach (var pending in tickets.Skip(32))
                Require(pending.Close(), "pending slot retirement");
            var fresh = Enumerable.Range(0, 32)
                .Select(_ => mixed.Register(mixed.Generation)).ToArray();
            Parallel.ForEach(leases, lease =>
            {
                Require(lease.Close() && !lease.Close(), "exactly-once lease release");
            });
            Require(mixed.Executing == 0 && mixed.PendingTickets == 32,
                "mixed credit after physical release");
            Require(mixed.Advance(mixed.Generation) && mixed.PendingTickets == 0,
                "rollover clears only pending slots");
            prior = tickets.Concat(fresh).ToArray();
        }
        Require(mixed.Close() && mixed.RetirementReady, "final mixed retirement");

        Console.WriteLine("Select continuation fence lifecycle checks passed");
    }
}
