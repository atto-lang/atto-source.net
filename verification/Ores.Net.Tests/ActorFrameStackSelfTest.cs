using Ores.Net.Runtime;

internal static class ActorFrameStackSelfTest
{
    public static void Run()
    {
        InvalidConfigurationFailsBeforeCarrierAdmission();
        IndependentStacksSurviveTurnsAndRejectForeignAndStaleHandles();
        BoundsAndLifoExhaustionAreFailureNeutral();
        RetirementAllowsInFlightFrameCleanupButRejectsPush();
        Console.WriteLine(
            "PASS .NET actor-owned logical frame stack host-only regression (not guest/AOT certification)");
    }

    private static ActorRuntimeOptions Options(int stackBytes = 2048,
        int maxFrames = 4) => new()
    {
        MaxActors = 8,
        MessagesPerDispatch = 8,
        ActorPersistentBytes = 8192,
        ActorScratchBytes = 512,
        ActorStackBytes = stackBytes,
        ActorMaxStackFrames = maxFrames,
        NativeBudgetBytes = 256 * 1024,
        Dispatcher = new ActorDispatcherOptions
        {
            PrivateWorkers = 2,
            SharedWorkers = 2,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 16
        }
    };

    private static void InvalidConfigurationFailsBeforeCarrierAdmission()
    {
        var tooLarge = Options(stackBytes: 8193);
        RejectRange(() => { using var _ = new ActorRuntime(tooLarge); },
            "actor stack cannot exceed persistent native capacity");
        var tooManyFrames = Options(maxFrames: 1025);
        RejectRange(() => { using var _ = new ActorRuntime(tooManyFrames); },
            "actor stack frame count has a hard upper bound");
    }

    private static void IndependentStacksSurviveTurnsAndRejectForeignAndStaleHandles()
    {
        using var runtime = new ActorRuntime(Options());
        using var finished = new CountdownEvent(2);
        ActorStackFrameHandle? saved = null;
        ActorContext? previousTurn = null;

        var first = runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            if (message.Span[0] == 1)
            {
                saved = context.PushStackFrame(16, alignment: 16);
                context.WriteStackFrame(saved, 0, new byte[] { 0xA1, 0xA2 });
                previousTurn = context;
                return;
            }

            Span<byte> original = stackalloc byte[2];
            context.ReadStackFrame(saved!, 0, original);
            Require(original.SequenceEqual(new byte[] { 0xA1, 0xA2 }),
                "frame bytes persist across physical turns");
            bool offCarrierDenied = Task.Run(() =>
            {
                try
                {
                    context.ReadStackFrame(saved!, 0, new byte[1]);
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }).GetAwaiter().GetResult();
            Require(offCarrierDenied,
                "another thread cannot read actor frame metadata or payload");

            var child = context.PushStackFrame(16, alignment: 16);
            Span<byte> zero = stackalloc byte[2];
            context.ReadStackFrame(child, 0, zero);
            Require(zero.SequenceEqual(new byte[] { 0, 0 }),
                "new child frame starts zeroed");

            RejectInvalid(() => context.PopStackFrame(saved!),
                "caller cannot pop a parent beneath a live child");
            context.WriteStackFrame(child, 0, new byte[] { 0xCC, 0xDD });
            context.PopStackFrame(child);
            RejectInvalid(() =>
                context.ReadStackFrame(child, 0, new byte[1]),
                "popped frame token must be invalid");
            var reused = context.PushStackFrame(16, alignment: 16);
            context.ReadStackFrame(reused, 0, zero);
            Require(zero.SequenceEqual(new byte[] { 0, 0 }),
                "popped child bytes are scrubbed before offset reuse");
            context.PopStackFrame(reused);
            context.PopStackFrame(saved!);
            RejectInvalid(() => context.PopStackFrame(saved!),
                "double pop cannot reclaim another allocation");
            finished.Signal();
        });

