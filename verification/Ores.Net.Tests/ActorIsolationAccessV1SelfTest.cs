using Ores.Net.Runtime;

internal static class ActorIsolationAccessV1SelfTest
{
    private static void Require(bool ok, string reason)
    {
        if (!ok) throw new Exception("actor isolation grant: " + reason);
    }

    public static void Run()
    {
        var a = new ActorIsolationAccessV1(1, 2, 3, 64);
        var grant = a.IssueAllocation(8, 16);
        Require(a.TryCheckedOffset(grant, 2, 16, out var at) && at == 8,
            "valid allocation access");
        Require(a.TryCheckedOffset(grant, 3, 8, out at) && at == 16,
            "valid tail access");
        foreach (var (word, size) in new (uint, ulong)[] {
            (0, 8), (1, 8), (3, 9), (4, 1), (2, 0), (2, ulong.MaxValue)
        })
            Require(!a.TryCheckedOffset(grant, word, size, out _),
                "invalid pointer/access denied");
        foreach (var foreign in new[] {
            new ActorIsolationAccessV1(1, 4, 3, 64),
            new ActorIsolationAccessV1(5, 2, 3, 64),
            new ActorIsolationAccessV1(1, 2, 4, 64)
        })
            Require(!foreign.TryCheckedOffset(grant, 2, 8, out _),
                "foreign actor/tenant/incarnation denied");
        bool overlap = false;
        try { a.IssueAllocation(16, 8); }
        catch (InvalidOperationException) { overlap = true; }
        Require(overlap, "overlapping live allocations denied");
        Require(a.Release(grant), "first release");
        Require(!a.Release(grant), "double release rejected");
        var fresh = a.IssueAllocation(8, 16);
        Require(!a.TryCheckedOffset(grant, 2, 8, out _) &&
                a.TryCheckedOffset(fresh, 2, 8, out at) && at == 8,
            "ABA use-after-free fenced");
        a.Retire();
        Require(!a.TryCheckedOffset(fresh, 2, 8, out _), "retirement fences access");
        var sibling = new ActorIsolationAccessV1(1, 99, 1, 64);
        var siblingGrant = sibling.IssueAllocation(0, 8);
        Require(sibling.TryCheckedOffset(siblingGrant, 1, 8, out at) && at == 0,
            "retiring a peer leaves sibling grant live");
        bool retired = false;
        try { a.IssueAllocation(0, 8); }
        catch (InvalidOperationException) { retired = true; }
        Require(retired, "retirement fences allocation");
        ulong span = ActorIsolationAccessV1.MaxCompressed32Span;
        var max = new ActorIsolationAccessV1(1, 1, 1, span);
        var last = max.IssueAllocation(span - 8, 8);
        Require(max.TryCheckedOffset(last, uint.MaxValue, 8, out at) &&
                at == span - 8, "last u32 word accepted without wrap");
        Require(!max.TryCheckedOffset(last, uint.MaxValue, 9, out _),
            "last u32 word oversized access denied");
        Console.WriteLine("PASS .NET actor allocation access grant bounds and lifetime fences");
    }
}
