using Ores.Net.Runtime;

internal static class ActorNativeScopeSelfTest
{
    private sealed class ExpectedAbort : Exception { }

    private static void Check(bool result, string reason)
    {
        if (!result) throw new Exception("actor native scope: " + reason);
    }

    public static void Run()
    {
        var alice = new ActorMailbox(912).CurrentRef;
        var bob = new ActorMailbox(913).CurrentRef;
        var quota = new ActorNativeMemoryBudget(128);
        using var heap = new ActorNativeHeap(alice, 128, 4, quota);
        var persistent = heap.Allocate(alice, 8);
        Check(persistent.HasValue, "persistent state admission");
        Check(heap.Write(alice, persistent.Value, new byte[] { 7, 8, 9 }),
            "persistent actor state");
        for (int round = 0; round < 2_000; round++)
        {
            using (var scoped = heap.AllocateScoped(alice, 32, 16))
            {
                Check(scoped.IsValid, "native scratch slot should recycle without GC");
                Check(scoped.Write(new byte[] { 11, 12, 13, 14 }),
                    "write owned scoped bytes");
                Span<byte> copied = stackalloc byte[4];
                Check(scoped.Read(copied) &&
                      copied.SequenceEqual(new byte[] { 11, 12, 13, 14 }),
                      "read owned scoped bytes");
                Check(heap.LiveAllocations == 2, "persistent + temporary scopes");
            }
            Check(heap.LiveAllocations == 1,
                "compiler-style using scope deterministically dropped native temporary");
        }

        try
        {
            using var exceptionScope = heap.AllocateScoped(alice, 16);
            Check(exceptionScope.IsValid, "exception fixture acquired native bytes");
            throw new ExpectedAbort();
        }
        catch (ExpectedAbort) { }
        Check(heap.LiveAllocations == 1,
            "exception/finally must deterministically scrub and drop actor slot");

        var original = heap.AllocateScoped(alice, 8);
        var copiedScope = original;
        Check(original.IsValid && copiedScope.IsValid, "ref struct is copyable in host C#");
        original.Dispose();
        bool duplicateRejected = false;
        try { copiedScope.Dispose(); }
        catch (InvalidOperationException) { duplicateRejected = true; }
        Check(duplicateRejected,
            "copied local handle may not drop the next slot incarnation or double-free");
        Check(heap.LiveAllocations == 1, "duplicate scope did not free persistent state");

        var invalid = heap.AllocateScoped(alice, 0);
        Check(!invalid.IsValid, "rejected native admission returns empty stack-only scope");
        invalid.Dispose();
        bool absentRejected = false;
        try {
            Span<byte> result = stackalloc byte[1];
            invalid.Read(result);
        } catch (InvalidOperationException) { absentRejected = true; }
        Check(absentRejected, "invalid scope cannot access native memory");

        bool foreignRejected = false;
        try { var foreign = heap.AllocateScoped(bob, 8); foreign.Dispose(); }
        catch (InvalidOperationException) { foreignRejected = true; }
        Check(foreignRejected, "other actor cannot create a native ownership scope");

        Span<byte> resident = stackalloc byte[3];
        Check(heap.Read(alice, persistent.Value, resident) &&
              resident.SequenceEqual(new byte[] { 7, 8, 9 }),
              "persistent actor object never changed during scope churn");
        Check(heap.Remove(alice, persistent.Value) && heap.LiveAllocations == 0,
              "eventual persistent source field cleanup");
        heap.Dispose();
        Check(quota.ReservedBytes == 0,
              "all native actor budget refunded after final lifetime release");
        Console.WriteLine("PASS .NET scoped native drops: 2000 turns, exceptional unwind, stale copy");
    }
}
