using Ores.Net.Runtime;

internal static class SelectContinuationFenceSelfTest
{
    private static void Require(bool value, string reason)
    {
        if (!value) throw new Exception("Continuation fence regression: " + reason);
    }

    private static void TicketConsumesOnce()
    {
        var fence = new SelectContinuationFence();
        var ticket = fence.Register(1);
        Require(ticket.Generation == 1, "ticket generation");
        Require(fence.TryConsume(ticket), "first consume");
        Require(!fence.TryConsume(ticket), "second consume rejected");
        Require(fence.PendingTickets == 0, "consumed ticket removed");
    }

    private static void AdvanceInvalidatesQueuedOldTicket()
    {
        var fence = new SelectContinuationFence();
        var queued = fence.Register(1);
        Require(fence.Advance(1), "advance current generation");
        Require(fence.Generation == 2, "generation incremented");
        Require(!fence.TryConsume(queued), "old queued ticket rejected");
    }

    private static void SlotReuseKeepsIdentityDistinct()
    {
        var fence = new SelectContinuationFence(maxTickets: 1);
        var old = fence.Register(1);
        Require(fence.Advance(1), "advance before slot reuse");
        var current = fence.Register(2);
        Require(!old.Active && current.Active, "only new ticket active");
        Require(!fence.TryConsume(old), "old ticket cannot consume new slot");
        Require(fence.TryConsume(current), "new ticket consumes");
    }

    private static void ClosedTicketCannotDeliver()
    {
        var fence = new SelectContinuationFence();
        var ticket = fence.Register(1);
        Require(ticket.Close(), "close ticket");
        Require(!ticket.Close(), "close is one-shot");
        Require(!fence.TryConsume(ticket), "closed ticket rejected");
    }

    private static void StaleAdvanceLeavesCurrentTicketIntact()
    {
        var fence = new SelectContinuationFence(generation: 7);
        var ticket = fence.Register(7);
        Require(!fence.Advance(6), "stale advance rejected");
        Require(fence.Generation == 7, "generation unchanged");
        Require(fence.TryConsume(ticket), "current ticket remains valid");
    }

    private static void StaleRegistrationFailsClosed()
    {
        var fence = new SelectContinuationFence(generation: 3);
        var observed = false;
        try
        {
            _ = fence.Register(2);
        }
        catch (InvalidOperationException)
        {
            observed = true;
        }
        Require(observed, "stale registration rejected");
        Require(fence.PendingTickets == 0, "no ticket leaked");
    }

    private static void ForeignTicketCannotConsume()
    {
        var first = new SelectContinuationFence();
        var second = new SelectContinuationFence();
        var ticket = first.Register(1);
        Require(!second.TryConsume(ticket), "foreign ticket rejected");
        Require(ticket.Close(), "owner can still close ticket");
    }

    private static void DeliveryRechecksGenerationAfterQueueing()
    {
        var fence = new SelectContinuationFence();
        var queued = fence.Register(1);
        Require(fence.Advance(1), "restart advances generation");
        var delivered = 0;
        if (fence.TryConsume(queued)) delivered++;
        Require(delivered == 0, "old queued continuation not delivered");

        var current = fence.Register(2);
        if (fence.TryConsume(current)) delivered++;
        Require(delivered == 1, "current continuation delivered once");
    }


    private static void CapacityFailurePreservesReservations()
    {
        var fence = new SelectContinuationFence(maxTickets: 2);
        var first = fence.Register(1);
        var second = fence.Register(1);
        var rejected = false;
        try { _ = fence.Register(1); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && fence.PendingTickets == 2, "capacity failure is atomic");
        Require(first.Active && second.Active, "both reservations survive");
        Require(first.Close(), "close releases one slot");
        var replacement = fence.Register(1);
        Require(!first.Close(), "old close cannot retire replacement");
        Require(fence.PendingTickets == 2, "replacement remains reserved");
        Require(fence.TryConsume(second), "second ticket survives");
        Require(fence.TryConsume(replacement), "replacement consumes");
        Require(fence.PendingTickets == 0, "no reservation leaked");
    }

    private static void AdvanceRestoresFullCapacity()
    {
        var fence = new SelectContinuationFence(maxTickets: 2);
        var first = fence.Register(1);
        var second = fence.Register(1);
        Require(fence.Advance(1), "advance full fence");
        Require(fence.PendingTickets == 0, "all old slots cleared");
        var nextFirst = fence.Register(2);
        var nextSecond = fence.Register(2);
        Require(!first.Active && !second.Active, "all old tickets inactive");
        Require(!first.Close() && !second.Close(), "old close cannot affect new slots");
        Require(!fence.TryConsume(first) && !fence.TryConsume(second), "old delivery rejected");
        Require(fence.PendingTickets == 2, "both new slots preserved");
        Require(fence.TryConsume(nextFirst) && fence.TryConsume(nextSecond), "new delivery");
    }

