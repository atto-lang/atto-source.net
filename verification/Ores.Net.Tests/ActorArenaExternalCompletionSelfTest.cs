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
        Console.WriteLine("ATTO_NET_EXTERNAL_COMPLETION_PIN_BEFORE_START_PASS");
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
