using Ores.Net.Runtime;
using System.Reflection;

internal static class ActorRuntimeSelfTest
{
    public static void Run()
    {
        ThreeKindsUseAssignedDomainsAndActorArenas();
        UnknownActorKindFailsBeforeNativeAllocation();
        RejectedMessagesDoNotStartCarriers();
        OneActorNeverRunsTwoHandlersConcurrently();
        DeliveredMessageStorageIsTurnBound();
        PersistentShardSurvivesAcrossActorTurns();
        RetainedContextCannotUseExpiredTurn();
        LifetimePinCanOutliveActorTurnAndStop();
        HandlerFaultRetiresOnlyThatActor();
        ActorOriginCannotDisposeOwningRuntime();
        ConcurrentDisposersAwaitCarrierQuiescence();
        FailedTurnAdmissionScrubsDequeuedMessage();
        Console.WriteLine(
            "PASS .NET integrated actor runtime mailbox, arena, domains, stop and fault isolation");
    }

    private static void UnknownActorKindFailsBeforeNativeAllocation()
    {
        using var runtime = new ActorRuntime(Options());
        bool rejected = false;
        try
        {
            runtime.Spawn((ActorKind)int.MaxValue, static (_, _) => { });
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }

        Require(rejected, "unknown actor kind must be rejected at spawn admission");
        Require(runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0 &&
                runtime.StartedCarrierThreads == 0,
            "invalid kind must not consume actor, native-memory or carrier capacity");
    }

    private static void RejectedMessagesDoNotStartCarriers()
    {
        using var runtime = new ActorRuntime(Options());
        var actor = runtime.Spawn(ActorKind.Shared, static (_, _) => { });
        Require(runtime.StartedCarrierThreads == 0,
            "actor spawn leaves carrier threads unstarted");

        bool rejected = false;
        try
        {
            runtime.Send(actor, new byte[ActorMailbox.MaxMessageBytes + 1]);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        Require(rejected, "oversized message is rejected");
        Require(runtime.StartedCarrierThreads == 0 &&
                runtime.CompletedMessages == 0 && runtime.LiveActors == 1,
            "invalid payload does not consume carrier capacity or actor state");

        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0 &&
                 runtime.NativeReservedBytes == 0),
            "rejected-message actor stops without native-memory leak");
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

