using Ores.Net.Runtime;
using System.Threading;

internal static class ActorFrameStackSelfTest
{
    public static void Run()
    {
        IndependentSlabsAndByteCopies();
        StrictLifoAndQuotaAreTerminal();
        DuplicateBeginPoisonsEvenWhenCaught();
        SamePhysicalTurnCannotResume();
        BadFrameSizeAndBoundsAreTerminal();
        AggregateFrameQuotaRollbackAndSiblings();
        SuspensionResumesOnAnotherCarrier();
        SynchronousHandlerCannotEscapeSuspension();
        SuspendedTurnExitRequiresOriginalPhysicalTurn();
        StaleContinuationPoisonsOnlyItsActor();
        RetireScrubsAndRefundsExactlyOnce();
        IntegratedFaultKeepsSiblingAlive();
        UnbalancedHandlerCannotSwallowStackEscape();
        Console.WriteLine("PASS .NET actor frame slab host tests (NOT compiler-backed .atto or native stack isolation)");
    }

    private static void IndependentSlabsAndByteCopies()
    {
        var budget = new ActorArenaBudget(1024);
        using var a = new ActorArena(7001, 1, 256, 128, 4, 2, 2, budget);
        using var b = new ActorArena(7002, 1, 256, 128, 4, 2, 2, budget);
        using var fa = new ActorFrameStack(a, budget, 4, 64);
        using var fb = new ActorFrameStack(b, budget, 4, 64);
        Require(budget.ReservedBytes == 768, "unbegun stack uses no budget");
        using (var ta = a.EnterTurn(7001, 1))
        using (var tb = b.EnterTurn(7002, 1))
        {
            var ea = fa.Begin(ta);
            var eb = fb.Begin(tb);
            var ha = fa.Push(ta, ea, 16);
            var hb = fb.Push(tb, eb, 16);
            byte[] callerBytes = { 1, 2, 3, 4 };
            fa.Write(ta, ea, ha, 0, callerBytes);
            callerBytes[0] = 99;
            byte[] read = new byte[4];
            fa.Read(ta, ea, ha, 0, read);
            Require(read.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "frame writes must copy caller-owned bytes");
            fb.Read(tb, eb, hb, 0, read);
            Require(read.AsSpan().SequenceEqual(new byte[4]),
                "independent sibling frame slab is initially zeroed");

            fa.Pop(ta, ea, ha);
            var recycled = fa.Push(ta, ea, 16);
            fa.Read(ta, ea, recycled, 0, read);
            Require(read.AsSpan().SequenceEqual(new byte[4]),
                "reused frame region is scrubbed");
            fa.Pop(ta, ea, recycled);
            fb.Pop(tb, eb, hb);
            fa.Finish(ta, ea);
            fb.Finish(tb, eb);
            fa.ValidateTurnExit(ta);
            fb.ValidateTurnExit(tb);
        }
        Require(budget.ReservedBytes == 896,
            "each actor stack charges its own 64 byte native slab");
    }

    private static void StrictLifoAndQuotaAreTerminal()
    {
        using var actor = new ActorArena(7100, 7, 128, 64, 4, 2, 2);
        using var stack = new ActorFrameStack(actor, new ActorArenaBudget(128), 2, 64);
        using var turn = actor.EnterTurn(7100, 7);
        var execution = stack.Begin(turn);
        var outer = stack.Push(turn, execution, 24);
        var inner = stack.Push(turn, execution, 24);
        ExpectInvalid(() => stack.Pop(turn, execution, outer), "out-of-order pop");
        Require(stack.IsPoisoned && stack.Depth == 0 && stack.ChargedBytes == 0,
            "LIFO violation permanently poisons only the actor frame slab");
        ExpectInvalid(() => stack.Read(turn, execution, inner, 0, new byte[1]),
            "no stale capability may read a poisoned slab");
        ExpectInvalid(() => stack.Begin(turn), "poisoned execution cannot restart");

        using var otherActor = new ActorArena(7101, 7, 128, 64, 4, 2, 2);
        using var other = new ActorFrameStack(otherActor,
            new ActorArenaBudget(128), 2, 64);
        using var nextTurn = otherActor.EnterTurn(7101, 7);
        var nextExec = other.Begin(nextTurn);
        other.Push(nextTurn, nextExec, 40);
        try
        {
            other.Push(nextTurn, nextExec, 32);
            throw new Exception("frame overflow was not rejected");
        }
        catch (ActorFrameBudgetExceededException) { }
        Require(other.IsPoisoned, "byte exhaustion is terminal for the actor stack");
        ExpectInvalid(() => other.ValidateTurnExit(nextTurn),
            "swallowed quota error cannot convert to successful handler return");
    }

