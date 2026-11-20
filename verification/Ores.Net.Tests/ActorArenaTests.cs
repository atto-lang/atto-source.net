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
        ExpectFailure(() => a.BeginTurn(101, 2)); // a recycled actor ID is not an incarnation grant
        ExpectFailure(() => b.BeginTurn(101, 1)); // a different actor cannot enter another arena
        var ta = a.BeginTurn(101, 1);
        var tb = b.BeginTurn(102, 1);
        var h = a.Allocate(ta, 2);
        a.Write(ta, h, new byte[] { 7, 9 });
        // The token alone cannot authorize a concurrent physical carrier.
        var wrongCarrierReadDenied = false;
        var wrongCarrierEndDenied = false;
        var intruder = new System.Threading.Thread(() => {
            try { _ = a.Read(ta, h); }
            catch (InvalidOperationException) { wrongCarrierReadDenied = true; }
            try { a.EndTurn(ta); }
            catch (InvalidOperationException) { wrongCarrierEndDenied = true; }
        });
        intruder.Start();
        intruder.Join();
        if (!wrongCarrierReadDenied || !wrongCarrierEndDenied)
            throw new Exception("actor arena accepted a foreign physical carrier");
        if (!a.Read(ta, h).SequenceEqual(new byte[] { 7, 9 }))
            throw new Exception("persistent arena read/write");
        ExpectFailure(() => b.Read(tb, h));
        ExpectFailure(() => a.Read(tb, h));
        var copy = a.Read(ta, h);
        copy[0] = 99;
        if (a.Read(ta, h)[0] != 7) throw new Exception("read returned mutable alias");
        var scratch = a.AllocateScratch(ta, 2);
        a.WriteScratch(ta, scratch, new byte[] { 5, 6 });
        // Even an in-bounds range must refer to an issued scratch allocation,
        // not a fabricated subrange of the actor's scratch memory.
        var scratchType = typeof(ActorArena.Scratch);
        var flags = System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic;
        var domainField = scratchType.GetField("Domain", flags)
            ?? throw new Exception("scratch domain fixture unavailable");
        var scratchCtor = scratchType.GetConstructors(flags).Single();
        var forged = (ActorArena.Scratch)scratchCtor.Invoke(new object[] {
            (Guid)domainField.GetValue(scratch)!, ta, 1, 1
        });
        ExpectFailure(() => a.ReadScratch(ta, forged));
        if (!a.ReadScratch(ta, scratch).SequenceEqual(new byte[] { 5, 6 }))
            throw new Exception("valid issued scratch handle rejected");
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
        // A new carrier may resume the actor only after the old turn ends.
        // Persistent bytes survive; scratch authority and contents do not.
        using (var migrating = new ActorArena(901, 7))
        {
            var first = migrating.BeginTurn(901, 7);
            var persistent = migrating.Allocate(first, 4);
            migrating.Write(first, persistent, new byte[] { 1, 2, 3, 4 });
            var staleScratch = migrating.AllocateScratch(first, 4);
            migrating.WriteScratch(first, staleScratch, new byte[] { 9, 8, 7, 6 });
            migrating.EndTurn(first);
            Exception? carrierFailure = null;
            var migrated = false;
            var nextCarrier = new System.Threading.Thread(() =>
            {
                try
                {
                    var second = migrating.BeginTurn(901, 7);
                    ExpectFailure(() => migrating.Read(first, persistent));
                    ExpectFailure(() => migrating.ReadScratch(second, staleScratch));
                    if (!migrating.Read(second, persistent).SequenceEqual(new byte[] { 1, 2, 3, 4 }))
                        throw new Exception("persistent bytes lost during carrier migration");
                    var freshScratch = migrating.AllocateScratch(second, 4);
                    if (!migrating.ReadScratch(second, freshScratch).SequenceEqual(new byte[4]))
                        throw new Exception("scratch not zeroized across carrier migration");
                    migrating.EndTurn(second);
                    migrated = true;
                }
                catch (Exception failure) { carrierFailure = failure; }
            });
            nextCarrier.Start();
            if (!nextCarrier.Join(TimeSpan.FromSeconds(5)))
                throw new Exception("next carrier stalled");
            if (carrierFailure is not null)
                throw new Exception("actor carrier migration failed", carrierFailure);
            if (!migrated) throw new Exception("actor carrier did not resume");
            ExpectFailure(() => migrating.Read(first, persistent));
            ExpectFailure(() => migrating.BeginTurn(901, 8));
            migrating.Dispose();
        }
        using var restarted = new ActorArena(101, 2);
        var tr = restarted.BeginTurn(101, 2);
        ExpectFailure(() => restarted.Read(tr, h));
        restarted.EndTurn(tr);
        using var twin = new ActorArena(101, 1);
        var twinTurn = twin.BeginTurn(101, 1);
        ExpectFailure(() => twin.Read(twinTurn, h)); // same numeric owner/incarnation, separate domain
        twin.EndTurn(twinTurn);
    }
}