        var second = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            RejectInvalid(() =>
                context.ReadStackFrame(saved!, 0, new byte[2]),
                "another actor cannot access an issued logical frame");
            var own = context.PushStackFrame(16);
            context.WriteStackFrame(own, 0, new byte[] { 0xBB });
            Span<byte> roundTrip = stackalloc byte[1];
            context.ReadStackFrame(own, 0, roundTrip);
            Require(roundTrip[0] == 0xBB,
                "second actor has independent native frame storage");
            context.PopStackFrame(own);
            finished.Signal();
        });

        Require(runtime.NativeReservedBytes == 2L * (8192 + 512),
            "actor logical stacks are charged within existing arena budgets");
        runtime.Send(first, new byte[] { 1 });
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "first physical turn has completed");
        RejectInvalid(() =>
            previousTurn!.ReadStackFrame(saved!, 0, new byte[2]),
            "retained prior-turn context cannot dereference continuation frame");
        RejectNoWire(() =>
        {
            _ = System.Text.Json.JsonSerializer.Serialize(saved!);
        }, "actor frame authority cannot be serialized as JSON");
        RejectNoWire(() =>
        {
            _ = System.Text.Json.JsonSerializer.Deserialize<ActorStackFrameHandle>(
                "{\"Length\":16}");
        }, "actor frame authority cannot be deserialized from JSON");

        runtime.Send(second, new byte[] { 1 });
        runtime.Send(first, new byte[] { 2 });
        Require(finished.Wait(TimeSpan.FromSeconds(5)),
            "both actors complete independent stack checks");
        Require(WaitUntil(() => runtime.CompletedMessages == 3),
            "all three host turns completed without faults");
        Require(runtime.FaultedTurns == 0, "stack probes do not fault actors");

        runtime.Stop(first);
        runtime.Stop(second);
        Require(WaitUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
            "actor finalization scrubs and refunds independent arena stacks");
    }

    private static void BoundsAndLifoExhaustionAreFailureNeutral()
    {
        using var runtime = new ActorRuntime(Options(stackBytes: 128, maxFrames: 2));
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            var root = context.PushStackFrame(8);
            var child = context.PushStackFrame(8);
            RejectInvalid(() => context.PushStackFrame(8),
                "frame count exhaustion denies third frame");
            RejectRange(() => context.PushStackFrame(129),
                "stack byte budget cannot be bypassed");
            RejectRange(() =>
                context.WriteStackFrame(child, 7, new byte[] { 1, 2 }),
                "frame write cannot cross its issued length");
            RejectInvalid(() => context.PopStackFrame(root),
                "non-LIFO pop leaves both frames live");

            context.PopStackFrame(child);
            var fullTail = context.PushStackFrame(120);
            Span<byte> sample = stackalloc byte[2];
            context.ReadStackFrame(fullTail, 0, sample);
            Require(sample.SequenceEqual(new byte[] { 0, 0 }),
                "failed admission did not corrupt the frame stack");
            context.PopStackFrame(fullTail);
            context.PopStackFrame(root);
            var recycled = context.PushStackFrame(128, alignment: 64);
            context.ReadStackFrame(recycled, 0, sample);
            Require(sample.SequenceEqual(new byte[] { 0, 0 }),
                "last-pop scrubbing and stack reuse erase the next generation");
            context.PopStackFrame(recycled);
            RejectInvalid(() =>
                context.ReadStackFrame(root, 0, new byte[1]),
                "reused native offset does not revive a stale frame handle");
        });

        runtime.Send(actor, new byte[] { 1 });
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "bounded logical-frame handler completed");
        Require(runtime.FaultedTurns == 0,
            "invalid frame operations are rejected without corrupting owner");
        runtime.Stop(actor);
        Require(WaitUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
            "bounded logical stack retires without native memory leak");
    }

    private static void RetirementAllowsInFlightFrameCleanupButRejectsPush()
    {
        using var runtime = new ActorRuntime(Options());
        using var entered = new ManualResetEventSlim();
        using var leave = new ManualResetEventSlim();
        bool poppedDuringRetirement = false;
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            var root = context.PushStackFrame(16);
            context.WriteStackFrame(root, 0, new byte[] { 0xE7 });
            entered.Set();
            if (!leave.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Retirement frame probe timed out");
            RejectInvalid(() => context.PushStackFrame(8),
                "retired actor must not admit a new frame");
            context.PopStackFrame(root);
            poppedDuringRetirement = true;
        });
        runtime.Send(actor, new byte[] { 1 });
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)),
                "actor entered a live physical turn before retirement");
            runtime.Stop(actor);
        }
        finally
        {
            leave.Set();
        }
        Require(WaitUntil(() => runtime.CompletedMessages == 1 &&
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
            "actor stops after permitted stack unwind and full native reclaim");
        Require(poppedDuringRetirement && runtime.FaultedTurns == 0,
            "in-flight frame cleanup must remain legal after logical retirement");
    }

    private static bool WaitUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    private static void RejectInvalid(Action action, string scenario)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected actor frame rejection: " + scenario);
    }

    private static void RejectNoWire(Action action, string scenario)
    {
        try { action(); }
        catch (NotSupportedException) { return; }
        throw new Exception("Expected actor frame JSON denial: " + scenario);
    }

    private static void RejectRange(Action action, string scenario)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected actor frame bounds rejection: " + scenario);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor logical frame stack regression: " + scenario);
    }
}
