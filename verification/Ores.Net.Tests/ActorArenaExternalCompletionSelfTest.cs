using System;
using System.Threading;
using System.Threading.Tasks;
using Ores.Net.Runtime;

// Same exact regression executes in CoreCLR and standalone NativeAOT.
internal static class ActorArenaExternalCompletionSelfTest
{
    public static void Run()
    {
        // A completion is a physical lifetime receipt, not a resumed turn.
        for (int outcome = 0; outcome < 3; ++outcome)
        {
            using var arena = new ActorArena(3002, (ulong)outcome + 1, 1024, 256, 4, 1, 2);
            using var sibling = new ActorArena(3003, 1, 1024, 256, 4, 1, 2);
            var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> completion;
            int starts = 0;
            ActorArenaTurn stale;
            using (var turn = arena.EnterTurn(3002, (ulong)outcome + 1))
            {
                stale = turn;
                completion = arena.TrackExternalCompletion<int>(turn, () =>
                {
                    ++starts;
                    Require(arena.OutstandingLifetimePins == 1, "pin must precede external start");
                    return source.Task;
                });
                ExpectInvalid(() => arena.TrackExternalCompletion(turn, () =>
                {
                    ++starts;
                    return Task.CompletedTask;
                }), "quota rejection must precede external start");
                Require(starts == 1 && !completion.IsCompleted, "only admitted factory starts");
            }
            Require(!arena.Retire(), "pending physical completion delays reclaim");
            ExpectInvalid(() => arena.TrackExternalCompletion(stale, () =>
            {
                ++starts;
                return Task.CompletedTask;
            }), "retired/stale turn cannot start external work");
            using (var peer = sibling.EnterTurn(3003, 1))
            {
                var value = sibling.AllocatePersistent(peer, 8);
                sibling.Write(peer, value, 0, new byte[] { 42 });
                var bytes = new byte[1];
                sibling.Read(peer, value, 0, bytes);
                Require(bytes[0] == 42, "sibling stays live during retirement");
            }
            var fault = new ApplicationException("external fault");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Task.Run(() =>
            {
                if (outcome == 0) source.SetResult(42);
                else if (outcome == 1) source.SetException(fault);
                else source.SetCanceled(cts.Token);
            }).GetAwaiter().GetResult();
            try
            {
                int result = completion.GetAwaiter().GetResult();
                Require(outcome == 0 && result == 42, "success value preserved");
            }
            catch (ApplicationException error)
            {
                Require(outcome == 1 && ReferenceEquals(error, fault), "original fault preserved");
            }
            catch (OperationCanceledException error)
            {
                Require(outcome == 2 && error.CancellationToken == cts.Token, "cancellation preserved");
            }
            Require(arena.IsReclaimed && arena.OutstandingLifetimePins == 0 && starts == 1,
                "completion releases exactly once before the observer returns");
        }

        using var immediate = new ActorArena(3004, 1, 1024, 256, 4, 1, 2);
        using (var turn = immediate.EnterTurn(3004, 1))
        {
            immediate.TrackExternalCompletion(turn, () => Task.CompletedTask).GetAwaiter().GetResult();
            Require(immediate.OutstandingLifetimePins == 0, "completed non-generic task releases pin");
            Require(immediate.TrackExternalCompletion<int>(turn, () => Task.FromResult(42))
                .GetAwaiter().GetResult() == 42, "completed generic task preserves value");
            ExpectInvalid(() => immediate.TrackExternalCompletion(turn, () =>
                throw new InvalidOperationException("start fault")), "synchronous start fault propagates");
            Require(immediate.OutstandingLifetimePins == 0, "synchronous start fault refunds pin");
            ExpectInvalid(() => immediate.TrackExternalCompletion(turn, () => null!),
                "null completion cannot strand a pin");
            Require(immediate.OutstandingLifetimePins == 0, "null completion refunds pin");
        }
        Require(immediate.Retire(), "no leaked pins after immediate and rejected operations");
        ConcurrentPhysicalCompletionQuiescence();
        RacePhysicalOwnersAndRetirement();
        Console.WriteLine("ATTO_NET_EXTERNAL_COMPLETION_PIN_BEFORE_START_PASS");
    }


