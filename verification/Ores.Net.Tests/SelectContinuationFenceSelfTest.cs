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

    public static void Run()
    {
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
