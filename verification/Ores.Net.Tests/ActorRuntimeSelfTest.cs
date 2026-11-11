using Ores.Net.Runtime;
using System.Reflection;

internal static class ActorRuntimeSelfTest
{
    public static void Run()
    {
        ThreeKindsUseAssignedDomainsAndActorArenas();
        UnknownActorKindFailsBeforeNativeAllocation();
        RejectedMessagesDoNotStartCarriers();
        ForgedSameIdCannotObserveTermination();
        OneActorNeverRunsTwoHandlersConcurrently();
        DeliveredMessageStorageIsTurnBound();
        PersistentShardSurvivesAcrossActorTurns();
        RetainedContextCannotUseExpiredTurn();
        LifetimePinCanOutliveActorTurnAndStop();
        HandlerFaultRetiresOnlyThatActor();
        ConcurrentDisposersAwaitCarrierQuiescence();
        FailedTurnAdmissionScrubsDequeuedMessage();
        ActorToActorRelayIsFifoAndCopyIsolated();
        TerminalStopDoesNotRequireMailboxDataCredit();
        HostIoTurnsAreDirectionalAndCopyIsolated();
        HostIoPendingDataScrubbedOnlyAfterPhysicalQuiescence();
        HostIoActorFaultRetiresOutput();
        HostIoGlobalBudgetSurvivesPressureAndStop();
        Console.WriteLine("ACTOR_IO_GLOBAL_BUDGET_PASS");
        Console.WriteLine("ACTOR_PIPELINE_MAILBOX_CONTROL_PASS");
        Console.WriteLine("ACTOR_IO_HOST_INTEGRATION_PASS");
        Console.WriteLine(
            "PASS .NET integrated actor runtime mailbox, arena, domains, stop and fault isolation");
    }

    // Real actor runtime paths: one actor handler sends to a second mailbox.
    // This is a host-runtime integration test, not a source-language guest proof.
    private static void ActorToActorRelayIsFifoAndCopyIsolated()
    {
        using var runtime = new ActorRuntime(Options());
        const int count = 48;
        using var delivered = new CountdownEvent(count);
        var observed = new System.Collections.Concurrent.ConcurrentQueue<byte>();

        var sink = runtime.Spawn(ActorKind.Private, (_, mail) =>
        {
            Require(mail.Length == 1, "relay frame remains exactly one byte");
            observed.Enqueue(mail.Span[0]);
            delivered.Signal();
        });
        var relay = runtime.Spawn(ActorKind.Shared, (_, mail) =>
            runtime.Send(sink, mail.Span));

        byte[] source = { 0 };
        for (int i = 0; i < count; i++)
        {
            source[0] = (byte)i;
            runtime.Send(relay, source);
            source[0] = 255; // caller buffer is never published by reference
        }
        Require(delivered.Wait(TimeSpan.FromSeconds(10)),
            "actor-to-actor relay drained all frames");
        Require(observed.SequenceEqual(Enumerable.Range(0, count).Select(i => (byte)i)),
            "actor-to-actor transport preserved FIFO and isolated sender mutation");
        Require(WaitUntil(() => runtime.CompletedMessages == 2 * count),
            "both relay and sink recorded each completed turn");

        runtime.Stop(relay);
        runtime.Stop(sink);
        Require(WaitUntil(() => runtime.LiveActors == 0 &&
            runtime.NativeReservedBytes == 0),
            "both pipeline actors physically retired");
    }

