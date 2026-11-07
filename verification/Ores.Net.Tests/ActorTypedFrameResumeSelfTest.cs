using Ores.Net.Runtime;

internal static class ActorTypedFrameResumeSelfTest
{
    public static void Run()
    {
        InvalidLayoutsAndMutableCallerInputsAreRejected();
        SingleClaimResumeIsActorAndTurnBound();
        CanceledContinuationCannotReplayOrLeakFrameBytes();
        InFlightCancellationCanUnwindAfterStop();
        StackExhaustionFaultsOneActorNotItsPeer();
        Console.WriteLine(
            "PASS .NET typed actor spills and single-claim resume host-only contract (not .atto guest/AOT)");
    }

    private static ActorRuntimeOptions Options(int stackBytes = 2048) => new()
    {
        MaxActors = 4,
        MessagesPerDispatch = 4,
        ActorPersistentBytes = 8192,
        ActorScratchBytes = 512,
        ActorStackBytes = stackBytes,
        ActorMaxStackFrames = 4,
        NativeBudgetBytes = 256 * 1024,
        Dispatcher = new ActorDispatcherOptions
        {
            PrivateWorkers = 2,
            SharedWorkers = 2,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 8
        }
    };

    private static void InvalidLayoutsAndMutableCallerInputsAreRejected()
    {
        RejectRange(() => _ = new ActorStackFrameLayout(),
            "empty typed stack layouts are not executable");
        RejectRange(() => _ = new ActorStackFrameLayout(
            new ActorStackSlotKind[129]),
            "bounded typed slot count");
        RejectRange(() => _ = new ActorStackFrameLayout(
            (ActorStackSlotKind)17),
            "invalid typed slot enum");
        var input = new[] { ActorStackSlotKind.I64, ActorStackSlotKind.Bool };
        var layout = new ActorStackFrameLayout(input);
        input[0] = ActorStackSlotKind.Bool;
        Require(layout.GetKind(0) == ActorStackSlotKind.I64 &&
                layout.GetKind(1) == ActorStackSlotKind.Bool &&
                layout.ByteLength == 16,
            "caller cannot mutate a verified frame layout after creation");
        RejectRange(() => _ = layout.GetKind(2),
            "layout slot lookup cannot escape its bounds");
    }

