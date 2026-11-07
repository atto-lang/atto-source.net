using Ores.Net.Runtime;
using System.Threading;

internal static class ActorFrameStackSelfTest
{
    public static void Run()
    {
        IndependentSlabsAndByteCopies();
        StrictLifoAndQuotaAreTerminal();
        SuspensionResumesOnAnotherCarrier();
        StaleContinuationPoisonsOnlyItsActor();
        RetireScrubsAndRefundsExactlyOnce();
        IntegratedFaultKeepsSiblingAlive();
        UnbalancedHandlerCannotSwallowStackEscape();
        StopDuringLiveTurnMustAllowAuthenticatedUnwind();
        FrameAuthorityIsNotJsonData();
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

    private static void StopDuringLiveTurnMustAllowAuthenticatedUnwind()
    {
        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 2,
            MessagesPerDispatch = 2,
            ActorPersistentBytes = 256,
            ActorScratchBytes = 128,
            ActorFrameBytes = 128,
            ActorMaxFrames = 4,
            NativeBudgetBytes = 8192,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
                QueueCapacityPerDomain = 2
            }
        });
        using var entered = new ManualResetEventSlim();
        using var allowUnwind = new ManualResetEventSlim();
        int unwound = 0;
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            var execution = context.BeginFrames();
            var root = context.PushFrame(execution, 16);
            context.WriteFrame(execution, root, 0, new byte[] { 0xF1 });
            entered.Set();
            if (!allowUnwind.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Actor STOP unwind probe timed out");

            // STOP sealed both native arena admission and frame admission.
            ExpectInvalid(() => context.PushFrame(execution, 8),
                "STOP cannot admit a new logical frame");
            // A stolen physical-turn token must not grant cleanup authority.
            bool offCarrierDenied = Task.Run(() =>
            {
                try
                {
                    context.PopFrame(execution, root);
                    return false;
                }
                catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            Require(offCarrierDenied, "off-carrier frame cleanup denied after STOP");

            context.PopFrame(execution, root);
            context.FinishFrames(execution);
            Interlocked.Exchange(ref unwound, 1);
        });

        runtime.Send(actor, new byte[] { 1 });
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)),
                "handler entered frame execution before STOP");
            runtime.Stop(actor);
        }
        finally { allowUnwind.Set(); }

        Require(SpinWait.SpinUntil(
            () => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(5)), "retired actor reclaimed stack and heap");
        Require(Volatile.Read(ref unwound) == 1 &&
                runtime.CompletedMessages == 1 && runtime.FaultedTurns == 0,
            "STOP allows same-carrier frame unwind without spurious actor fault");
    }

    private static void FrameAuthorityIsNotJsonData()
    {
        using var arena = new ActorArena(7600, 1, 256, 128, 4, 2, 2);
        using var frames = new ActorFrameStack(
            arena, new ActorArenaBudget(128), 4, 128);
        using var turn = arena.EnterTurn(7600, 1);
        var execution = frames.Begin(turn);
        var handle = frames.Push(turn, execution, 16);
        var continuation = frames.Suspend(turn, execution);
        ExpectJsonDenied(() =>
            _ = System.Text.Json.JsonSerializer.Serialize(execution),
            "execution ticket cannot cross JSON");
        ExpectJsonDenied(() =>
            _ = System.Text.Json.JsonSerializer.Serialize(handle),
            "frame handle cannot cross JSON");
        ExpectJsonDenied(() =>
            _ = System.Text.Json.JsonSerializer.Serialize(continuation),
            "continuation ticket cannot cross JSON");
        ExpectJsonDenied(() =>
            _ = System.Text.Json.JsonSerializer.Deserialize<ActorFrameExecution>("{}"),
            "execution authority cannot be minted from JSON");
        ExpectJsonDenied(() =>
            _ = System.Text.Json.JsonSerializer.Deserialize<ActorFrameHandle>("{\"Length\":16}"),
            "frame handle cannot be minted from JSON");
        ExpectJsonDenied(() =>
            _ = System.Text.Json.JsonSerializer.Deserialize<ActorFrameContinuation>("{}"),
            "continuation authority cannot be minted from JSON");
    }

    private static void ExpectJsonDenied(Action callback, string scenario)
    {
        try { callback(); }
        catch (NotSupportedException ex) when
            (ex.Message.Contains("authority", StringComparison.Ordinal)) { return; }
        throw new Exception("Expected explicit JSON authority denial: " + scenario);
    }

    private static void ExpectInvalid(Action callback, string scenario)
    {
        try { callback(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected actor frame denial: " + scenario);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition) throw new Exception("Actor frame stack regression: " + scenario);
    }
}
