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
        // Lifetime-only external leases model continuations, FFI and I/O
        // callbacks. Retirement must revoke arena operations immediately but
        // keep native storage pinned until the last lease is released.
        var quiescing = new ActorArena(1201, 9);
        var qt = quiescing.BeginTurn(1201, 9);
        var qh = quiescing.Allocate(qt, 4);
        quiescing.Write(qt, qh, new byte[] { 1, 4, 9, 16 });
        var external = quiescing.AcquireExternalLease(qt);
        if (quiescing.OutstandingExternalLeases != 1)
            throw new Exception("external arena lease was not charged");
        quiescing.EndTurn(qt);
        quiescing.Dispose();
        if (!quiescing.Retired || quiescing.PhysicallyDisposed)
            throw new Exception("external lease did not pin physical actor memory");
        ExpectFailure(() => quiescing.BeginTurn(1201, 9));

        Exception? leaseReleaseFailure = null;
        var callbackCarrier = new System.Threading.Thread(() =>
        {
            try
            {
                external.Dispose();
                external.Dispose(); // idempotent even on callback cleanup retry
            }
            catch (Exception failure) { leaseReleaseFailure = failure; }
        });
        callbackCarrier.Start();
        if (!callbackCarrier.Join(TimeSpan.FromSeconds(5)))
            throw new Exception("external lease release stalled");
        if (leaseReleaseFailure is not null)
            throw new Exception("external lease release failed", leaseReleaseFailure);
        if (!quiescing.PhysicallyDisposed || quiescing.OutstandingExternalLeases != 0)
            throw new Exception("last external lease did not trigger physical reclaim");
        quiescing.Dispose();
        // Runtime bridge: register the Task while the turn is valid, then
        // prove completion may release its lifetime pin from a different carrier.
        using var taskPinned = new ActorArena(405, 1);
        var taskTurn = taskPinned.BeginTurn(405, 1);
        var completion = new System.Threading.Tasks.TaskCompletionSource<int>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var trackedCompletion = taskPinned.TrackExternalCompletion(taskTurn, completion.Task);
        if (taskPinned.OutstandingExternalLeases != 1)
            throw new Exception("Task bridge did not acquire external lifetime pin");
        taskPinned.EndTurn(taskTurn);
        taskPinned.Dispose();
        if (!taskPinned.Retired || taskPinned.PhysicallyDisposed)
            throw new Exception("pending Task must pin physical actor arena lifetime");
        completion.SetResult(17);
        if (trackedCompletion.GetAwaiter().GetResult() != 17)
            throw new Exception("tracked Task result changed");
        if (!taskPinned.PhysicallyDisposed || taskPinned.OutstandingExternalLeases != 0)
            throw new Exception("Task completion did not release actor arena lifetime");


        // Every external Task terminal path must release its lifetime-only pin.
        // This bridge covers the external operation itself; a future guest
        // continuation that needs actor state still requires its own lifetime pin.
        using (var faultPinned = new ActorArena(406, 1))
        {
            var turn = faultPinned.BeginTurn(406, 1);
            var source = new System.Threading.Tasks.TaskCompletionSource<int>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var tracked = faultPinned.TrackExternalCompletion(turn, source.Task);
            faultPinned.EndTurn(turn);
            faultPinned.Dispose();
            var expected = new InvalidOperationException("synthetic external failure");
            source.SetException(expected);
            try
            {
                _ = tracked.GetAwaiter().GetResult();
                throw new Exception("faulted external Task unexpectedly succeeded");
            }
            catch (InvalidOperationException actual) when (ReferenceEquals(actual, expected)) { }
            if (!faultPinned.PhysicallyDisposed || faultPinned.OutstandingExternalLeases != 0)
                throw new Exception("faulted Task did not release actor arena lifetime");
        }

        using (var cancelledPinned = new ActorArena(407, 1))
        {
            var turn = cancelledPinned.BeginTurn(407, 1);
            var source = new System.Threading.Tasks.TaskCompletionSource<int>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var tracked = cancelledPinned.TrackExternalCompletion(turn, source.Task);
            cancelledPinned.EndTurn(turn);
            cancelledPinned.Dispose();
            source.SetCanceled();
            try
            {
                _ = tracked.GetAwaiter().GetResult();
                throw new Exception("cancelled external Task unexpectedly succeeded");
            }
            catch (System.Threading.Tasks.TaskCanceledException) { }
            if (!cancelledPinned.PhysicallyDisposed ||
                cancelledPinned.OutstandingExternalLeases != 0)
                throw new Exception("cancelled Task did not release actor arena lifetime");
        }

        using (var settledPinned = new ActorArena(408, 1))
        {
            var turn = settledPinned.BeginTurn(408, 1);
            var tracked = settledPinned.TrackExternalCompletion(
                turn, System.Threading.Tasks.Task.FromResult(31));
            if (tracked.GetAwaiter().GetResult() != 31)
                throw new Exception("already-settled Task result changed");
            if (settledPinned.OutstandingExternalLeases != 0)
                throw new Exception("already-settled Task retained a phantom lifetime pin");
            settledPinned.EndTurn(turn);
        }

        using var restarted = new ActorArena(101, 2);
        var tr = restarted.BeginTurn(101, 2);
        ExpectFailure(() => restarted.Read(tr, h));
        restarted.EndTurn(tr);
    }
}