    // The trusted STOP path must not be a user-DATA message. In particular,
    // a saturated bounded mailbox cannot prevent cancellation/retirement.
    private static void TerminalStopDoesNotRequireMailboxDataCredit()
    {
        using var runtime = new ActorRuntime(Options());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int handled = 0;
        var actor = runtime.Spawn(ActorKind.Private, (_, _) =>
        {
            Interlocked.Increment(ref handled);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new Exception("CONTROL separation test timed out");
        });

        runtime.Send(actor, new byte[] { 1 });
        Require(entered.Wait(TimeSpan.FromSeconds(5)),
            "actor entered a live turn before DATA saturation");
        try
        {
            for (int i = 0; i < Options().MailboxMessages; i++)
                runtime.Send(actor, new byte[] { 2 });

            bool rejected = false;
            try { runtime.Send(actor, new byte[] { 3 }); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "saturated DATA mailbox must reject additional DATA");
            var stop = Task.Run(() => runtime.Stop(actor));
            Require(stop.Wait(TimeSpan.FromSeconds(5)),
                "trusted STOP cannot wait for ordinary DATA queue credit");
            rejected = false;
            try { runtime.Send(actor, new byte[] { 4 }); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "STOP fences further DATA admission");
        }
        finally { release.Set(); }

        Require(WaitUntil(() => runtime.LiveActors == 0 &&
            runtime.NativeReservedBytes == 0),
            "terminal STOP physically retired saturated actor");
        Require(handled == 1,
            "queued DATA never ran after terminal CONTROL STOP");
    }

    private static void HostIoTurnsAreDirectionalAndCopyIsolated()
    {
        using var runtime = new ActorRuntime(Options());
        using var handled = new ManualResetEventSlim();
        ActorContext? retained = null;
        var (actor, hostIo) = runtime.SpawnWithIo(ActorKind.Shared, (context, _) =>
        {
            retained = context;
            Require(context.TryReadInput(out byte[]? input) && input is not null,
                "live turn must read host-fed input");
            Require(input.SequenceEqual(new byte[] { 13, 21, 34 }),
                "host-to-actor channel must isolate sender mutations");
            Require(context.InputEof, "io.in EOF follows final queued record");
            Require(context.TryWriteOutput(input),
                "live turn must publish bounded output");
            context.CompleteOutput();
            Require(!context.TryWriteOutput(new byte[] { 9 }),
                "actor cannot write io.out after actor-owned EOF");
            handled.Set();
        });

        // The host port provides *only* input admission and output draining.
        // It must never expose the actor's input reader or output writer.
        var publicApi = typeof(ActorIoHostPort).GetMethods(
            BindingFlags.Instance | BindingFlags.Public).Select(m => m.Name).ToArray();
        Require(!publicApi.Contains("TryReadInput") &&
            !publicApi.Contains("TryWriteOutput"),
            "host port must not widen into actor-side I/O authority");

        var inputSource = new byte[] { 13, 21, 34 };
        Require(hostIo.TryFeedInput(inputSource), "host feed input");
        hostIo.CompleteInput();
        hostIo.CompleteInput();
        Require(!hostIo.TryFeedInput(new byte[] { 66 }),
            "host cannot write io.in after host-owned EOF");
        inputSource[0] = 255;
        // Until a dedicated I/O readiness scheduler token exists, explicit
        // ordinary actor wakeup is required. Never label this native I/O AOT.
        runtime.Send(actor, ReadOnlySpan<byte>.Empty);
        Require(handled.Wait(TimeSpan.FromSeconds(5)), "actor turn consumed io.in");
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "I/O actor turn completed");

