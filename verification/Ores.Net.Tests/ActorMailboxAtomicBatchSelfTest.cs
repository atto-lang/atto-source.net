using Ores.Net.Runtime;

// B: host-only transactional single-mailbox admission. This is deliberately
// not actor guest CIL/NativeAOT source execution and not DAG-wide fan-out.
internal static class ActorMailboxAtomicBatchSelfTest
{
    public static void Run()
    {
        static IReadOnlyList<ReadOnlyMemory<byte>> Frames(params byte[][] values) =>
            values.Select(value => (ReadOnlyMemory<byte>)value).ToArray();

        var box = new ActorMailbox(96001, maxMessages: 3, maxQueuedBytes: 6);
        var authority = box.CurrentRef;
        byte[] mutable = { 7, 8 };
        box.SendBatchAtomic(authority, Frames(mutable, new byte[] { 9 }, new byte[] { 10 }));
        mutable[0] = 99;
        if (box.QueuedMessages != 3 ||
            !box.TryReceive(out var first) || first is null || first[0] != 7 ||
            !box.TryReceive(out var second) || second is null || second[0] != 9 ||
            !box.TryReceive(out var third) || third is null || third[0] != 10)
            throw new Exception("atomic batch lost FIFO or leaked borrowed sender alias");

        box.Send(authority, new byte[] { 1, 2 });
        var fullRejected = false;
        try { box.SendBatchAtomic(authority, Frames(new byte[] { 3, 4 }, new byte[] { 5, 6, 7 })); }
        catch (InvalidOperationException) { fullRejected = true; }
        if (!fullRejected || box.QueuedMessages != 1 ||
            !box.TryReceive(out var stillFirst) || stillFirst is null ||
            !stillFirst.SequenceEqual(new byte[] { 1, 2 }))
            throw new Exception("byte-overflow batch partially published DATA");

        box.SendBatchAtomic(authority, Frames(new byte[] { 3, 4 }, new byte[] { 5, 6 }));
        if (box.QueuedMessages != 2)
            throw new Exception("failed admission leaked byte quota");
        var countRejected = false;
        try { box.SendBatchAtomic(authority, Frames(new byte[] { 7 }, new byte[] { 8 })); }
        catch (InvalidOperationException) { countRejected = true; }
        if (!countRejected || box.QueuedMessages != 2)
            throw new Exception("message-count overrun partially published DATA");

        box.Stop();
        var stoppedRejected = false;
        try { box.SendBatchAtomic(authority, Frames(new byte[] { 7 })); }
        catch (InvalidOperationException) { stoppedRejected = true; }
        if (!stoppedRejected || box.QueuedMessages != 0)
            throw new Exception("STOP admitted actor DATA");
        var next = box.Restart();
        var staleRejected = false;
        try { box.SendBatchAtomic(authority, Frames(new byte[] { 7 })); }
        catch (InvalidOperationException) { staleRejected = true; }
        if (!staleRejected || box.QueuedMessages != 0)
            throw new Exception("stale generation resurrected old mailbox authority");
        box.SendBatchAtomic(next, Frames(new byte[] { 42 }));
        if (!box.TryReceive(out var revived) || revived is null || revived[0] != 42)
            throw new Exception("new generation failed bounded atomic batch admission");
        box.Dispose();

        // Stop vs an entire batch is serialized by the same mailbox lock.
        // Racing batch/stop may either admit everything then scrub on stop,
        // or deny everything. There must be no partially-visible suffix.
        for (var trial = 0; trial < 128; ++trial)
        {
            using var racing = new ActorMailbox((ulong)(100000 + trial), 4, 16);
            var cap = racing.CurrentRef;
            var ready = new System.Threading.ManualResetEventSlim();
            var a = Task.Run(() => {
                ready.Wait();
                try { racing.SendBatchAtomic(cap, Frames(new byte[] { 1 }, new byte[] { 2 })); }
                catch (InvalidOperationException) { }
            });
            var b = Task.Run(() => { ready.Wait(); racing.Stop(); });
            ready.Set();
            Task.WaitAll(a, b);
            if (racing.QueuedMessages != 0)
                throw new Exception("race allowed DATA past STOP");
            try { racing.SendBatchAtomic(cap, Frames(new byte[] { 3 })); }
            catch (InvalidOperationException) { continue; }
            throw new Exception("post-STOP batch admission bypass");
        }
        Console.WriteLine("PASS .NET bounded atomic actor-turn DATA, alias isolation, quota, stop/restart fence, 128 races (HOST_ONLY)");
    }
}
