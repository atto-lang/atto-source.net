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
        Console.WriteLine("Select continuation fence lifecycle checks passed");
    }
}