        Require(!hostIo.OutputEof, "output EOF must wait for pending record");
        Require(hostIo.TryReadOutput(out var output) && output is not null &&
            output.SequenceEqual(new byte[] { 13, 21, 34 }),
            "host drain io.out returns exact owned payload");
        Require(hostIo.OutputEof, "host observes EOF after final output drain");
        bool expired = false;
        try { retained!.TryWriteOutput(new byte[] { 99 }); }
        catch (InvalidOperationException) { expired = true; }
        Require(expired, "retained ActorContext cannot write io.out outside turn");

        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0),
            "I/O actor physically stopped");
        bool retired = false;
        try { hostIo.TryFeedInput(new byte[] { 1 }); }
        catch (ObjectDisposedException) { retired = true; }
        Require(retired, "terminal actor STOP must retire host I/O port");
    }

    // Inspect transport-owned arrays: clearing queues without scrubbing
    // would not pass, and scrubbing before a live turn quiesces is forbidden.
    private static byte[] PeekHostIoOwnedBytes(ActorIoHostPort host, string queueField)
    {
        var ioField = typeof(ActorIoHostPort).GetField("_io",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("missing host I/O transport field");
        var io = (ActorIoEnvelope)(ioField.GetValue(host)
            ?? throw new Exception("missing host I/O envelope"));
        var queue = typeof(ActorIoEnvelope).GetField(queueField,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("missing I/O owned queue " + queueField);
        return ((Queue<byte[]>)queue.GetValue(io)!).Peek();
    }

    private static void HostIoPendingDataScrubbedOnlyAfterPhysicalQuiescence()
    {
        using var runtime = new ActorRuntime(Options());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var (actor, hostIo) = runtime.SpawnWithIo(ActorKind.Private, (ctx, _) =>
        {
            Require(ctx.TryWriteOutput(new byte[] { 77, 88, 99 }),
                "turn writes output before its STOP");
            entered.Set();
            Require(release.Wait(TimeSpan.FromSeconds(10)),
                "test release carrier after STOP");
        });

        runtime.Send(actor, ReadOnlySpan<byte>.Empty);
        Require(entered.Wait(TimeSpan.FromSeconds(5)), "actor entered I/O turn");
        try
        {
            Require(hostIo.TryFeedInput(new byte[] { 41, 42 }),
                "host posted independently bounded input");
            byte[] ownedInput = PeekHostIoOwnedBytes(hostIo, "_input");
            byte[] ownedOutput = PeekHostIoOwnedBytes(hostIo, "_output");
            runtime.Stop(actor);
            Require(ownedInput[0] == 41 && ownedOutput[0] == 77,
                "pending owned buffers cannot be reclaimed until physical turn exit");
            Require(runtime.LiveActors == 1,
                "stop cannot claim completed physical retirement before quiescence");
            bool stale = false;
            try { hostIo.TryFeedInput(new byte[] { 1 }); }
            catch (InvalidOperationException) { stale = true; }
            Require(stale, "STOP must immediately fence further I/O DATA");
            release.Set();
            Require(WaitUntil(() => runtime.LiveActors == 0 &&
                runtime.NativeReservedBytes == 0),
                "STOP finalizes after actor turn exit");
            Require(ownedInput.All(value => value == 0) &&
                ownedOutput.All(value => value == 0),
                "physical finalization scrubs queued input and output");
        }
        finally { release.Set(); }
    }

    private static void HostIoActorFaultRetiresOutput()
    {
        using var runtime = new ActorRuntime(Options());
        var (actor, hostIo) = runtime.SpawnWithIo(ActorKind.Shared, (ctx, _) =>
        {
            Require(ctx.TryWriteOutput(new byte[] { 12, 13 }),
                "faulting turn publishes output");
            throw new InvalidOperationException("expected actor-local fault");
        });
        runtime.Send(actor, ReadOnlySpan<byte>.Empty);
        Require(WaitUntil(() => runtime.FaultedTurns == 1 &&
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
            "actor fault must retire only its own I/O resources");
        bool retired = false;
        try { hostIo.TryReadOutput(out _); }
        catch (ObjectDisposedException) { retired = true; }
        Require(retired, "fault-retired host output must not remain readable");
    }

    private static void HostIoGlobalBudgetSurvivesPressureAndStop()
    {
        // Two different actor I/O envelopes share one runtime-level credit
        // window, including zero-byte records. The native arena budget does
        // not account for CLR-managed I/O queues.
        using var runtime = new ActorRuntime(Options(ioBytes: 4, ioRecords: 2));
        using var processed = new ManualResetEventSlim();
        var (first, firstIo) = runtime.SpawnWithIo(ActorKind.Shared, (ctx, _) =>
        {
            Require(ctx.TryReadInput(out var data) && data is not null &&
                data.SequenceEqual(new byte[] { 1, 2 }),
                "first actor drains its own input");
            processed.Set();
        });
        var (second, secondIo) = runtime.SpawnWithIo(
            ActorKind.Private, static (_, _) => { });

        Require(firstIo.TryFeedInput(new byte[] { 1, 2 }),
            "first actor reserves two global bytes");
        Require(secondIo.TryFeedInput(new byte[] { 3, 4 }),
            "second actor reserves remaining two global bytes");
        Require(runtime.IoQueuedBytes == 4 && runtime.IoQueuedRecords == 2,
            "global I/O credit telemetry sees both actors");
        Require(!firstIo.TryFeedInput(Array.Empty<byte>()),
            "zero-byte frame cannot evade shared record credit");
        Require(!secondIo.TryFeedInput(new byte[] { 5 }),
            "global byte/record backpressure rejects cross-actor payload");
        Require(runtime.IoQueuedBytes == 4 && runtime.IoQueuedRecords == 2,
            "rejected queue admission leaves accounting unchanged");

        runtime.Send(first, ReadOnlySpan<byte>.Empty);
        Require(processed.Wait(TimeSpan.FromSeconds(5)),
            "first actor serviced its queue");
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "first actor completed before credit observation");
        Require(runtime.IoQueuedBytes == 2 && runtime.IoQueuedRecords == 1,
            "draining one actor refunds only that actor's reserved credits");

        Require(firstIo.TryFeedInput(new byte[] { 6, 7 }),
            "refunded window admits another actor payload");
        Require(!secondIo.TryFeedInput(Array.Empty<byte>()),
            "record window still bounded even when bytes fit");

        runtime.Stop(first);
        Require(WaitUntil(() => runtime.LiveActors == 1),
            "stopped actor retires only its own I/O queue");
        Require(runtime.IoQueuedBytes == 2 && runtime.IoQueuedRecords == 1,
            "first actor STOP cannot refund another actor's queued DATA");
        Require(secondIo.TryFeedInput(new byte[] { 8, 9 }),
            "credit returned by retirement can be reused independently");
        Require(runtime.IoQueuedBytes == 4 && runtime.IoQueuedRecords == 2,
            "credits remain bounded after retirement and reuse");

        runtime.Stop(second);
        Require(WaitUntil(() => runtime.LiveActors == 0 &&
            runtime.NativeReservedBytes == 0),
            "both I/O actor cells physically retired");
        Require(runtime.IoQueuedBytes == 0 && runtime.IoQueuedRecords == 0,
            "terminal STOP refunds every remaining global I/O record and byte");

        bool invalidLimitsRejected = false;
        try { using var _ = new ActorRuntime(Options(ioBytes: 0)); }
        catch (ArgumentOutOfRangeException) { invalidLimitsRejected = true; }
        Require(invalidLimitsRejected, "zero global byte cap must reject at admission");
        invalidLimitsRejected = false;
        try { using var _ = new ActorRuntime(Options(ioRecords: 0)); }
        catch (ArgumentOutOfRangeException) { invalidLimitsRejected = true; }
        Require(invalidLimitsRejected, "zero global record cap must reject at admission");
    }

    private static void ForgedSameIdCannotObserveTermination()
    {
        using var runtime = new ActorRuntime(Options());
        var real = runtime.Spawn(ActorKind.Shared, static (_, _) => { });
        var forged = new ActorMailbox(real.Id).CurrentRef;
        Require(forged.Id == real.Id && forged.Generation == real.Generation &&
                forged != real,
            "test must forge the same numeric actor identity with a different nonce");

        // Internal observation is reached reflectively in this host-only test;
        // production need not expose an unauthenticated public observation API.
        MethodInfo? observer = typeof(ActorRuntime).GetMethod(
            "ObserveTermination", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(observer is not null, "termination observer entrypoint must exist");
        bool denied = false;
        try
        {
            _ = observer!.Invoke(runtime, new object[] { forged });
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is InvalidOperationException)
        {
            denied = true;
        }
        Require(denied, "a forged ActorRef must not observe another actor's termination");
        Require(runtime.LiveActors == 1,
            "denied observation must not affect the original actor's lifecycle");

        Task legitimate = (Task)(observer!.Invoke(runtime, new object[] { real })
            ?? throw new InvalidOperationException("missing legitimate termination task"));
        runtime.Stop(real);
        Require(legitimate.Wait(TimeSpan.FromSeconds(5)),
            "runtime-minted ActorRef must continue to observe termination");
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

    private static ActorRuntimeOptions Options(
        int ioBytes = 64 * 1024 * 1024, int ioRecords = 65_536) => new()
    {
        MaxIoQueuedBytes = ioBytes,
        MaxIoQueuedRecords = ioRecords,
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