    private static void SingleClaimResumeIsActorAndTurnBound()
    {
        var layout = new ActorStackFrameLayout(
            ActorStackSlotKind.I64, ActorStackSlotKind.Bool, ActorStackSlotKind.I64);
        using var runtime = new ActorRuntime(Options());
        ActorStackFrameHandle? saved = null;
        ActorStackResumeTicket? ticket = null;
        ActorContext? oldContext = null;

        var owner = runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            if (message.Span[0] == 1)
            {
                saved = context.PushTypedStackFrame(layout);
                context.WriteStackI64(saved, 0, long.MinValue);
                context.WriteStackBool(saved, 1, true);
                context.WriteStackI64(saved, 2, long.MaxValue);
                oldContext = context;
                ticket = context.SuspendStackFrame(saved);
                RejectInvalid(() => _ = context.ResumeStackFrame(ticket!),
                    "cannot resume within the suspending physical turn");
                RejectInvalid(() => context.ReadStackI64(saved, 0),
                    "cannot read while the actor stack is suspended");
                RejectInvalid(() => context.WriteStackBool(saved, 1, false),
                    "cannot overwrite a suspended continuation");
                RejectInvalid(() => context.PopStackFrame(saved),
                    "cannot pop a suspended continuation without cancellation");
                RejectInvalid(() => context.PushStackFrame(8),
                    "cannot create new frame under a frozen stack");
                return;
            }

            Require(message.Span[0] == 2, "unexpected owner message");
            var restored = context.ResumeStackFrame(ticket!);
            Require(ReferenceEquals(restored, saved),
                "resume returned original exact-issued frame handle");
            Require(context.ReadStackI64(restored, 0) == long.MinValue &&
                    context.ReadStackBool(restored, 1) &&
                    context.ReadStackI64(restored, 2) == long.MaxValue,
                "typed spill bytes survived across distinct authenticated turns");
            RejectInvalid(() => _ = context.ResumeStackFrame(ticket!),
                "single-claim ticket replay denied");
            RejectInvalid(() => context.ReadStackBool(restored, 0),
                "wrong slot kind cannot masquerade as bool");
            RejectInvalid(() => context.WriteStackI64(restored, 1, 42),
                "wrong slot kind cannot masquerade as i64");
            RejectRange(() => _ = context.ReadStackI64(restored, 3),
                "typed slot indices are bounded");
            context.WriteStackFrame(restored, 8, new byte[] { 2 });
            RejectFormat(() => _ = context.ReadStackBool(restored, 1),
                "noncanonical raw bool is rejected on typed restoration");
            context.WriteStackBool(restored, 1, false);
            Require(!context.ReadStackBool(restored, 1),
                "typed bool restoration uses canonical one-byte encoding");
            context.PopStackFrame(restored);
            RejectInvalid(() => context.PopStackFrame(restored),
                "popped resumed frame cannot be reused");
        });

        var foreign = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            RejectInvalid(() => _ = context.ResumeStackFrame(ticket!),
                "foreign actor cannot claim another actor resume ticket");
            RejectInvalid(() => context.CancelSuspendedStackFrame(ticket!),
                "foreign actor cannot cancel another actor continuation");
            RejectInvalid(() => _ = context.ReadStackI64(saved!, 0),
                "foreign actor cannot read another actor's typed spill");
        });

        runtime.Send(owner, new byte[] { 1 });
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "initial actor suspension completed its physical turn");
        RejectInvalid(() => _ = oldContext!.ResumeStackFrame(ticket!),
            "expired physical-turn context cannot claim a continuation");
        RejectInvalid(() => _ = oldContext!.ReadStackI64(saved!, 0),
            "expired physical-turn context cannot recover native spills");
        RejectNoWire(() => _ = System.Text.Json.JsonSerializer.Serialize(ticket!),
            "resume authority must never become serialized JSON");
        RejectNoWire(() => _ = System.Text.Json.JsonSerializer.Deserialize<ActorStackResumeTicket>(
            "{}"), "resume authority must never be reconstructed from JSON");

        runtime.Send(foreign, new byte[] { 3 });
        Require(WaitUntil(() => runtime.CompletedMessages == 2),
            "foreign access denial completed without actor failure");
        runtime.Send(owner, new byte[] { 2 });
        Require(WaitUntil(() => runtime.CompletedMessages == 3),
            "typed resume and single-claim validations completed");
        Require(runtime.FaultedTurns == 0, "valid owner and denied foreign turns remain healthy");
        runtime.Stop(owner);
        runtime.Stop(foreign);
        Require(WaitUntil(() =>
                runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
            "actor lifetime retirement refunds typed stack storage");
    }

    private static void CanceledContinuationCannotReplayOrLeakFrameBytes()
    {
        using var runtime = new ActorRuntime(Options());
        var layout = new ActorStackFrameLayout(ActorStackSlotKind.I64,
            ActorStackSlotKind.Bool);
        ActorStackFrameHandle? canceledFrame = null;
        ActorStackResumeTicket? canceledTicket = null;
        var actor = runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            if (message.Span[0] == 1)
            {
                canceledFrame = context.PushTypedStackFrame(layout);
                context.WriteStackI64(canceledFrame, 0, -123456789);
                context.WriteStackBool(canceledFrame, 1, true);
                canceledTicket = context.SuspendStackFrame(canceledFrame);
                return;
            }

            context.CancelSuspendedStackFrame(canceledTicket!);
            RejectInvalid(() => _ = context.ResumeStackFrame(canceledTicket!),
                "canceled ticket cannot resume");
            RejectInvalid(() => context.CancelSuspendedStackFrame(canceledTicket!),
                "cancellation is exactly once");
            RejectInvalid(() => _ = context.ReadStackI64(canceledFrame!, 0),
                "canceled frame handle is no longer live");
            var reused = context.PushTypedStackFrame(layout);
            Require(context.ReadStackI64(reused, 0) == 0 &&
                    !context.ReadStackBool(reused, 1),
                "cancellation scrubs typed slots before native offset reuse");
            context.PopStackFrame(reused);

            // Same-turn cancellation is an unwind operation, not a resume.
            var sameTurn = context.PushTypedStackFrame(layout);
            var immediate = context.SuspendStackFrame(sameTurn);
            context.CancelSuspendedStackFrame(immediate);
            RejectInvalid(() => _ = context.ResumeStackFrame(immediate),
                "canceled current-turn ticket never becomes resumable");
        });

        runtime.Send(actor, new byte[] { 1 });
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "cancellation fixture first physical turn completed");
        runtime.Send(actor, new byte[] { 2 });
        Require(WaitUntil(() => runtime.CompletedMessages == 2),
            "canceled continuation cleanup and reuse completed");
        Require(runtime.FaultedTurns == 0, "normal cancellation does not fault actor");
        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
            "canceled typed frames refund their actor native arena on stop");
    }

    private static void InFlightCancellationCanUnwindAfterStop()
    {
        using var runtime = new ActorRuntime(Options());
        using var entered = new ManualResetEventSlim();
        using var leave = new ManualResetEventSlim();
        bool canceled = false;
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            var frame = context.PushTypedStackFrame(
                new ActorStackFrameLayout(ActorStackSlotKind.I64));
            context.WriteStackI64(frame, 0, 42);
            var ticket = context.SuspendStackFrame(frame);
            entered.Set();
            if (!leave.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("retirement cancellation fixture timed out");
            RejectInvalid(() => _ = context.ResumeStackFrame(ticket),
                "retiring actor cannot resume or admit new execution");
            context.CancelSuspendedStackFrame(ticket);
            canceled = true;
        });

        runtime.Send(actor, new byte[] { 1 });
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)),
                "actor entered suspension fixture");
            runtime.Stop(actor);
        }
        finally { leave.Set(); }
        Require(WaitUntil(() => runtime.LiveActors == 0 &&
                    runtime.NativeReservedBytes == 0 && runtime.CompletedMessages == 1),
            "retired actor permits in-flight cancellation and physical cleanup");
        Require(canceled && runtime.FaultedTurns == 0,
            "cancellation on retiring physical turn cannot crash process");
    }

    private static void StackExhaustionFaultsOneActorNotItsPeer()
    {
        using var runtime = new ActorRuntime(Options(stackBytes: 32));
        var offender = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            _ = context.PushStackFrame(24);
            // This exception escapes the handler: the host actor must stop.
            // Native CLR machine recursion is never attempted.
            _ = context.PushStackFrame(24);
        });
        var peer = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var frame = context.PushTypedStackFrame(
                new ActorStackFrameLayout(ActorStackSlotKind.I64));
            context.WriteStackI64(frame, 0, 42);
            Require(context.ReadStackI64(frame, 0) == 42,
                "surviving actor has separate native frame storage");
            context.PopStackFrame(frame);
        });
        runtime.Send(offender, new byte[] { 1 });
        runtime.Send(peer, new byte[] { 1 });
        Require(WaitUntil(() => runtime.FaultedTurns == 1 &&
                    runtime.CompletedMessages == 1 && runtime.LiveActors == 1),
            "logical stack overflow faults only its owning actor");
        runtime.Stop(peer);
        Require(WaitUntil(() => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
            "survivor and failed actor both refund native arenas");
    }

    private static bool WaitUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    private static void RejectInvalid(Action action, string scenario)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Actor typed frame admission was too permissive: " + scenario);
    }

    private static void RejectRange(Action action, string scenario)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Actor typed frame bounds were too permissive: " + scenario);
    }

    private static void RejectFormat(Action action, string scenario)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Actor typed frame bool validation was too permissive: " + scenario);
    }

    private static void RejectNoWire(Action action, string scenario)
    {
        try { action(); }
        catch (NotSupportedException) { return; }
        throw new Exception("Actor typed frame authority reached JSON: " + scenario);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor typed continuation regression: " + scenario);
    }
}