    private static void PersistentShardSurvivesAcrossActorTurns()
    {
        using var runtime = new ActorRuntime(Options());
        using var done = new ManualResetEventSlim();
        ActorShardColumns? shard = null;
        double before = double.NaN;
        double after = double.NaN;

        var actor = runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            if (message.Span[0] == 1)
            {
                var created = context.CreateShard(
                    rows: 4, fields: 2, layout: ActorDataLayout.Soa);
                shard = created;
                for (int row = 0; row < 4; row++)
                {
                    context.WriteShard(created, row, 0, row * 10.0);
                    context.WriteShard(created, row, 1, row + 0.5);
                }
                return;
            }

            Require(shard is not null,
                "persistent shard from prior turn is available");
            before = context.ReadShard(shard!, 2, 0);
            context.IntegrateShard(
                shard!, positionField: 0, velocityField: 1, dt: 0.5);
            after = context.ReadShard(shard!, 2, 0);
            context.ReleaseShard(shard!);
            done.Set();
        });

        runtime.Send(actor, new byte[] { 1 });
        runtime.Send(actor, new byte[] { 2 });

        Require(done.Wait(TimeSpan.FromSeconds(5)),
            "persistent shard is usable on a later actor turn");
        Require(WaitUntil(() => runtime.CompletedMessages == 2),
            "cross-turn shard messages complete");
        Require(before == 20.0 && after == 21.25,
            "SoA shard preserves state and dense-kernel semantics across turns");

        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0),
            "cross-turn shard actor finalizes");
    }

    private static void RetainedContextCannotUseExpiredTurn()
    {
        using var runtime = new ActorRuntime(Options());
        using var captured = new ManualResetEventSlim();
        ActorContext? retained = null;

        var actor = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            retained = context;
            captured.Set();
        });

        runtime.Send(actor, new byte[] { 1 });
        Require(captured.Wait(TimeSpan.FromSeconds(5)),
            "actor context captured during turn");
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "captured actor turn completed");

        bool rejected = false;
        try
        {
            _ = retained!.AllocateScratch(8);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Require(rejected,
            "retained ActorContext cannot reacquire arena authority after turn exit");

        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0),
            "expired-context actor finalizes");
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


    private static void FailedTurnAdmissionScrubsDequeuedMessage()
    {
        using var runtime = new ActorRuntime(Options());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var actor = runtime.Spawn(ActorKind.Shared, (context, message) =>
        {
            if (message.Span[0] != 1)
                throw new Exception("Second message must fail turn admission");

            // Exercise the exception path without introducing a production
            // fault-injection API: retire the exposed-to-this-test host arena
            // while its first turn remains active.
            var field = typeof(ActorContext).GetField(
                "_arena", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(context) is not ActorArena arena)
                throw new Exception("Could not locate actor arena in test");
            Require(!arena.Retire(), "active turn delays forced arena reclaim");
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new Exception("Failed-turn test timed out");
        });

        runtime.Send(actor, new byte[] { 1 });
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)),
                "first handler retired arena while retaining its turn");
            runtime.Send(actor, new byte[] { 2, 0xC1, 0xC2, 0xC3 });

            // Capture the mailbox-owned second envelope, not the caller's
            // input. It must be scrubbed even when EnterTurn rejects it.
            var actorsField = typeof(ActorRuntime).GetField(
                "_actors", BindingFlags.Instance | BindingFlags.NonPublic);
            if (actorsField?.GetValue(runtime) is not System.Collections.IDictionary actors ||
                actors[actor.Id] is not object cell)
                throw new Exception("Could not locate actor cell in test");
            var mailboxField = cell.GetType().GetField(
                "<Mailbox>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (mailboxField?.GetValue(cell) is not ActorMailbox mailbox)
                throw new Exception("Could not locate test mailbox");

            var messagesField = typeof(ActorMailbox).GetField(
                "_messages", BindingFlags.Instance | BindingFlags.NonPublic);
            if (messagesField?.GetValue(mailbox) is not Queue<byte[]> messages ||
                messages.Count != 1)
                throw new Exception("Could not capture pending envelope");
            byte[] ownedEnvelope = messages.Peek();
            release.Set();
            Require(WaitUntil(() => runtime.LiveActors == 0),
                "turn-admission failure must retire its actor");
            Require(runtime.CompletedMessages == 1 && runtime.FaultedTurns == 1,
                "failed turn admission accounts for one successful and one failed message");
            Require(ownedEnvelope.AsSpan().IndexOfAnyExcept((byte)0) < 0,
                "dequeued message must be erased after failed turn admission");
            Require(runtime.NativeReservedBytes == 0,
                "failed turn admission eventually refunds actor native capacity");
        }
        finally
        {
            release.Set();
        }
    }

    // A child task inherits actor-origin provenance even when it executes
    // after the physical dispatch turn has completed. It must not be able to
    // shut down the runtime whose actor created it. Host-origin concurrent
    // disposal remains independently covered below.
    private static void ActorOriginCannotDisposeOwningRuntime()
    {
        using var runtime = new ActorRuntime(Options());
        var carrierResult = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var childResult = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = runtime.Spawn(ActorKind.Shared, (_, _) =>
        {
            bool carrierDenied = false;
            try { runtime.Dispose(); }
            catch (InvalidOperationException error) when (
                error.Message.Contains(
                    "ActorRuntime.Dispose cannot run from one of its actor carriers",
                    StringComparison.Ordinal))
            {
                carrierDenied = true;
            }
            carrierResult.TrySetResult(carrierDenied);
            _ = Task.Run(async () =>
            {
                await releaseChild.Task.ConfigureAwait(false);
                bool childDenied = false;
                try { runtime.Dispose(); }
                catch (InvalidOperationException error) when (
                    error.Message.Contains("ORES-ACTOR-DISPOSE-ORIGIN-DENIED",
                        StringComparison.Ordinal))
                {
                    childDenied = true;
                }
                childResult.TrySetResult(childDenied);
            });
        });

        runtime.Send(actor, new byte[] { 1 });
        Require(carrierResult.Task.Wait(TimeSpan.FromSeconds(5)) &&
                carrierResult.Task.Result,
            "physical actor carrier must not dispose its owning runtime");
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "actor handler did not complete before delayed child disposal attempt");
        releaseChild.TrySetResult(true);
        Require(childResult.Task.Wait(TimeSpan.FromSeconds(5)) &&
                childResult.Task.Result,
            "post-turn actor-origin Task.Run disposed its owning runtime");
        Require(runtime.LiveActors == 1 && runtime.FaultedTurns == 0,
            "rejected actor-origin disposal stopped or faulted healthy actor");
        runtime.Stop(actor);
    }

    private static void ConcurrentDisposersAwaitCarrierQuiescence()
    {
        var runtime = new ActorRuntime(Options());
        using var inHandler = new ManualResetEventSlim();
        using var allowReturn = new ManualResetEventSlim();
        using var startedFirst = new ManualResetEventSlim();
        using var startedSecond = new ManualResetEventSlim();

        var actor = runtime.Spawn(ActorKind.Private, (_, _) =>
        {
            inHandler.Set();
            if (!allowReturn.Wait(TimeSpan.FromSeconds(10)))
                throw new Exception("disposal regression handler timed out");
        });

        runtime.Send(actor, new byte[] { 7 });
        Task? first = null;
        Task? second = null;
        try
        {
            Require(inHandler.Wait(TimeSpan.FromSeconds(5)),
                "long-running handler entered before concurrent dispose");

            first = Task.Run(() =>
            {
                startedFirst.Set();
                runtime.Dispose();
            });
            second = Task.Run(() =>
            {
                startedSecond.Set();
                runtime.Dispose();
            });

            Require(startedFirst.Wait(TimeSpan.FromSeconds(5)) &&
                    startedSecond.Wait(TimeSpan.FromSeconds(5)),
                "both disposal calls started");
            Require(!Task.WhenAny(first!, second!).Wait(TimeSpan.FromMilliseconds(150)),
                "neither disposer may return while its actor handler is active");
        }
        finally
        {
            allowReturn.Set();
            if (first is not null && second is not null)
                Task.WaitAll(first, second);
            else
                runtime.Dispose();
        }

        Require(runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            "both disposal callers observed complete native reclamation");
    }

    private static bool WaitUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor runtime regression: " + scenario);
    }
}

