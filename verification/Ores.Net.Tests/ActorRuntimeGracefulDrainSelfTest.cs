using Ores.Net.Runtime;

// HOST-RUNTIME ONLY. This is not compiled AttoLang source execution and is
// not a substitute for supervisor-owned actor pipeline completion receipts.
internal static class ActorRuntimeGracefulDrainSelfTest
{
    public static void Run()
    {
        PreservesAcceptedMessagesAndRejectsLateAdmission();
        TwoActorHostForwardingRequiresUpstreamFirstDrain();
        ForgedRefCannotSealSomeoneElsesMailbox();
        Console.WriteLine("PASS .NET host actor graceful drain, admission and " +
            "two-actor forwarding (NOT SOURCE GUEST/NATIVE AOT EVIDENCE)");
    }

    private static ActorRuntimeOptions Options() => new()
    {
        MaxActors = 8,
        MessagesPerDispatch = 1,
        MailboxMessages = 8,
        MailboxBytes = 128,
        ActorPersistentBytes = 4096,
        ActorScratchBytes = 512,
        ActorMaxAllocations = 64,
        ActorMaxLifetimePins = 16,
        ActorMaxReadLoans = 16,
        NativeBudgetBytes = 1024 * 1024,
        Dispatcher = new ActorDispatcherOptions
        {
            PrivateWorkers = 2,
            SharedWorkers = 2,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 32
        }
    };

    private static void PreservesAcceptedMessagesAndRejectsLateAdmission()
    {
        using var runtime = new ActorRuntime(Options());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int handled = 0;
        var actor = runtime.Spawn(ActorKind.Private, (_, message) =>
        {
            if (message.Span[0] == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new Exception("timed out waiting for drain test");
            }
            Interlocked.Increment(ref handled);
        });

        runtime.Send(actor, new byte[] { 1 });
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)),
                "handler entered before graceful terminal barrier");
            runtime.Send(actor, new byte[] { 2 });
            Task drained = runtime.DrainAndStop(actor);
            Require(!drained.IsCompleted,
                "generation-bound completion cannot precede active-turn exit");

            bool denied = false;
            try { runtime.Send(actor, new byte[] { 3 }); }
            catch (InvalidOperationException) { denied = true; }
            Require(denied, "new DATA rejected after grace-period admission seal");
            Require(runtime.LiveActors == 1 && handled == 0,
                "active turn cannot finalize or discard already accepted data");
        }
        finally
        {
            release.Set();
        }

        Require(WaitUntil(() => runtime.LiveActors == 0),
            "draining actor retires after queued work completes");
        Require(handled == 2 && runtime.CompletedMessages == 2 &&
                runtime.FaultedTurns == 0,
            "all accepted DATA processed, none dropped by drain");
        Require(runtime.NativeReservedBytes == 0,
            "private arena reclaimed after successful drain");
    }

    private static void TwoActorHostForwardingRequiresUpstreamFirstDrain()
    {
        using var runtime = new ActorRuntime(Options());
        int result = 0;
        var consumer = runtime.Spawn(ActorKind.Private, (_, message) =>
        {
            Require(message.Length == sizeof(int), "consumer payload is four bytes");
            int input = BitConverter.ToInt32(message.Span);
            Interlocked.Exchange(ref result, checked(input * 2));
        });
        var producer = runtime.Spawn(ActorKind.Private, (_, message) =>
        {
            Require(message.Length == sizeof(int), "producer payload is four bytes");
            int input = BitConverter.ToInt32(message.Span);
            runtime.Send(consumer, BitConverter.GetBytes(checked(input + 1)));
        });

        runtime.Send(producer, BitConverter.GetBytes(2));
        Task upstreamDone = runtime.DrainAndStop(producer);

        // Without this authenticated completion barrier, sealing the sink
        // before the producer forwards can reject already accepted input.
        Require(upstreamDone.Wait(TimeSpan.FromSeconds(5)),
            "producer has finished its forwarded turn before sink is sealed");
        Task sinkDone = runtime.DrainAndStop(consumer);
        Require(sinkDone.Wait(TimeSpan.FromSeconds(5)) &&
                runtime.LiveActors == 0,
            "consumer drained before terminal retirement");
        Require(runtime.CompletedMessages == 2 &&
                runtime.FaultedTurns == 0 && result == 6,
            "two host DATA turns deliver 2 -> 3 -> 6 without loss");
        Require(runtime.NativeReservedBytes == 0,
            "two independent actor arenas release their budgets");
    }

    private static void ForgedRefCannotSealSomeoneElsesMailbox()
    {
        using var runtime = new ActorRuntime(Options());
        var actor = runtime.Spawn(ActorKind.Private, static (_, _) => { });
        var forged = new ActorMailbox(actor.Id).CurrentRef;
        Require(forged != actor && forged.Id == actor.Id,
            "same numeric ID but foreign generation/nonce authority");

        bool rejected = false;
        try { runtime.DrainAndStop(forged); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && runtime.LiveActors == 1,
            "foreign authority cannot change graceful admission/teardown");
        runtime.Send(actor, new byte[] { 7 });
        Task legitimateDone = runtime.DrainAndStop(actor);
        Require(legitimateDone.Wait(TimeSpan.FromSeconds(5)) &&
                WaitUntil(() => runtime.LiveActors == 0 &&
                               runtime.CompletedMessages == 1),
            "authorized incarnation retains mailbox admission and drains");
    }

    private static bool WaitUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    private static void Require(bool ok, string reason)
    {
        if (!ok) throw new Exception("Graceful actor drain regression: " + reason);
    }
}
