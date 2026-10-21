using Ores.Net.Runtime;

internal static class ActorNativeBudgetSelfTest
{
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new Exception("native actor budget: " + message);
    }

    private static void Deny(Action action, string reason)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("native actor budget failed to reject: " + reason);
    }

    public static void Run()
    {
        var alice = new ActorMailbox(71).CurrentRef;
        var bob = new ActorMailbox(72).CurrentRef;
        var quota = new ActorNativeMemoryBudget(64);
        Require(quota.LimitBytes == 64 && quota.ReservedBytes == 0, "fresh quota");
        using var a = new ActorNativeHeap(alice, 40, 4, quota);
        using var b = new ActorNativeHeap(bob, 24, 4, quota);
        Require(quota.ReservedBytes == 64, "two separate actors reserve one aggregate limit");
        Deny(() => _ = new ActorNativeHeap(alice, 1, 1, quota), "overcommitted third actor");
        Require(quota.ReservedBytes == 64, "denial cannot mutate aggregate accounting");

        var lifetime = a.AcquireLifetimeLease(alice);
        Require(lifetime is not null, "borrowed actor lifetime");
        Require(!a.Retire(alice), "cannot physically reclaim while borrowed");
        Require(quota.ReservedBytes == 64,
            "retirement request cannot refund physically pinned memory");
        lifetime!.Dispose();
        lifetime.Dispose();
        Require(a.Retired && quota.ReservedBytes == 24,
            "last lease physically frees and refunds first actor exactly once");

        using var c = new ActorNativeHeap(alice, 40, 4, quota);
        Require(quota.ReservedBytes == 64, "refunded bytes admit another actor");
        c.Dispose();
        c.Dispose();
        Require(quota.ReservedBytes == 24, "duplicate close cannot double refund");
        b.Dispose();
        Require(quota.ReservedBytes == 0, "all actor regions physically reclaimed");

        var contended = new ActorNativeMemoryBudget(128 * 8);
        Parallel.For(0, 256, i =>
        {
            var cap = new ActorMailbox((ulong)(1_000 + i)).CurrentRef;
            using var heap = new ActorNativeHeap(cap, 128, 1, contended);
            Require(contended.ReservedBytes <= contended.LimitBytes,
                "parallel admissions must not exceed aggregate limit");
        });
        Require(contended.ReservedBytes == 0, "parallel scopes refunded all native bytes");
        Console.WriteLine("PASS .NET aggregate actor native budget, delayed refunds and concurrent cap");
    }
}