    // Port the unique multi-owner case from stacked #137 onto the current
    // arena's issued lifetime-pin API. Check both completion orders and keep
    // the process/tenant charge until the final physical owner settles.
    private static void ConcurrentPhysicalCompletionQuiescence()
    {
        for (int order = 0; order < 2; order++)
        {
            var budget = new ActorArenaBudget(1280);
            long processBefore = ActorArenaBudget.Process.ReservedBytes;
            using var arena = new ActorArena(3010, (ulong)order + 1, 1024, 256,
                4, 2, 2, budget);
            var cancelled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var faulted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> cancelReceipt, faultReceipt;
            int starts = 0;
            using (var turn = arena.EnterTurn(3010, (ulong)order + 1))
            {
                cancelReceipt = arena.TrackExternalCompletion<int>(turn, () =>
                {
                    Require(arena.OutstandingLifetimePins == 1, "first factory must be pinned");
                    starts++;
                    return cancelled.Task;
                });
                faultReceipt = arena.TrackExternalCompletion<int>(turn, () =>
                {
                    Require(arena.OutstandingLifetimePins == 2, "second factory must be independently pinned");
                    starts++;
                    return faulted.Task;
                });
                ExpectInvalid(() => arena.TrackExternalCompletion(turn, () =>
                {
                    starts++;
                    return Task.CompletedTask;
                }), "third factory exceeds two-pin quota");
                Require(starts == 2 && arena.OutstandingLifetimePins == 2,
                    "denied start must not change either receipt");
            }
            Require(!arena.Retire() && arena.IsRetired && !arena.IsReclaimed,
                "logical retirement must wait for both physical owners");
            var fault = new ApplicationException("second physical owner failed");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            void CancelOwner()
            {
                Task.Run(() => cancelled.SetCanceled(cts.Token))
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                try
                {
                    cancelReceipt.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    throw new Exception("cancelled owner unexpectedly succeeded");
                }
                catch (OperationCanceledException error) when (error.CancellationToken == cts.Token) { }
            }

            void FaultOwner()
            {
                Task.Run(() => faulted.SetException(fault))
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                try
                {
                    faultReceipt.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    throw new Exception("faulted owner unexpectedly succeeded");
                }
                catch (ApplicationException error) when (ReferenceEquals(error, fault)) { }
            }

            if (order == 0) CancelOwner(); else FaultOwner();
            Require(!arena.IsReclaimed && arena.OutstandingLifetimePins == 1 &&
                arena.ReservedNativeBytes == 1280 && budget.ReservedBytes == 1280 &&
                ActorArenaBudget.Process.ReservedBytes == processBefore + 1280,
                "first terminal receipt cannot refund the remaining owner's storage");
            if (order == 0) FaultOwner(); else CancelOwner();
            Require(arena.IsReclaimed && arena.OutstandingLifetimePins == 0 &&
                arena.ReservedNativeBytes == 0 && budget.ReservedBytes == 0 &&
                ActorArenaBudget.Process.ReservedBytes == processBefore,
                "final terminal receipt must reclaim and refund exactly once");
            Require(arena.Retire() && budget.ReservedBytes == 0 &&
                ActorArenaBudget.Process.ReservedBytes == processBefore,
                "repeated retirement cannot refund again");
        }
        Console.WriteLine("ATTO_NET_MULTI_EXTERNAL_COMPLETION_QUIESCENCE_PASS");
    }

    // Unlike the two ordered terminal-path checks, run the actual physical
    // owners and retirement concurrently. A third, deliberately withheld pin
    // proves storage remains charged after both racing callbacks settle.
    private static void RacePhysicalOwnersAndRetirement()
    {
        var budget = new ActorArenaBudget(1280);
        long processBefore = ActorArenaBudget.Process.ReservedBytes;
        for (int cycle = 0; cycle < 1024; ++cycle)
        {
            ulong incarnation = (ulong)cycle + 1;
            using var arena = new ActorArena(3011, incarnation, 1024, 256,
                4, 3, 2, budget);
            var left = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var right = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> leftReceipt, rightReceipt;
            ActorArenaLifetimePin withheld;
            using (var turn = arena.EnterTurn(3011, incarnation))
            {
                leftReceipt = arena.TrackExternalCompletion<int>(turn, () => left.Task);
                rightReceipt = arena.TrackExternalCompletion<int>(turn, () => right.Task);
                withheld = arena.AcquireLifetimePin(turn);
                Require(arena.OutstandingLifetimePins == 3, "three physical owners charged");
            }
            using (withheld)
            {
                using var go = new ManualResetEventSlim(false);
                var completeLeft = Task.Run(() => { go.Wait(); left.SetResult(41); });
                var completeRight = Task.Run(() => { go.Wait(); right.SetResult(42); });
                var retire = Task.Run(() =>
                {
                    go.Wait();
                    Require(!arena.Retire(), "withheld owner prevents concurrent retirement");
                });
                go.Set();
                Task.WhenAll(completeLeft, completeRight, retire)
                    .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var results = Task.WhenAll(leftReceipt, rightReceipt)
                    .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Require(results[0] == 41 && results[1] == 42, "racing receipts preserve values");
                Require(arena.IsRetired && !arena.IsReclaimed &&
                    arena.OutstandingLifetimePins == 1 && budget.ReservedBytes == 1280 &&
                    ActorArenaBudget.Process.ReservedBytes == processBefore + 1280,
                    "racing owners cannot release the withheld physical credit");
            }
            Require(arena.IsReclaimed && arena.OutstandingLifetimePins == 0 &&
                arena.ReservedNativeBytes == 0 && budget.ReservedBytes == 0 &&
                ActorArenaBudget.Process.ReservedBytes == processBefore,
                "last physical owner releases the full charge exactly once");
            Require(arena.Retire() && budget.ReservedBytes == 0,
                "retirement retry remains idempotent");
        }
        Console.WriteLine("ATTO_NET_CONCURRENT_OWNER_RETIREMENT_PASS cycles=1024");
    }

    private static void ExpectInvalid(Action action, string scenario)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("External completion unexpectedly admitted: " + scenario);
    }
    private static void Require(bool condition, string scenario)
    {
        if (!condition) throw new Exception("External completion regression: " + scenario);
    }
}