    private static void DuplicateBeginPoisonsEvenWhenCaught()
    {
        using var actor = new ActorArena(7150, 1, 128, 64, 4, 2, 2);
        using var frames = new ActorFrameStack(actor, new ActorArenaBudget(64), 2, 64);
        using var turn = actor.EnterTurn(7150, 1);
        var execution = frames.Begin(turn);
        _ = frames.Push(turn, execution, 8);
        ExpectInvalid(() => frames.Begin(turn),
            "reentrant Begin must fail even when the host catches the denial");
        Require(frames.IsPoisoned && frames.Depth == 0,
            "reentrant handler admission must permanently revoke prior frames");
        ExpectInvalid(() => frames.ValidateSynchronousTurnExit(turn),
            "caught reentrant Begin cannot be converted into a successful turn");
        ExpectInvalid(() => frames.Push(turn, execution, 8),
            "first execution token must not survive a second Begin");
    }

    private static void SamePhysicalTurnCannotResume()
    {
        using var actor = new ActorArena(7160, 1, 128, 64, 4, 2, 2);
        using var frames = new ActorFrameStack(actor, new ActorArenaBudget(64), 2, 64);
        using var turn = actor.EnterTurn(7160, 1);
        var execution = frames.Begin(turn);
        var frame = frames.Push(turn, execution, 8);
        var continuation = frames.Suspend(turn, execution);
        ExpectInvalid(() => frames.Resume(turn, execution, continuation),
            "a continuation must not resume on its original physical turn");
        Require(frames.IsPoisoned && frames.Depth == 0 && !frames.IsSuspended,
            "same-turn continuation replay must be terminal");
        ExpectInvalid(() => frames.Read(turn, execution, frame, 0, new byte[1]),
            "same-turn replay must not expose old bytes");
    }

    private static void BadFrameSizeAndBoundsAreTerminal()
    {
        using var zeroActor = new ActorArena(7180, 1, 128, 64, 4, 2, 2);
        using var writeActor = new ActorArena(7181, 1, 128, 64, 4, 2, 2);
        using var readActor = new ActorArena(7182, 1, 128, 64, 4, 2, 2);
        using var survivorActor = new ActorArena(7183, 1, 128, 64, 4, 2, 2);
        using var zero = new ActorFrameStack(zeroActor, new ActorArenaBudget(64), 2, 64);
        using var writer = new ActorFrameStack(writeActor, new ActorArenaBudget(64), 2, 64);
        using var reader = new ActorFrameStack(readActor, new ActorArenaBudget(64), 2, 64);
        using var survivor = new ActorFrameStack(survivorActor, new ActorArenaBudget(64), 2, 64);
        using var t0 = zeroActor.EnterTurn(7180, 1);
        using var t1 = writeActor.EnterTurn(7181, 1);
        using var t2 = readActor.EnterTurn(7182, 1);
        using var t3 = survivorActor.EnterTurn(7183, 1);
        var e0 = zero.Begin(t0);
        var e1 = writer.Begin(t1);
        var e2 = reader.Begin(t2);
        var e3 = survivor.Begin(t3);
        var f1 = writer.Push(t1, e1, 8);
        var f2 = reader.Push(t2, e2, 8);
        var f3 = survivor.Push(t3, e3, 8);

        ExpectOutOfRange(() => zero.Push(t0, e0, 0),
            "a zero-length compiler-issued frame must terminate its actor");
        ExpectOutOfRange(() => writer.Write(t1, e1, f1, 7, new byte[] { 1, 2 }),
            "frame write cannot cross into a sibling allocation");
        ExpectOutOfRange(() => reader.Read(t2, e2, f2, -1, new byte[1]),
            "negative offset frame reads must terminate the actor");
        Require(zero.IsPoisoned && writer.IsPoisoned && reader.IsPoisoned,
            "caught invalid frame size and byte bounds cannot preserve their actor");
        ExpectInvalid(() => writer.ValidateSynchronousTurnExit(t1),
            "swallowed frame bounds error must not pass handler-exit validation");
        survivor.Write(t3, e3, f3, 0, new byte[] { 42 });
        var result = new byte[1];
        survivor.Read(t3, e3, f3, 0, result);
        Require(result[0] == 42 && !survivor.IsPoisoned,
            "invalid frame operation in actor A must not corrupt actor B");
        survivor.Pop(t3, e3, f3);
        survivor.Finish(t3, e3);
    }

