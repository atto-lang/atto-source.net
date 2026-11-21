using System.Linq;
using Ores.Net.Runtime;

internal static class ActorFrameStackSelfTest
{
    public static void Run()
    {
        InvalidConfigurationFailsBeforeCarrierAdmission();
        IndependentInvocationStacksRejectForeignAndStaleHandles();
        InvalidArgumentsAndLifoDenialsAreFailureNeutral();
        RepeatedAlignmentAndReissuedFrameCapabilities();
        TerminalQuotaFaultsAndSiblingSurvival();
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

    private static void IndependentInvocationStacksRejectForeignAndStaleHandles()
    {
        using var runtime = new ActorRuntime(Options());
        using var finished = new CountdownEvent(2);
        ActorStackFrameHandle? saved = null;
        ActorContext? previousTurn = null;
        ActorArenaHandle? persistent = null;

        var first = runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            if (message.Span[0] == 1)
            {
                saved = context.PushStackFrame(16, alignment: 16);
                context.WriteStackFrame(saved, 0, new byte[] { 0xA1, 0xA2 });
                previousTurn = context;
                persistent = context.AllocatePersistent(2);
                context.Write(persistent, 0, new byte[] { 0xA1, 0xA2 });
                return;
            }

            Span<byte> original = stackalloc byte[2];
            RejectInvalid(() => context.ReadStackFrame(saved!, 0, new byte[2]),
                "a new receive cannot acquire an earlier invocation stack");
            Require(!ReferenceEquals(context.Execution, previousTurn!.Execution) &&
                !ReferenceEquals(context.Execution.Stack, previousTurn.Execution.Stack) &&
                context.Execution.ExecutionSequence > previousTurn.Execution.ExecutionSequence &&
                context.Execution.Actor == context.Self, "receive has fresh actor-bound execution identity");
            context.Read(persistent!, 0, original);
            Require(original.SequenceEqual(new byte[] { 0xA1, 0xA2 }),
                "persistent heap survives independently of receive stacks");
            saved = context.PushStackFrame(16, alignment: 16);
            context.ReadStackFrame(saved, 0, original);
            Require(original.SequenceEqual(new byte[] { 0, 0 }), "next invocation stack is scrubbed");
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
        RejectNoWire(() =>
        {
            _ = System.Text.Json.JsonSerializer.Serialize<object>(saved!);
        }, "object-erased frame capability must not escape the JSON boundary");
        RejectNoWire(() =>
        {
            _ = System.Text.Json.JsonSerializer.Serialize(new[] { saved! });
        }, "frame capability nested in a collection must not escape JSON");
        RejectNoWire(() =>
        {
            _ = System.Text.Json.JsonSerializer.Serialize(previousTurn!.Execution);
        }, "actor execution authority cannot be serialized as JSON");
        RejectNoWire(() =>
        {
            _ = System.Text.Json.JsonSerializer.Deserialize<ActorExecutionContext>("{}");
        }, "actor execution authority cannot be reconstructed from JSON");

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

    private static void InvalidArgumentsAndLifoDenialsAreFailureNeutral()
    {
        using var runtime = new ActorRuntime(Options(stackBytes: 128, maxFrames: 2));
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            var root = context.PushStackFrame(8);
            var child = context.PushStackFrame(8);
            RejectRange(() => context.PushStackFrame(0),
                "malformed zero-length frame is rejected without poisoning");
            RejectRange(() => context.PushStackFrame(8, alignment: 3),
                "invalid non-power-of-two alignment is nonterminal");
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

    private static void RepeatedAlignmentAndReissuedFrameCapabilities()
    {
        using var runtime = new ActorRuntime(Options(stackBytes: 256, maxFrames: 3));
        int verified = 0;
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            for (int iteration = 0; iteration < 128; ++iteration)
            {
                int alignment = 1 << (iteration % 7);
                var root = context.PushStackFrame(8, alignment);
                var child = context.PushStackFrame(8, alignment: 1);
                byte stamp = checked((byte)(iteration + 1));
                context.WriteStackFrame(root, 0, new byte[] { stamp });
                context.WriteStackFrame(child, 0,
                    new byte[] { (byte)(stamp ^ 0x5A) });

                // The current main-line contract is failure-neutral for
                // rejected bounds/alignment; it must not mutate live frames.
                RejectRange(() => context.WriteStackFrame(child, 7,
                    new byte[] { 1, 2 }),
                    "out-of-bounds write cannot consume an adjacent frame");
                RejectRange(() => context.PushStackFrame(8, alignment: 3),
                    "non-power-of-two alignment cannot consume frame credit");
                var actual = new byte[1];
                context.ReadStackFrame(root, 0, actual);
                Require(actual[0] == stamp,
                    "failed child bounds operation corrupted parent frame");
                context.ReadStackFrame(child, 0, actual);
                Require(actual[0] == (byte)(stamp ^ 0x5A),
                    "invalid alignment or bounds operation mutated child frame");

                context.PopStackFrame(child);
                RejectInvalid(() => context.ReadStackFrame(child, 0,
                    new byte[1]), "retired child issuance accepted stale read");
                var reissued = context.PushStackFrame(8, alignment: 1);
                context.ReadStackFrame(reissued, 0, actual);
                Require(actual[0] == 0,
                    "reissued frame retained bytes from previous capability");
                context.PopStackFrame(reissued);
                context.PopStackFrame(root);
                verified++;
            }
        });

        runtime.Send(actor, new byte[] { 1 });
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "128 bounded frame reissuance rounds complete on native actor arena");
        Require(verified == 128 && runtime.FaultedTurns == 0,
            "frame bounds and capability recycling preserve live actor progress");
        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0 &&
            runtime.NativeReservedBytes == 0),
            "repeated frame reissuance does not strand actor native credits");
    }

    private static void TerminalQuotaFaultsAndSiblingSurvival()
    {
        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 4, MessagesPerDispatch = 1,
            MailboxMessages = 4, MailboxBytes = 128,
            ActorPersistentBytes = 256, ActorScratchBytes = 64,
            ActorStackBytes = 64, ActorMaxStackFrames = 2,
            NativeBudgetBytes = 8192,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
                QueueCapacityPerDomain = 4
            }
        });
        using var survivorCompleted = new ManualResetEventSlim();
        var validatedFaults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var depth = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var root = context.PushStackFrame(16);
            context.PushStackFrame(8);
            bool caught = false;
            try { context.PushStackFrame(8); }
            catch (ActorStackQuotaExceededException) { caught = true; }
            Require(caught, "depth quota must throw the exact typed exception");
            RejectInvalid(() => context.ReadStackFrame(root, 0, new byte[1]),
                "caught depth quota failure must revoke stack reads");
            validatedFaults.Enqueue("depth");
        });
        var bytes = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            // A typed ABI frame loses its authority after cumulative quota
            // exhaustion; the host cannot recover by catching the exception.
            var typed = ActorScalarFrameAbiV1.Push(context, new byte[] { 1, 2 }, 1);
            ActorScalarFrameAbiV1.WriteI64(context, typed, 0, 42);
            bool caught = false;
            try { context.PushStackFrame(24); }
            catch (ActorStackQuotaExceededException) { caught = true; }
            Require(caught, "cumulative byte quota must throw exact typed fault");
            RejectInvalid(() => context.PushStackFrame(8),
                "caught byte quota failure must revoke new admission");
            RejectInvalid(() => { ActorScalarFrameAbiV1.ReadI64(context, typed, 0); },
                "typed scalar spill cannot be read from terminally poisoned stack");
            RejectInvalid(() => context.PopStackFrame(typed),
                "terminal stack fault must not permit trusted raw Pop bypass");
            validatedFaults.Enqueue("aggregate");
        });
        var singleFrame = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            bool caught = false;
            try { context.PushStackFrame(65); }
            catch (ActorStackQuotaExceededException) { caught = true; }
            Require(caught, "oversized single frame must throw exact typed fault");
            RejectInvalid(() => context.PushStackFrame(1),
                "single oversized frame poisons only its own actor");
            validatedFaults.Enqueue("oversized");
        });
        var sibling = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var frame = context.PushStackFrame(8);
            context.WriteStackFrame(frame, 0, new byte[] { 42 });
            byte[] observed = new byte[1];
            context.ReadStackFrame(frame, 0, observed);
            Require(observed[0] == 42,
                "sibling actor frame must survive unrelated quota poisoning");
            context.PopStackFrame(frame);
            survivorCompleted.Set();
        });
        runtime.Send(depth, new byte[] { 1 });
        runtime.Send(bytes, new byte[] { 2 });
        runtime.Send(singleFrame, new byte[] { 3 });
        runtime.Send(sibling, new byte[] { 4 });
        Require(survivorCompleted.Wait(TimeSpan.FromSeconds(10)),
            "healthy actor failed to execute after poisoned actors");
        Require(SpinWait.SpinUntil(() =>
                runtime.FaultedTurns == 3 && runtime.CompletedMessages == 1 &&
                runtime.LiveActors == 1, TimeSpan.FromSeconds(10)),
            "caught quota faults must not count as successful actor messages");
        Require(validatedFaults.Count == 3 &&
                validatedFaults.Contains("depth") &&
                validatedFaults.Contains("aggregate") &&
                validatedFaults.Contains("oversized"),
            "all quota paths must execute and prove terminal denial after catch");
        Require(runtime.StartedCarrierThreads == 1 &&
                runtime.NativeReservedBytes > 0,
            "single shared carrier and sibling native arena remain available");
        runtime.Stop(sibling);
        Require(SpinWait.SpinUntil(() =>
                runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(10)), "all actor arena quotas refunded after STOP");
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
        catch (NotSupportedException denied)
            when (denied.Message.Contains(
                      "Actor logical frame authority cannot be", StringComparison.Ordinal) ||
                  denied.Message.Contains(
                      "Actor execution authority cannot be", StringComparison.Ordinal))
        {
            // A generic NativeAOT serializer-metadata failure is NOT evidence
            // that the actor handle's deliberate no-wire converter ran.
            return;
        }
        throw new Exception("Expected explicit actor frame JSON denial: " + scenario);
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
