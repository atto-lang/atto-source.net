using Ores.Net.Runtime;

internal static class ActorArenaTests
{
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   ArgumentException or ArgumentOutOfRangeException) { return; }
        throw new Exception("expected arena rejection");
    }

    public static void Run()
    {
        using var a = new ActorArena(101, 1);
        using var b = new ActorArena(102, 1);
        var ta = a.BeginTurn(101, 1);
        var tb = b.BeginTurn(102, 1);
        var h = a.Allocate(ta, 2);
        a.Write(ta, h, new byte[] { 7, 9 });
        if (!a.Read(ta, h).SequenceEqual(new byte[] { 7, 9 }))
            throw new Exception("persistent arena read/write");
        ExpectFailure(() => b.Read(tb, h));
        ExpectFailure(() => a.Read(tb, h));
        var copy = a.Read(ta, h);
        copy[0] = 99;
        if (a.Read(ta, h)[0] != 7) throw new Exception("read returned mutable alias");
        var scratch = a.AllocateScratch(ta, 2);
        a.WriteScratch(ta, scratch, new byte[] { 5, 6 });
        ExpectFailure(() => a.Dispose());
        a.EndTurn(ta);
        b.EndTurn(tb);
        var next = a.BeginTurn(101, 1);
        ExpectFailure(() => a.ReadScratch(next, scratch));
        var fresh = a.AllocateScratch(next, 2);
        if (!a.ReadScratch(next, fresh).SequenceEqual(new byte[] { 0, 0 }))
            throw new Exception("scratch bytes survived previous turn");
        if (a.Read(next, h)[0] != 7) throw new Exception("persistent bytes lost");
        a.Drop(next, h);
        ExpectFailure(() => a.Read(next, h));
        // A recycled native slot may never revive the retired generation,
        // and its new owner must observe zeroed rather than old bytes.
        var replacement = a.Allocate(next, 2);
        if (!a.Read(next, replacement).SequenceEqual(new byte[] { 0, 0 }))
            throw new Exception("recycled native slot was not zeroized");
        ExpectFailure(() => a.Read(next, h));
        a.Drop(next, replacement);
        for (var i = 0; i < 4096; ++i)
        {
            var transient = a.Allocate(next, 2);
            a.Write(next, transient, new byte[] { 7, 9 });
            a.Drop(next, transient);
            ExpectFailure(() => a.Read(next, transient));
        }
        a.EndTurn(next);
        a.Dispose(); a.Dispose();
        ExpectFailure(() => a.BeginTurn(101, 1));
        // Host CONTROL and actor carriers must not park on a held arena
        // monitor. Hold it on another thread and require fail-fast rejection.
        using (var contended = new ActorArena(510, 3))
        {
            var field = typeof(ActorArena).GetField("_gate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field is null) throw new Exception("arena monitor fixture unavailable");
            var gate = field.GetValue(contended)!;
            using var entered = new System.Threading.ManualResetEventSlim(false);
            using var release = new System.Threading.ManualResetEventSlim(false);
            var holder = new System.Threading.Thread(() =>
            {
                lock (gate)
                {
                    entered.Set();
                    release.Wait();
                }
            });
            holder.Start();
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(5)))
                    throw new Exception("arena contention fixture stalled");
                ExpectFailure(() => contended.BeginTurn(510, 3));
                ExpectFailure(contended.Dispose);
            }
            finally
            {
                release.Set();
                holder.Join();
            }
            var admitted = contended.BeginTurn(510, 3);
            contended.EndTurn(admitted);
            contended.Dispose();
        }
        using var restarted = new ActorArena(101, 2);
        var tr = restarted.BeginTurn(101, 2);
        ExpectFailure(() => restarted.Read(tr, h));
        restarted.EndTurn(tr);
    }
}