    private static void AggregateFrameQuotaRollbackAndSiblings()
    {
        var budget = new ActorArenaBudget(448);
        using var a = new ActorArena(7170, 1, 128, 64, 4, 2, 2, budget);
        using var b = new ActorArena(7171, 1, 128, 64, 4, 2, 2, budget);
        using var fa = new ActorFrameStack(a, budget, 2, 64);
        using var fb = new ActorFrameStack(b, budget, 2, 64);
        Require(budget.ReservedBytes == 384,
            "two independent heap arenas charged before stack admission");
        using (var turnA = a.EnterTurn(7170, 1))
        using (var turnB = b.EnterTurn(7171, 1))
        {
            var ea = fa.Begin(turnA);
            var ha = fa.Push(turnA, ea, 8);
            Require(budget.ReservedBytes == 448,
                "first actor frame slab exhausts shared reservation capacity");
            ExpectInvalid(() => fb.Begin(turnB),
                "second actor must not overcommit native frame slab budget");
            Require(fb.IsPoisoned && budget.ReservedBytes == 448,
                "denied slab reservation is refunded and target actor fails closed");
            byte[] answer = new byte[1];
            fa.Write(turnA, ea, ha, 0, new byte[] { 42 });
            fa.Read(turnA, ea, ha, 0, answer);
            Require(answer[0] == 42 && !fa.IsPoisoned,
                "failed sibling admission cannot corrupt the admitted actor");
            fa.Pop(turnA, ea, ha);
            fa.Finish(turnA, ea);
        }
        fa.Dispose();
        fb.Dispose();
        a.Retire();
        b.Retire();
        a.TryReclaim();
        b.TryReclaim();
        Require(budget.ReservedBytes == 0,
            "both independent arena/slab reservations refunded on reclaim");
    }