    private static void GenerationOverflowPreservesReservations()
    {
        var fence = new SelectContinuationFence(generation: ulong.MaxValue, maxTickets: 1);
        var ticket = fence.Register(ulong.MaxValue);
        Require(!fence.Advance(ulong.MaxValue - 1), "stale advance at limit rejected");
        var rejected = false;
        try { _ = fence.Advance(ulong.MaxValue); }
        catch (OverflowException) { rejected = true; }
        Require(rejected, "generation cannot wrap");
        Require(fence.Generation == ulong.MaxValue, "overflow preserves generation");
        Require(fence.PendingTickets == 1 && ticket.Active, "overflow preserves reservation");
        Require(fence.TryConsume(ticket), "current ticket remains deliverable");
    }

    private static void InvalidConfigurationRejected()
    {
        foreach (var capacity in new[] { -1, 0, 4097 })
        {
            var rejected = false;
            try { _ = new SelectContinuationFence(maxTickets: capacity); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "invalid capacity rejected");
        }
        var generationRejected = false;
        try { _ = new SelectContinuationFence(generation: 0); }
        catch (ArgumentOutOfRangeException) { generationRejected = true; }
        Require(generationRejected, "zero generation rejected");
        Require(new SelectContinuationFence(maxTickets: 4096).PendingTickets == 0,
            "maximum valid capacity accepted");
    }

    private static void NullTicketDoesNotConsumeReservation()
    {
        var fence = new SelectContinuationFence(maxTickets: 1);
        var ticket = fence.Register(1);
        Require(!fence.TryConsume(null!), "null ticket rejected");
        Require(fence.PendingTickets == 1, "null rejection preserves live ticket");
        Require(fence.TryConsume(ticket), "live ticket still consumes");
    }


    private static void TerminalCloseCannotReopen()
    {
        var fence = new SelectContinuationFence(maxTickets: 2);
        var first = fence.Register(1);
        var second = fence.Register(1);
        Require(!fence.Closed && fence.Close(), "first terminal close");
        Require(fence.Closed && !fence.Close(), "close idempotent");
        Require(fence.PendingTickets == 0 && fence.Generation == 1, "closed without generation reuse");
        Require(!fence.Advance(1), "closed fence cannot advance");
        Require(!first.Active && !second.Active, "all pending tickets invalidated");
        Require(!first.Close() && !second.Close(), "closed ticket close rejected");
        Require(!fence.TryConsume(first), "delivery after close rejected");
        var rejected = false;
        try { _ = fence.Register(1); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "closed fence rejects registration");
        var sibling = new SelectContinuationFence();
        Require(sibling.TryConsume(sibling.Register(1)), "sibling unaffected");
    }

    private static void CloseAtGenerationLimit()
    {
        var fence = new SelectContinuationFence(generation: ulong.MaxValue);
        var ticket = fence.Register(ulong.MaxValue);
        Require(fence.Close(), "terminal close does not need another generation");
        Require(!fence.Advance(ulong.MaxValue), "closed limit cannot advance");
        Require(!fence.TryConsume(ticket), "limit ticket invalidated");
    }

    private static void ConcurrentCancellationAndDelivery()
    {
        for (var round = 0; round < 256; round++)
        {
            var fence = new SelectContinuationFence(maxTickets: 1);
            var ticket = fence.Register(1);
            var consumed = false;
            var cancelled = false;
            System.Threading.Tasks.Parallel.Invoke(
                () => consumed = fence.TryConsume(ticket),
                () => cancelled = ticket.Close());
            Require(consumed != cancelled, "exactly one cancel/delivery winner");
            Require(fence.PendingTickets == 0, "concurrent winner releases slot");
        }
    }

    private static void RegistrationRacingTerminalClose()
    {
        for (var round = 0; round < 256; round++)
        {
            var fence = new SelectContinuationFence(maxTickets: 1);
            SelectContinuationFence.Ticket ticket = null!;
            var rejected = false;
            System.Threading.Tasks.Parallel.Invoke(
                () => {
                    try { ticket = fence.Register(1); }
                    catch (InvalidOperationException) { rejected = true; }
                },
                () => Require(fence.Close(), "shutdown wins once"));
            Require(rejected || ticket is not null, "registration outcome recorded");
            Require(fence.Closed && fence.PendingTickets == 0, "no reservation survives shutdown");
            Require(ticket is null || !ticket.Active, "registered ticket is invalidated");
            Require(!fence.TryConsume(ticket!), "no post-shutdown delivery");
        }
    }

    public static void Run()
    {
        TerminalCloseCannotReopen();
        CloseAtGenerationLimit();
        ConcurrentCancellationAndDelivery();
        RegistrationRacingTerminalClose();
        CapacityFailurePreservesReservations();
        AdvanceRestoresFullCapacity();
        GenerationOverflowPreservesReservations();
        InvalidConfigurationRejected();
        NullTicketDoesNotConsumeReservation();
        TicketConsumesOnce();
        AdvanceInvalidatesQueuedOldTicket();
        SlotReuseKeepsIdentityDistinct();
        ClosedTicketCannotDeliver();
        StaleAdvanceLeavesCurrentTicketIntact();
        StaleRegistrationFailsClosed();
        ForeignTicketCannotConsume();
        DeliveryRechecksGenerationAfterQueueing();
    }
}
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

        Console.WriteLine("Select continuation fence lifecycle checks passed");
    }
}
