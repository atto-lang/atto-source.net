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
        // Reflection in a trusted host can reconstruct the public numeric
        // tuple; that must not create an actor-owned native memory capability.
        var handleType = typeof(ActorArena.Handle);
        var handleFlags = System.Reflection.BindingFlags.Instance |
                          System.Reflection.BindingFlags.NonPublic;
        var handleCtor = handleType.GetConstructors(handleFlags).Single();
        var handleDomain = (Guid)handleType.GetField("Domain", handleFlags)!.GetValue(h)!;
        var handleSlot = (int)handleType.GetField("Slot", handleFlags)!.GetValue(h)!;
        var handleGen = (ulong)handleType.GetField("Generation", handleFlags)!.GetValue(h)!;
        var forgedSameHandle = (ActorArena.Handle)handleCtor.Invoke(
            new object[] { handleDomain, handleSlot, handleGen });
        ExpectFailure(() => a.Read(ta, forgedSameHandle));

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
        var exactScratchClone = (ActorArena.Scratch)scratchCtor.Invoke(
            new object[] { (Guid)domainField.GetValue(scratch)!, ta, 0, 2 });
        ExpectFailure(() => a.ReadScratch(ta, exactScratchClone));

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
        // Carrier authority must retain the live Thread object itself, not a
        // recyclable ManagedThreadId integer. Keep the active turn valid, then
        // replace only the recorded carrier with a terminated Thread object:
        // access must fail until the real owning Thread reference is restored.
        using (var identityBound = new ActorArena(610, 4))
        {
            var turn = identityBound.BeginTurn(610, 4);
            var handle = identityBound.Allocate(turn, 2);
            identityBound.Write(turn, handle, new byte[] { 8, 6 });
            var carrierField = typeof(ActorArena).GetField("_carrier",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("carrier field fixture unavailable");
            if (carrierField.FieldType != typeof(System.Threading.Thread))
                throw new Exception("carrier field must retain Thread object identity");

            var finished = new System.Threading.Thread(() => { });
            finished.Start();
            if (!finished.Join(TimeSpan.FromSeconds(5)))
                throw new Exception("terminated-carrier fixture stalled");
            carrierField.SetValue(identityBound, finished);
            ExpectFailure(() => identityBound.Read(turn, handle));

            carrierField.SetValue(identityBound, System.Threading.Thread.CurrentThread);
            if (!identityBound.Read(turn, handle).SequenceEqual(new byte[] { 8, 6 }))
                throw new Exception("restored live carrier identity rejected");
            identityBound.EndTurn(turn);
        }

        // A suspended actor may resume on another carrier only after ending
        // the previous admitted turn. Persistent bytes survive the handoff.
        using (var migrating = new ActorArena(711, 2))
        {
            var firstTurn = migrating.BeginTurn(711, 2);
            var persistent = migrating.Allocate(firstTurn, 3);
            migrating.Write(firstTurn, persistent, new byte[] { 4, 2, 1 });
            Exception? failure = null;
            var wrongCarrier = new System.Threading.Thread(() =>
            {
                try
                {
                    ExpectFailure(() => migrating.Read(firstTurn, persistent));
                    ExpectFailure(() => migrating.BeginTurn(711, 2));
                }
                catch (Exception ex) { failure = ex; }
            });
            wrongCarrier.Start();
            if (!wrongCarrier.Join(TimeSpan.FromSeconds(5)))
                throw new Exception("actor wrong-carrier test stalled");
            if (failure is not null) throw new Exception("wrong carrier admission", failure);
            migrating.EndTurn(firstTurn);

            failure = null;
            var nextCarrier = new System.Threading.Thread(() =>
            {
                try
                {
                    var secondTurn = migrating.BeginTurn(711, 2);
                    if (!migrating.Read(secondTurn, persistent)
                                  .SequenceEqual(new byte[] { 4, 2, 1 }))
                        throw new Exception("persistent data lost on carrier migration");
                    ExpectFailure(() => migrating.Read(firstTurn, persistent));
                    migrating.EndTurn(secondTurn);
                }
                catch (Exception ex) { failure = ex; }
            });
            nextCarrier.Start();
            if (!nextCarrier.Join(TimeSpan.FromSeconds(5)))
                throw new Exception("actor migration test stalled");
            if (failure is not null) throw new Exception("carrier migration", failure);
        }
        using var restarted = new ActorArena(101, 2);
        var tr = restarted.BeginTurn(101, 2);
        ExpectFailure(() => restarted.Read(tr, h));
        restarted.EndTurn(tr);
    }
}