    private static void SuspensionResumesOnAnotherCarrier()
    {
        using var actor = new ActorArena(7200, 3, 256, 128, 4, 2, 2);
        using var stack = new ActorFrameStack(actor, new ActorArenaBudget(128), 4, 64);
        ActorFrameExecution execution;
        ActorFrameHandle frame;
        ActorFrameContinuation continuation;
        using (var first = actor.EnterTurn(7200, 3))
        {
            execution = stack.Begin(first);
            frame = stack.Push(first, execution, 24);
            stack.Write(first, execution, frame, 0, new byte[] { 42 });
            continuation = stack.Suspend(first, execution);
            Require(stack.IsSuspended && stack.ChargedBytes == 24,
                "suspension retains actor-owned frame bytes");
            stack.ValidateTurnExit(first);
            ExpectInvalid(() => stack.Read(first, execution, frame, 0, new byte[1]),
                "suspended frame denies old physical-turn access");
        }

        Exception? error = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var resumed = actor.EnterTurn(7200, 3);
                stack.Resume(resumed, execution, continuation);
                var read = new byte[1];
                stack.Read(resumed, execution, frame, 0, read);
                Require(read[0] == 42 && !stack.IsSuspended,
                    "resumed frame retains contents across carrier migration");
                stack.Pop(resumed, execution, frame);
                stack.Finish(resumed, execution);
                stack.ValidateTurnExit(resumed);
            }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        worker.Start();
        Require(worker.Join(TimeSpan.FromSeconds(10)), "carrier migration test timed out");
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        Require(stack.Depth == 0 && stack.ChargedBytes == 0,
            "resumed frame pop refunds full logical stack charge");
    }

    private static void SynchronousHandlerCannotEscapeSuspension()
    {
        using var actor = new ActorArena(7250, 1, 128, 64, 4, 2, 2);
        using var stack = new ActorFrameStack(actor, new ActorArenaBudget(64), 2, 64);
        using var turn = actor.EnterTurn(7250, 1);
        var execution = stack.Begin(turn);
        _ = stack.Push(turn, execution, 8);
        var continuation = stack.Suspend(turn, execution);
        // A trusted scheduler handoff is a separate seam, not the shipping
        // synchronous dispatcher contract.
        stack.ValidateTurnExit(turn);
        ExpectInvalid(() => stack.ValidateSynchronousTurnExit(turn),
            "synchronous dispatcher must not admit suspended actor continuation");
        Require(stack.IsPoisoned && !stack.IsSuspended && stack.Depth == 0,
            "escaped suspension permanently poisons only this actor stack");
        ExpectInvalid(() => stack.Resume(turn, execution, continuation),
            "poisoned suspended execution cannot be resumed");
    }

    private static void SuspendedTurnExitRequiresOriginalPhysicalTurn()
    {
        using var actor = new ActorArena(7260, 1, 128, 64, 4, 2, 2);
        using var stack = new ActorFrameStack(actor, new ActorArenaBudget(64), 2, 64);
        using (var original = actor.EnterTurn(7260, 1))
        {
            var execution = stack.Begin(original);
            _ = stack.Push(original, execution, 8);
            _ = stack.Suspend(original, execution);
            stack.ValidateTurnExit(original);
        }
        using (var foreignTurn = actor.EnterTurn(7260, 1))
        {
            ExpectInvalid(() => stack.ValidateTurnExit(foreignTurn),
                "a fresh turn cannot attest the previous suspension origin");
        }
        Require(stack.IsPoisoned && stack.Depth == 0,
            "mismatched continuation origin fails closed");
    }

    private static void StaleContinuationPoisonsOnlyItsActor()
    {
        using var firstActor = new ActorArena(7300, 1, 128, 64, 4, 2, 2);
        using var freshActor = new ActorArena(7300, 2, 128, 64, 4, 2, 2);
        using var stack = new ActorFrameStack(firstActor,
            new ActorArenaBudget(128), 2, 64);
        using var successor = new ActorFrameStack(freshActor,
            new ActorArenaBudget(128), 2, 64);
        ActorFrameExecution e;
        ActorFrameContinuation c;
        using (var t = firstActor.EnterTurn(7300, 1))
        {
            e = stack.Begin(t);
            _ = stack.Push(t, e, 8);
            c = stack.Suspend(t, e);
        }
        using (var fresh = freshActor.EnterTurn(7300, 2))
        {
            var next = successor.Begin(fresh);
            var handle = successor.Push(fresh, next, 8);
            ExpectInvalid(() => successor.Resume(fresh, e, c),
                "foreign-generation continuation denied before byte access");
            Require(successor.IsPoisoned, "invalid continuation poisons target stack");
        }
        Require(!stack.IsPoisoned && stack.IsSuspended,
            "wrong-generation attempt cannot mutate original actor stack");
    }

    private static void RetireScrubsAndRefundsExactlyOnce()
    {
        var budget = new ActorArenaBudget(256);
        using var actor = new ActorArena(7400, 1, 128, 64, 4, 2, 2);
        var stack = new ActorFrameStack(actor, budget, 4, 256);
        using (var turn = actor.EnterTurn(7400, 1))
        {
            var e = stack.Begin(turn);
            var h = stack.Push(turn, e, 32);
            stack.Write(turn, e, h, 0, new byte[] { 77 });
            stack.Pop(turn, e, h);
            stack.Finish(turn, e);
        }
        Require(budget.ReservedBytes == 256, "stack charged before native use");
        stack.Seal();
        using (var turn = actor.EnterTurn(7400, 1))
            ExpectInvalid(() => stack.Begin(turn), "sealed stack refuses new execution");
        stack.Dispose();
        stack.Dispose();
        Require(stack.IsRetired && budget.ReservedBytes == 0,
            "retirement scrubs slab and refunds native quota once");
    }

    private static void IntegratedFaultKeepsSiblingAlive()
    {
        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 4,
            MessagesPerDispatch = 4,
            ActorPersistentBytes = 512,
            ActorScratchBytes = 128,
            ActorMaxAllocations = 4,
            ActorMaxLifetimePins = 2,
            ActorMaxReadLoans = 2,
            ActorFrameBytes = 128,
            ActorMaxFrames = 2,
            NativeBudgetBytes = 8192,
            Dispatcher = new ActorDispatcherOptions
            {
                SharedWorkers = 1,
                PrivateWorkers = 1,
                UntrustedWorkers = 1,
                QueueCapacityPerDomain = 8
            }
        });
        using var peerDone = new ManualResetEventSlim();
        int peerValue = 0;
        var failed = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var e = context.BeginFrames();
            _ = context.PushFrame(e, 96);
            try { _ = context.PushFrame(e, 40); }
            catch (ActorFrameBudgetExceededException) { }
            // Deliberately swallow. Runtime must still detect poisoned stack.
        });
        var healthy = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var e = context.BeginFrames();
            var h = context.PushFrame(e, 16);
            context.WriteFrame(e, h, 0, new byte[] { 42 });
            var result = new byte[1];
            context.ReadFrame(e, h, 0, result);
            peerValue = result[0];
            context.PopFrame(e, h);
            context.FinishFrames(e);
            peerDone.Set();
        });
        runtime.Send(failed, new byte[] { 1 });
        runtime.Send(healthy, new byte[] { 2 });
        Require(peerDone.Wait(TimeSpan.FromSeconds(5)) && peerValue == 42,
            "actor B responds on shared carrier even when actor A blows its frame budget");
        Require(SpinWait.SpinUntil(
            () => runtime.LiveActors == 1 && runtime.FaultedTurns >= 1,
            TimeSpan.FromSeconds(5)),
            "terminal frame failure stops actor A without stopping B");
        runtime.Stop(healthy);
        Require(SpinWait.SpinUntil(
            () => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(5)),
            "actor fault and sibling stop reclaim independent stack and heap budgets");
    }

    private static void UnbalancedHandlerCannotSwallowStackEscape()
    {
        using var actor = new ActorArena(7500, 1, 128, 64, 4, 2, 2);
        using var frames = new ActorFrameStack(actor, new ActorArenaBudget(64), 2, 64);
        using var turn = actor.EnterTurn(7500, 1);
        var execution = frames.Begin(turn);
        _ = frames.Push(turn, execution, 8);
        ExpectInvalid(() => frames.ValidateTurnExit(turn),
            "handler must not return with unpopped frames");
        Require(frames.IsPoisoned, "unbalanced handler escape poisons its actor");
    }

    private static void ExpectInvalid(Action callback, string scenario)
    {
        try { callback(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected actor frame denial: " + scenario);
    }

    private static void ExpectOutOfRange(Action callback, string scenario)
    {
        try { callback(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected terminal frame bounds denial: " + scenario);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition) throw new Exception("Actor frame stack regression: " + scenario);
    }
}
