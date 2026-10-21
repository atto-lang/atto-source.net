using Ores.Net.Runtime;

internal static class ActorRuntimeSelfTest
{
    public static void Run()
    {
        ThreeKindsUseAssignedDomainsAndActorArenas();
        OneActorNeverRunsTwoHandlersConcurrently();
        DeliveredMessageStorageIsTurnBound();
        LifetimePinCanOutliveActorTurnAndStop();
        HandlerFaultRetiresOnlyThatActor();
        Console.WriteLine(
            "PASS .NET integrated actor runtime mailbox, arena, domains, stop and fault isolation");
    }

    private static ActorRuntimeOptions Options() => new()
    {
        MaxActors = 32,
        MessagesPerDispatch = 16,
        MailboxMessages = 256,
        MailboxBytes = 1024 * 1024,
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
            QueueCapacityPerDomain = 64
        }
    };

    private static void ThreeKindsUseAssignedDomainsAndActorArenas()
    {
        using var runtime = new ActorRuntime(Options());
        using var done = new CountdownEvent(3);
        string? privateThread = null;
        string? sharedThread = null;
        string? untrustedThread = null;

        ActorMessageHandler Make(ActorKind expected, Action<string?> capture) =>
            (context, message) =>
            {
                Require(context.Kind == expected, "handler kind matches actor cell");
                Require(message.Span.SequenceEqual(new byte[] { 7, 8, 9 }),
                    "runtime delivers copied mailbox payload");

                var owned = context.AllocatePersistent(8);
                context.Write(owned, 0, message.Span);
                var roundTrip = new byte[3];
                context.Read(owned, 0, roundTrip);
                Require(roundTrip.AsSpan().SequenceEqual(message.Span),
                    "actor handler reads its own unmanaged arena");
                context.ReleasePersistent(owned);

                capture(Thread.CurrentThread.Name);
                done.Signal();
            };

        var privateActor = runtime.Spawn(
            ActorKind.Private, Make(ActorKind.Private, value => privateThread = value));
        var sharedActor = runtime.Spawn(
            ActorKind.Shared, Make(ActorKind.Shared, value => sharedThread = value));
        var untrustedActor = runtime.Spawn(
            ActorKind.Untrusted, Make(ActorKind.Untrusted, value => untrustedThread = value));

        Require(runtime.StartedCarrierThreads == 0,
            "spawning actors creates no carrier threads");
        Require(runtime.LiveActors == 3, "three actor cells registered");
        Require(runtime.NativeReservedBytes == 3L * (4096 + 512),
            "each actor reserves private native arena capacity");

        runtime.Send(privateActor, new byte[] { 7, 8, 9 });
        runtime.Send(sharedActor, new byte[] { 7, 8, 9 });
        runtime.Send(untrustedActor, new byte[] { 7, 8, 9 });

        Require(done.Wait(TimeSpan.FromSeconds(5)),
            "three actor kinds complete one message");
        Require(privateThread?.StartsWith("ores-net-private-",
                    StringComparison.Ordinal) == true,
            "private actor executes on private domain");
        Require(sharedThread?.StartsWith("ores-net-shared-",
                    StringComparison.Ordinal) == true,
            "shared actor executes on shared domain");
        Require(untrustedThread?.StartsWith("ores-net-untrusted-",
                    StringComparison.Ordinal) == true,
            "untrusted actor executes on untrusted domain");
        Require(WaitUntil(() => runtime.CompletedMessages == 3),
            "completed message metric converges");

        runtime.Stop(privateActor);
        runtime.Stop(sharedActor);
        runtime.Stop(untrustedActor);
        Require(WaitUntil(() => runtime.LiveActors == 0),
            "stopped actors leave live registry");
        Require(WaitUntil(() => runtime.NativeReservedBytes == 0),
            "stopped actors refund native arena budget");
    }

    private static void OneActorNeverRunsTwoHandlersConcurrently()
    {
        using var runtime = new ActorRuntime(Options());
        const int messages = 96;
        using var done = new CountdownEvent(messages);
        int active = 0;
        int maxActive = 0;

        var actor = runtime.Spawn(ActorKind.Private, (_, _) =>
        {
            int now = Interlocked.Increment(ref active);
            int observed;
            while (now > (observed = Volatile.Read(ref maxActive)) &&
                Interlocked.CompareExchange(ref maxActive, now, observed) != observed)
            {
            }

            Thread.SpinWait(25_000);
            Interlocked.Decrement(ref active);
            done.Signal();
        });

        for (int i = 0; i < messages; i++)
            runtime.Send(actor, new byte[] { (byte)i });

        Require(done.Wait(TimeSpan.FromSeconds(10)),
            "serial actor drains bounded burst");
        Require(maxActive == 1,
            "one actor never runs more than one physical handler concurrently");
        Require(WaitUntil(() => runtime.CompletedMessages == messages),
            "serial burst completion accounting");

        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0),
            "serial actor finalizes");
    }

    private static void DeliveredMessageStorageIsTurnBound()
    {
        using var runtime = new ActorRuntime(Options());
        using var delivered = new ManualResetEventSlim();
        ReadOnlyMemory<byte> retained = default;

        var actor = runtime.Spawn(ActorKind.Shared, (_, message) =>
        {
            retained = message;
            delivered.Set();
        });

        var caller = new byte[] { 11, 22, 33 };
        runtime.Send(actor, caller);
        caller[0] = 99;

        Require(delivered.Wait(TimeSpan.FromSeconds(5)), "message delivered");
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "message handler returned before retained-storage check");
        Require(retained.Length == 3 && retained.Span.ToArray().All(b => b == 0),
            "runtime scrubs delivered mailbox storage after turn completion");
        Require(caller.SequenceEqual(new byte[] { 99, 22, 33 }),
            "runtime never scrubs caller-owned sender bytes");

        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0),
            "message-lifetime actor finalizes");
    }

    private static void LifetimePinCanOutliveActorTurnAndStop()
    {
        using var runtime = new ActorRuntime(Options());
        using var pinned = new ManualResetEventSlim();
        ActorArenaLifetimePin? lifetimePin = null;

        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            lifetimePin = context.PinLifetime();
            pinned.Set();
        });

        runtime.Send(actor, new byte[] { 1 });
        Require(pinned.Wait(TimeSpan.FromSeconds(5)),
            "handler acquires lifetime-only arena pin");
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "pinned actor turn completes");

        long reserved = runtime.NativeReservedBytes;
        Require(reserved == 4096 + 512,
            "one actor arena remains charged");
        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0),
            "stopped pinned actor leaves live registry");
        Require(runtime.NativeReservedBytes == reserved,
            "lifetime pin delays physical arena reclaim after actor stop");

        Task.Run(() => lifetimePin!.Dispose()).GetAwaiter().GetResult();
        lifetimePin!.Dispose();
        Require(WaitUntil(() => runtime.NativeReservedBytes == 0),
            "off-carrier pin release performs final physical reclaim");
    }

    private static void HandlerFaultRetiresOnlyThatActor()
    {
        using var runtime = new ActorRuntime(Options());
        using var healthyDone = new ManualResetEventSlim();

        var failed = runtime.Spawn(ActorKind.Shared, static (_, _) =>
            throw new InvalidOperationException("synthetic actor failure"));
        var healthy = runtime.Spawn(ActorKind.Shared, (_, _) => healthyDone.Set());

        runtime.Send(failed, new byte[] { 1 });
        runtime.Send(healthy, new byte[] { 2 });

        Require(healthyDone.Wait(TimeSpan.FromSeconds(5)),
            "sibling actor survives another actor's handler fault");
        Require(WaitUntil(() => runtime.FaultedTurns == 1),
            "faulted actor turn recorded");
        Require(WaitUntil(() => runtime.LiveActors == 1),
            "faulted actor is retired without killing sibling");

        runtime.Stop(healthy);
        Require(WaitUntil(() => runtime.LiveActors == 0),
            "healthy sibling remains independently stoppable");
    }

    private static bool WaitUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor runtime regression: " + scenario);
    }
}
