using Ores.Net.Runtime;

internal static class ActorGuestHeap32SelfTest
{
    public static void Run()
    {
        PerActorReferencesSurviveTurnsAndDenyStaleAccess();
        GenerationExhaustionFailsClosedWithoutWrapping();
        Console.WriteLine("PASS .NET actor-local 32-bit guest refs (host runtime only; not compiled guest)");
    }

    private static ActorRuntimeOptions Options(int slots = 8) => new()
    {
        MaxActors = 8,
        MessagesPerDispatch = 8,
        MailboxMessages = 32,
        MailboxBytes = 4096,
        ActorPersistentBytes = 2048,
        ActorScratchBytes = 128,
        ActorMaxAllocations = slots,
        ActorMaxLifetimePins = 8,
        ActorMaxReadLoans = 8,
        NativeBudgetBytes = 1024 * 1024,
        Dispatcher = new ActorDispatcherOptions
        {
            PrivateWorkers = 1,
            SharedWorkers = 1,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 16
        }
    };

    private static void PerActorReferencesSurviveTurnsAndDenyStaleAccess()
    {
        using var runtime = new ActorRuntime(Options());
        using var done = new CountdownEvent(4);
        object failureGate = new();
        Exception? failure = null;
        uint leftRoot = 0, rightRoot = 0;
        uint leftStale = 0, rightStale = 0;
        ActorContext? expiredContext = null;

        ActorMessageHandler Handler(bool left) => (context, message) =>
        {
            try
            {
                Require(message.Length == 1, "fixture message length");
                if (message.Span[0] == 1)
                {
                    uint root = context.AllocateGuestObject32(16);
                    Require(root != 0, "zero remains the null ref");
                    context.WriteGuestObject32(root, 0,
                        new byte[] { left ? (byte)0xA1 : (byte)0xB2 });
                    uint child = context.AllocateGuestObject32(8);
                    context.WriteGuestReference32(root, 8, child);
                    Require(context.ReadGuestReference32(root, 8) == child,
                        "nested pointer occupies one little-endian uint32 word");
                    ExpectInvalid(() => context.WriteGuestReference32(root, 8, uint.MaxValue),
                        "forged target cannot enter a typed field");
                    context.DropGuestObject32(child);
                    ExpectInvalid(() => context.ReadGuestReference32(root, 8),
                        "dropped child pointer read must fail");
                    context.WriteGuestReference32(root, 8, 0);
                    if (left)
                    {
                        leftRoot = root;
                        leftStale = child;
                        expiredContext = context;
                    }
                    else
                    {
                        rightRoot = root;
                        rightStale = child;
                    }
                }
                else if (message.Span[0] == 2)
                {
                    uint root = left ? leftRoot : rightRoot;
                    uint stale = left ? leftStale : rightStale;
                    byte[] data = new byte[1];
                    context.ReadGuestObject32(root, 0, data);
                    Require(data[0] == (left ? (byte)0xA1 : (byte)0xB2),
                        "actor-private object survives physical turn migration");

                    uint fresh = context.AllocateGuestObject32(8);
                    Require(fresh != stale, "slot reuse increments 16-bit generation");
                    ExpectInvalid(() => context.ReadGuestObject32(stale, 0, data),
                        "stale guest reference is not revived by slot reuse");
                    ExpectInvalid(() => context.WriteGuestObject32(stale, 0, data),
                        "stale guest write cannot mutate new allocation");
                    ExpectInvalid(() => context.DropGuestObject32(stale),
                        "double drop of stale guest reference denied");
                    ExpectInvalid(() => context.ReadGuestObject32(root, 16, data),
                        "guest reads must be allocation-bounded");
                    ExpectInvalid(() => context.DropGuestObject32(0),
                        "zero cannot be dropped");
                    context.WriteGuestReference32(root, 8, fresh);
                    Require(context.ReadGuestReference32(root, 8) == fresh,
                        "fresh reference round trip");
                    context.WriteGuestObject32(root, 8, new byte[] { 255, 255, 255, 255 });
                    ExpectInvalid(() => context.ReadGuestReference32(root, 8),
                        "malformed native bytes cannot be elevated to a guest ref");
                    context.WriteGuestReference32(root, 8, 0);
                    context.DropGuestObject32(fresh);
                    context.DropGuestObject32(root);
                }
                else
                {
                    throw new InvalidOperationException("Unexpected test message");
                }
            }
            catch (Exception error)
            {
                lock (failureGate) failure ??= error;
            }
            finally
            {
                done.Signal();
            }
        };

        ActorRef leftActor = runtime.Spawn(ActorKind.Private, Handler(true));
        ActorRef rightActor = runtime.Spawn(ActorKind.Shared, Handler(false));
        Require(runtime.NativeReservedBytes == 2 * (2048 + 128),
            "reference table does not reserve a second native payload arena");

        runtime.Send(leftActor, new byte[] { 1 });
        runtime.Send(rightActor, new byte[] { 1 });
        runtime.Send(leftActor, new byte[] { 2 });
        runtime.Send(rightActor, new byte[] { 2 });
        Require(done.Wait(TimeSpan.FromSeconds(10)), "both actors complete both turns");
        if (failure is not null) throw new Exception("guest ref32 actor fixture failed", failure);
        Require(leftRoot == rightRoot && leftRoot != 0,
            "the same actor-local reference word can map to independent storage");
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 4,
            TimeSpan.FromSeconds(10)) && runtime.FaultedTurns == 0,
            "four actual host actor message turns completed");

        Require(expiredContext is not null, "retained-context fixture");
        ExpectInvalid(() => expiredContext!.AllocateGuestObject32(8),
            "retained context cannot allocate after physical turn");
        ExpectInvalid(() => expiredContext!.ReadGuestObject32(leftRoot, 0, new byte[1]),
            "retained context cannot dereference after physical turn");

        runtime.Stop(leftActor);
        runtime.Stop(rightActor);
        Require(SpinWait.SpinUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(10)),
            "actor retirement must reclaim both physical native payload slabs");
    }

    private static void GenerationExhaustionFailsClosedWithoutWrapping()
    {
        using var runtime = new ActorRuntime(Options(slots: 1));
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        ActorRef actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            try
            {
                uint oldest = 0;
                for (int i = 0; i < ushort.MaxValue; i++)
                {
                    uint reference = context.AllocateGuestObject32(8);
                    if (i == 0) oldest = reference;
                    context.DropGuestObject32(reference);
                }
                ExpectInvalid(() => context.AllocateGuestObject32(8),
                    "generation exhaustion retires the only slot");
                ExpectInvalid(() => context.ReadGuestObject32(oldest, 0, new byte[1]),
                    "oldest reference cannot alias wrapped generation");
            }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        });
        runtime.Send(actor, new byte[] { 1 });
        Require(finished.Wait(TimeSpan.FromSeconds(20)),
            "bounded slot generation rollover fixture completed");
        if (failure is not null) throw new Exception("generation exhaustion fixture failed", failure);
        Require(runtime.FaultedTurns == 0, "generation exhaustion does not fault host actor");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(10)),
            "exhausted slot still permits deterministic native arena retirement");
    }

    private static void Require(bool condition, string why)
    {
        if (!condition) throw new Exception("ActorGuestHeap32 FAILED: " + why);
    }

    private static void ExpectInvalid(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("ActorGuestHeap32 expected rejection: " + why);
    }
}
