using Ores.Net.Runtime;

internal static class ActorFinalizationV1SelfTest
{
    private static void Check(bool good, string message)
    {
        if (!good) throw new Exception("AF07 .NET mailbox: " + message);
    }

    public static void Run()
    {
        StaleRefCannotStopReplacementOrEraseMessages();
        SameNumericIdentityCannotForgeStopAuthority();
        ConcurrentRestartAndOldStopNeverFencesNewGeneration();
        Console.WriteLine("PASS .NET AF07 nonce/generation fenced mailbox stop (host only)");
    }

    private static void StaleRefCannotStopReplacementOrEraseMessages()
    {
        var box = new ActorMailbox(9801);
        var old = box.CurrentRef;
        var replacement = box.Restart();
        box.Send(replacement, new byte[] { 0xA5, 0x3C });
        Check(!box.TryStop(old), "stale actor ref was accepted");
        Check(box.CurrentRef == replacement && box.QueuedMessages == 1,
            "stale stop invalidated generation or erased queued bytes");
        Check(box.TryReceive(out byte[]? payload) && payload is { Length: 2 } &&
              payload[0] == 0xA5 && payload[1] == 0x3C,
            "stale stop changed the replacement's payload");
        Check(box.TryStop(replacement), "current reference could not stop");
        Check(box.TryStop(replacement), "repeated current stop was not idempotent");
        Check(box.QueuedMessages == 0, "current stop failed to clear queue");
        bool denied = false;
        try { box.Send(replacement, new byte[] { 7 }); }
        catch (InvalidOperationException) { denied = true; }
        Check(denied, "stopped incarnation accepted another message");
    }

    private static void SameNumericIdentityCannotForgeStopAuthority()
    {
        // IDs and generation alone are never authority; nonce is essential.
        var owner = new ActorMailbox(9802);
        var imposter = new ActorMailbox(9802);
        var original = owner.CurrentRef;
        owner.Send(original, new byte[] { 9, 8, 7 });
        Check(!owner.TryStop(imposter.CurrentRef) &&
              owner.CurrentRef == original && owner.QueuedMessages == 1,
            "foreign equal-ID/generation reference closed mailbox");
        Check(owner.TryReceive(out byte[]? payload) &&
              payload is { Length: 3 } && payload[0] == 9 &&
              payload[1] == 8 && payload[2] == 7,
            "foreign ref mutated queued data");
        Check(owner.TryStop(original), "actual capability could not stop actor");
    }

    private static void ConcurrentRestartAndOldStopNeverFencesNewGeneration()
    {
        for (int round = 0; round < 128; ++round)
        {
            var box = new ActorMailbox((ulong)(9900 + round));
            var old = box.CurrentRef;
            using var go = new ManualResetEventSlim(false);
            bool stopAccepted = false;
            ActorRef replacement = default;
            var stopper = Task.Run(() => {
                go.Wait();
                stopAccepted = box.TryStop(old);
            });
            var restarter = Task.Run(() => {
                go.Wait();
                replacement = box.Restart();
            });
            go.Set();
            Task.WaitAll(stopper, restarter);
            Check(replacement.Generation == old.Generation + 1 &&
                  box.CurrentRef == replacement,
                "restart failed to publish a fresh capability");
            // A stop issued before restart is valid; after restart it must
            // fail. Either ordering is accepted, neither may stop generation 2.
            Check(!box.TryStop(old), "old incarnation was accepted after restart");
            box.Send(replacement, new byte[] { (byte)(round % 251) });
            Check(box.TryReceive(out byte[]? payload) &&
                  payload is { Length: 1 } &&
                  payload[0] == (byte)(round % 251),
                "racing old stop made replacement unreachable");
            _ = stopAccepted;
        }
    }
}
