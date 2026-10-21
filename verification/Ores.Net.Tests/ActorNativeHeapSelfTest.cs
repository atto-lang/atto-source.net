using System.Text.Json;
using Ores.Net.Runtime;

internal static class ActorNativeHeapSelfTest
{
    public static void Run()
    {
        static void Require(bool good, string reason)
        {
            if (!good) throw new Exception("actor native heap: " + reason);
        }

        static void Reject(Action operation, string reason)
        {
            try { operation(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("actor native heap failed to reject " + reason);
        }

        static void RejectWire(Action operation)
        {
            try { operation(); }
            catch (NotSupportedException) { return; }
            throw new Exception("actor heap handle escaped to JSON");
        }

        using var mailboxA = new ActorNativeTestMailbox(new ActorMailbox(42));
        using var mailboxB = new ActorNativeTestMailbox(new ActorMailbox(42));
        var alice = mailboxA.Mailbox.CurrentRef;
        var bob = mailboxB.Mailbox.CurrentRef;
        using var heapA = new ActorNativeHeap(alice, 256, 4);
        using var heapB = new ActorNativeHeap(bob, 256, 4);

        var root = heapA.Allocate(alice, 8);
        Require(root.HasValue, "initial root");
        Require(heapA.Write(alice, root.Value, new byte[] { 1, 2, 3, 4 }),
            "write to actor-owned native memory");
        Span<byte> buffer = stackalloc byte[4];
        Require(heapA.Read(alice, root.Value, buffer) && buffer.SequenceEqual(new byte[] {1, 2, 3, 4}),
            "round-trip native storage");
        Reject(() => heapA.Allocate(bob, 8), "foreign actor allocation");
        Reject(() => heapA.Remove(bob, root.Value), "foreign actor removal");
        Reject(() => heapA.AcquireLifetimeLease(bob), "foreign actor async loan");
        Require(!heapB.Read(bob, root.Value, buffer), "foreign heap must reject handle");
        Require(!heapB.Remove(bob, root.Value), "foreign heap must reject removal");
        Require(!heapA.Read(alice, root.Value, buffer, -1), "negative offset");
        Require(!heapA.Write(alice, root.Value, new byte[9]), "out-of-bounds write");
        Require(!heapA.Allocate(alice, 1, 3).HasValue, "invalid alignment rejected");
        Require(!heapA.Allocate(alice, 257).HasValue, "byte quota enforced");

        // Reuse freed regions without moving a permanently live root.
        var former = heapA.Allocate(alice, 64);
        Require(former.HasValue, "initial transient");
        Require(heapA.Remove(alice, former.Value), "free transient");
        for (int i = 0; i < 2000; ++i)
        {
            var next = heapA.Allocate(alice, 64);
            Require(next.HasValue, "partial-free long-lived actor allocation");
            Require(!heapA.Read(alice, former.Value, buffer), "stale handle after reuse");
            Require(heapA.Read(alice, next.Value, buffer)
                    && buffer.SequenceEqual(new byte[4]), "zeroed reusable native bytes");
            Require(heapA.Write(alice, next.Value, new byte[] { 90, 91, 92, 93 }),
                "native transient write");
            Require(heapA.Remove(alice, next.Value), "deterministic per-message cleanup");
        }
        Require(heapA.Read(alice, root.Value, buffer)
                && buffer.SequenceEqual(new byte[] {1, 2, 3, 4}),
            "persistent state survived transient free/reuse");
        Require(heapA.LiveAllocations == 1, "per-actor allocation accounting");

        // Exercise a genuinely interior hole while a later allocation
        // remains live. First-fit must split the hole for aligned reuse,
        // then coalesce both fragments when the new allocation is dropped.
        using (var fragmented = new ActorNativeHeap(alice, 40, 4))
        {
            var head = fragmented.Allocate(alice, 1, 1);
            var center = fragmented.Allocate(alice, 32, 1);
            var tail = fragmented.Allocate(alice, 1, 1);
            Require(head.HasValue && center.HasValue && tail.HasValue,
                "fragmentation fixture admission");
            Require(fragmented.Write(alice, tail.Value, new byte[] { 23 }),
                "write stable trailing allocation");
            Require(fragmented.Remove(alice, center.Value), "create interior hole");
            var aligned = fragmented.Allocate(alice, 8, 8);
            Require(aligned.HasValue, "reuse interior hole with alignment padding");
            Require(fragmented.Remove(alice, aligned.Value), "return split hole");
            var whole = fragmented.Allocate(alice, 32, 1);
            Require(whole.HasValue, "coalesced interior hole must fit full 32-byte region");
            Span<byte> tailValue = stackalloc byte[1];
            Require(fragmented.Read(alice, tail.Value, tailValue) && tailValue[0] == 23,
                "live trailing allocation cannot be compacted or overwritten");
            Require(fragmented.Remove(alice, whole.Value) &&
                    fragmented.Remove(alice, tail.Value) &&
                    fragmented.Remove(alice, head.Value),
                "all persistent and transient native allocations released");
        }

        RejectWire(() => JsonSerializer.Serialize(root.Value));
        RejectWire(() => JsonSerializer.Deserialize<ActorHeapHandle>("{}"));
        var pending = heapA.AcquireLifetimeLease(alice);
        Require(pending is not null, "actor async lifetime admitted");
        Require(!heapA.Retire(alice), "pending async reference must defer native free");
        Require(!heapA.Retired, "quarantined actor memory remains allocated");
        Require(!heapA.Allocate(alice, 1).HasValue, "retiring actor rejects new allocations");
        Require(!heapA.Write(alice, root.Value, new byte[] {1}), "retiring actor rejects writes");
        Require(heapA.AcquireLifetimeLease(alice) is null, "retiring actor rejects leases");
        pending!.Dispose();
        pending!.Dispose();
        Require(heapA.Retired && heapA.LiveAllocations == 0, "final lease frees native heap exactly once");
        Require(heapA.Retire(alice), "idempotent retirement");

        var solo = new ActorNativeHeap(bob, 32, 1);
        using (var lease = solo.AcquireLifetimeLease(bob))
        {
            Require(lease is not null, "second async lease");
            solo.Dispose();
            Require(!solo.Retired, "Dispose cannot free borrowed heap");
        }
        Require(solo.Retired, "Dispose deferred native reclamation until lease exit");

        using var bounded = new ActorNativeHeap(bob, 32, 1);
        var only = bounded.Allocate(bob, 8);
        Require(only.HasValue && !bounded.Allocate(bob, 8).HasValue, "slot quota");
        Require(bounded.Remove(bob, only.Value), "remove sole slot");
        Require(bounded.Allocate(bob, 8).HasValue
                && !bounded.Remove(bob, only.Value), "generation fenced slot reuse");

        Console.WriteLine("PASS .NET per-actor unmanaged heaps, bounds, loans, ABA, recycle, retirement");
    }

    // ActorMailbox itself is not IDisposable. This holder merely makes the
    // disposable heap fixtures' owner relationship explicit in the test.
    private sealed class ActorNativeTestMailbox : IDisposable
    {
        internal ActorMailbox Mailbox { get; }
        internal ActorNativeTestMailbox(ActorMailbox mailbox) => Mailbox = mailbox;
        public void Dispose() => Mailbox.Stop();
    }
}
