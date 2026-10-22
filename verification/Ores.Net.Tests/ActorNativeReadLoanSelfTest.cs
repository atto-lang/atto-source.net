using Ores.Net.Runtime;

internal static class ActorNativeReadLoanSelfTest
{
    private static void Check(bool yes, string why)
    {
        if (!yes) throw new Exception("actor native read loan: " + why);
    }

    private static void Reject(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("read loan did not reject " + why);
    }

    public static void Run()
    {
        var owner = new ActorMailbox(801).CurrentRef;
        var foreign = new ActorMailbox(802).CurrentRef;
        var quota = new ActorNativeMemoryBudget(256);
        using var heap = new ActorNativeHeap(owner, 128, 3, quota);

        var root = heap.Allocate(owner, 32);
        var other = heap.Allocate(owner, 32);
        Check(root.HasValue && other.HasValue, "two native object slots");
        Check(heap.Write(owner, root.Value, new byte[] { 11, 12, 13 }),
            "initial root bytes");
        Check(heap.Write(owner, other.Value, new byte[] { 77 }), "other native object");

        var first = heap.AcquireReadOnlyLoan(owner, root.Value);
        var second = heap.AcquireReadOnlyLoan(owner, root.Value);
        Check(first != null && second != null, "two read-only loans");
        Reject(() => heap.AcquireReadOnlyLoan(foreign, root.Value),
            "foreign actor read admission");
        Check(!heap.Write(owner, root.Value, new byte[] { 44 }),
            "shared borrow must block in-place mutation");
        Check(!heap.Remove(owner, root.Value), "shared borrow must block object free");
        Check(heap.Write(owner, other.Value, new byte[] { 99 }),
            "unrelated object remains independently mutable");

        Span<byte> borrowed = stackalloc byte[3];
        Check(first!.Read(owner, borrowed) && borrowed.SequenceEqual(new byte[] {11, 12, 13}),
            "loaned copy preserves immutable payload");
        Check(!first.Read(owner, borrowed, -1), "borrowed bounds reject negative offsets");
        Check(!first.Read(owner, borrowed, 31), "borrowed bounds reject overflow");
        Reject(() => {
            Span<byte> bytes = stackalloc byte[1];
            first.Read(foreign, bytes);
        }, "foreign actor borrowed reads");
        first.Dispose();
        first.Dispose();
        Check(!heap.Write(owner, root.Value, new byte[] { 55 }),
            "second outstanding loan keeps data frozen");
        second!.Dispose();
        Check(heap.Write(owner, root.Value, new byte[] { 55 }),
            "last immutable reader releases mutation fence");
        Check(heap.Remove(owner, root.Value), "last loan allows explicit native drop");
        Check(heap.AcquireReadOnlyLoan(owner, root.Value) == null,
            "stale generational handle cannot acquire a new loan");

        var replacement = heap.Allocate(owner, 16);
        Check(replacement.HasValue, "reuse native slot after loan release");
        Check(heap.AcquireReadOnlyLoan(owner, root.Value) == null,
            "recycled slot never resurrects stale loan handle");

        var pending = heap.AcquireReadOnlyLoan(owner, replacement.Value);
        Check(pending is not null, "read lease survives actor stop request");
        Check(!heap.Retire(owner) && !heap.Retired && quota.ReservedBytes == 128,
            "retirement cannot release native bytes or quota while pinned");
        Check(heap.AcquireReadOnlyLoan(owner, other.Value) is null,
            "retirement must seal new read admissions");
        Span<byte> value = stackalloc byte[1];
        Check(pending!.Read(owner, value), "existing admitted loan drains during retirement");
        Check(!heap.Write(owner, other.Value, new byte[] { 1 }),
            "ordinary actor writes revoked during retirement");
        pending.Dispose();
        Check(heap.Retired && quota.ReservedBytes == 0,
            "last outstanding read loan frees and refunds native arena");
        Check(!heap.Read(owner, other.Value, value), "old handles fail after retirement");
        Check(heap.Retire(owner), "repeated retirement cannot refund twice");
        Console.WriteLine("PASS .NET native read loans: immutable slot, owner, bounds, recycle, retirement");
    }
}
