using Ores.Net.Runtime;
using System.Text.Json.Serialization;

internal static class ActorFrameStackSelfTest
{
    public static void Run()
    {
        InvalidConfigurationFailsBeforeCarrierAdmission();
        IndependentStacksSurviveTurnsAndRejectForeignAndStaleHandles();
        BoundsAndLifoExhaustionAreFailureNeutral();
        UncaughtLogicalOverflowRetiresOnlyOwner();
        CompilerFrameOverflowCannotBeSuppressedAndRetainsControlNotice();
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
            _ = System.Text.Json.JsonSerializer.Serialize(
                saved!, ActorFrameStackJsonContext.Default.ActorStackFrameHandle);
        }, "actor frame authority cannot be serialized as JSON");
        RejectNoWire(() =>
        {
            _ = System.Text.Json.JsonSerializer.Deserialize(
                "{\"Length\":16}", ActorFrameStackJsonContext.Default.ActorStackFrameHandle);
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
            RejectActorOverflow(() => context.PushStackFrame(8),
                ActorLogicalStackLimit.FrameCount,
                "frame count exhaustion reports a typed actor-local signal");
            RejectRange(() => context.PushStackFrame(129),
                "stack byte budget cannot be bypassed");
            RejectRange(() =>
                context.WriteStackFrame(child, 7, new byte[] { 1, 2 }),
                "frame write cannot cross its issued length");
            RejectInvalid(() => context.PopStackFrame(root),
                "non-LIFO pop leaves both frames live");

            context.PopStackFrame(child);
            RejectActorOverflow(() => context.PushStackFrame(121),
                ActorLogicalStackLimit.ByteCapacity,
                "byte quota exhaustion reports a typed actor-local signal");
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


    // A deterministic logical guest-stack overflow is safe to induce in process.
    // This must not be confused with overflowing the CLR/carrier machine stack.
    private static void UncaughtLogicalOverflowRetiresOnlyOwner()
    {
        foreach (ActorKind kind in new[] {
            ActorKind.Private, ActorKind.Shared, ActorKind.Untrusted })
        {
            using var runtime = new ActorRuntime(Options(stackBytes: 96, maxFrames: 4));
            int ranPastOverflow = 0;
            int typedOverflowObserved = 0;
            int siblingTurns = 0;
            ActorStackFrameHandle? failedFrame = null;

            var failing = runtime.Spawn(kind, (context, _) =>
            {
                failedFrame = context.PushStackFrame(64);
                context.WriteStackFrame(failedFrame, 0, new byte[] { 0x5A });
                // The second aligned frame exceeds this actor's 96-byte stack.
                // Observe the typed fault and rethrow: RunCell must retire
                // the failing actor. A catch that returns would recover the
                // *host test*, not the compiled guest PANIC contract.
                try
                {
                    context.PushStackFrame(64);
                }
                catch (ActorLogicalStackOverflowException overflow)
                {
                    if (overflow.Limit == ActorLogicalStackLimit.ByteCapacity)
                        Interlocked.Exchange(ref typedOverflowObserved, 1);
                    throw;
                }
                Interlocked.Increment(ref ranPastOverflow);
            });
            var sibling = runtime.Spawn(kind, (context, _) =>
            {
                Require(context.Kind == kind, "sibling retains dispatch domain");
                var frame = context.PushStackFrame(32);
                context.WriteStackFrame(frame, 0, new byte[] { 0xB7 });
                Span<byte> observed = stackalloc byte[1];
                context.ReadStackFrame(frame, 0, observed);
                Require(observed[0] == 0xB7,
                    "sibling stack remains usable after peer overflow");
                RejectInvalid(() =>
                    context.ReadStackFrame(failedFrame!, 0, new byte[1]),
                    "sibling cannot borrow failed actor's retained frame handle");
                context.PopStackFrame(frame);
                Interlocked.Increment(ref siblingTurns);
            });

            runtime.Send(failing, new byte[] { 1 });
            Require(WaitUntil(() =>
                runtime.FaultedTurns == 1 && runtime.LiveActors == 1),
                "logical stack overflow must fail-stop only its owning actor");
            Require(Volatile.Read(ref ranPastOverflow) == 0 &&
                    Volatile.Read(ref typedOverflowObserved) == 1 &&
                    runtime.CompletedMessages == 0,
                "failed actor cannot resume after rejected stack admission");
            Require(runtime.NativeReservedBytes == 8192 + 512,
                "failed actor's persistent arena and frame slab are reclaimed");
            RejectInvalid(() => runtime.Send(failing, new byte[] { 2 }),
                "stale failing actor must not be resurrected by another send");

            runtime.Send(sibling, new byte[] { 3 });
            runtime.Send(sibling, new byte[] { 4 });
            Require(WaitUntil(() => runtime.CompletedMessages == 2 &&
                                     Volatile.Read(ref siblingTurns) == 2),
                "healthy sibling must process two subsequent turns");
            Require(runtime.FaultedTurns == 1 && runtime.LiveActors == 1,
                "healthy sibling is neither faulted nor retired");
            runtime.Stop(sibling);
            Require(WaitUntil(() =>
                runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
                "all actor-local native memory must be refunded on final stop");
        }
    }

    // The parser/compiler ABI owns this allocation, not a host diagnostics
    // caller. Once a checked activation quota is exceeded, catching the C#
    // exception must not turn a failed compiled guest turn into success.
    // The host CONTROL receipt is independent of the saturated DATA mailbox.
    private static void CompilerFrameOverflowCannotBeSuppressedAndRetainsControlNotice()
    {
        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 2,
            MessagesPerDispatch = 1,
            MailboxMessages = 1,
            ActorPersistentBytes = 8192,
            ActorScratchBytes = 512,
            ActorStackBytes = 96,
            ActorMaxStackFrames = 1,
            NativeBudgetBytes = 256 * 1024,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
                QueueCapacityPerDomain = 4
            }
        });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int caught = 0;
        int peerTurns = 0;
        var failed = runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Compiler frame probe timed out");
            var first = ActorScalarFrameAbiV1.Push(context, new byte[] { 1 }, 1);
            try
            {
                ActorScalarFrameAbiV1.Push(context, new byte[] { 1 }, 1);
                throw new Exception("Second compiler frame unexpectedly admitted");
            }
            catch (ActorLogicalStackOverflowException exhausted)
                when (exhausted.Limit == ActorLogicalStackLimit.FrameCount)
            {
                Interlocked.Increment(ref caught);
            }
            // Cleanup is still permitted. Successful C# callback return
            // must nevertheless be converted to a terminal actor failure.
            ActorScalarFrameAbiV1.Pop(context, first);
        });
        var peer = runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            var own = context.PushStackFrame(8);
            context.WriteStackFrame(own, 0, new byte[] { 0x42 });
            Span<byte> value = stackalloc byte[1];
            context.ReadStackFrame(own, 0, value);
            Require(value[0] == 0x42, "same-domain peer owns its own stack");
            context.PopStackFrame(own);
            Interlocked.Increment(ref peerTurns);
        });

        try
        {
            runtime.Send(failed, new byte[] { 1 });
            Require(entered.Wait(TimeSpan.FromSeconds(5)),
                "failed actor must enter its current turn");
            runtime.Send(failed, new byte[] { 2 });
            RejectInvalid(() => runtime.Send(failed, new byte[] { 3 }),
                "saturated DATA mailbox must reject excess work");
            release.Set();
            Require(WaitUntil(() =>
                runtime.FaultedTurns == 1 && runtime.LiveActors == 1),
                "caught compiled-frame overflow still kills only its owner");
            Require(Volatile.Read(ref caught) == 1 &&
                    runtime.CompletedMessages == 0,
                "caught stack overflow must not acknowledge its guest turn");
            Require(runtime.PendingTerminalFaults == 1,
                "terminal CONTROL notice survives actor reclamation");
            RejectInvalid(() => runtime.Send(failed, new byte[] { 4 }),
                "failed actor and its queued DATA work are retired");

            runtime.Send(peer, new byte[] { 1 });
            Require(WaitUntil(() =>
                Volatile.Read(ref peerTurns) == 1 && runtime.CompletedMessages == 1),
                "healthy actor runs on the same single-worker domain");

            RejectInvalid(() => runtime.Spawn(ActorKind.Private,
                    (context, message) => { }),
                "unread terminal CONTROL receipt reserves actor capacity");
            Require(runtime.TryReadTerminalFault(out var notice),
                "host CONTROL reader must receive one terminal fault");
            Require(notice.Sequence == 1 &&
                    notice.ActorId == failed.Id &&
                    notice.Generation == failed.Generation &&
                    notice.ActorKind == ActorKind.Private &&
                    notice.Kind == ActorTerminalFaultKind.LogicalStackOverflow &&
                    notice.StackLimit == ActorLogicalStackLimit.FrameCount,
                "terminal receipt must preserve owner and typed quota cause");
            Require(runtime.PendingTerminalFaults == 0 &&
                    !runtime.TryReadTerminalFault(out _),
                "terminal receipt must never be delivered twice");

            var replacement = runtime.Spawn(ActorKind.Private,
                (context, message) => { });
            runtime.Send(replacement, new byte[] { 1 });
            Require(WaitUntil(() => runtime.CompletedMessages == 2),
                "new actor can start only after terminal receipt drain");
            runtime.Stop(replacement);
            runtime.Stop(peer);
            Require(WaitUntil(() =>
                runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
                "terminal actor and all healthy replacements refund memory");
        }
        finally
        {
            release.Set();
        }
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

    private static void RejectActorOverflow(Action action,
        ActorLogicalStackLimit expected, string scenario)
    {
        try { action(); }
        catch (ActorLogicalStackOverflowException fault)
        {
            Require(fault.Limit == expected, scenario + ": incorrect fault reason");
            return;
        }
        throw new Exception("Expected typed actor stack overflow: " + scenario);
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

// NativeAOT-safe proof: issue conversion metadata at compile time, never
// enable reflection-based JSON serialization of actor frame capabilities.
[JsonSerializable(typeof(ActorStackFrameHandle))]
internal partial class ActorFrameStackJsonContext : JsonSerializerContext
{
}
