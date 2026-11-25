using Ores.Net.Compiler;
using Ores.Net.Runtime;
using Oreslang.Generated;

internal static class ActorAwaitSourceTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("actor await: " + message);
    }

    private static void Reject(string source)
    {
        try { _ = OresParser.InspectActorAwait(source); }
        catch (FormatException) { return; }
        throw new Exception("unsupported actor await source admitted");
    }

    internal static void Run()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AwaitCounter.atto"));
        var plan = OresParser.InspectActorAwait(source);
        Require(plan.ActorName == "AwaitCounter" && plan.MethodName == "add" &&
            plan.FieldName == "count" && plan.InitialValue == 0, "verified source identity");
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GeneratedAwaitActor.g.cs"));
        Require(ActorAwaitSourceEmitter.EmitCSharp(plan) ==
            golden.Replace("\r\n", "\n", StringComparison.Ordinal), "generated golden differs");
        Reject(source.Replace("suspends ", "", StringComparison.Ordinal));
        Reject(source.Replace("Future<int>", "Future<bool>", StringComparison.Ordinal));
        Reject(source.Replace("await pending", "await incoming", StringComparison.Ordinal));
        Reject(source.Replace("val int saved = incoming;", "val int saved = s.count;", StringComparison.Ordinal));
        Reject(source.Replace("s.count = s.count + (saved + result);",
            "s.count = s.count + (saved + await pending);", StringComparison.Ordinal));
        Reject(source.Replace("await pending;", "await pending; await pending;", StringComparison.Ordinal));
        Reject(source.Replace("return s.count;", "return s;", StringComparison.Ordinal));
        Reject(source.Replace("val int saved", "val int incoming", StringComparison.Ordinal));
        Reject(source.Replace("mtd (AwaitCounter counter) add", "mtd (AwaitCounter counter) receive", StringComparison.Ordinal));
        Reject(source + "\nfnc extra(): int { return 0; }\n");
        foreach (bool serial in new[] { false, true }) RunProfile(serial);
        RunSourceProducer();
        CheckProducerLifecycle();
        RunTwoAwaits(fault: false);
        RunTwoAwaits(fault: true);
        CheckTrapAwaitAbi();
        RunTrappedAwait(fault: false);
        RunTrappedAwait(fault: true);
        RunTrappedFuture();
        RunCapturedTasks();
        CheckCancelledReceiveChildRetirement();
        Console.WriteLine("NET_CAPTURED_ACTOR_TASKS_TESTS_PASS");
        Console.WriteLine("NET_TRAPPED_ACTOR_AWAIT_TESTS_PASS");
        Console.WriteLine("NET_TWO_AWAIT_ACTOR_FRAMES_TESTS_PASS");
        Console.WriteLine("NET_SOURCE_OWNED_ACTOR_TASK_TESTS_PASS");
        Console.WriteLine("NET_VERIFIED_ACTOR_AWAIT_FRAME_TESTS_PASS");
    }

    private static void RunSourceProducer()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TaskCounter.atto"));
        var plan = OresParser.InspectActorAwait(source);
        Require(plan.ProducerLeft == 5 && plan.ProducerRight == 7, "verified producer operands");
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GeneratedTaskActor.g.cs"));
        Require(ActorAwaitSourceEmitter.EmitCSharp(plan) ==
            golden.Replace("\r\n", "\n", StringComparison.Ordinal), "source-owned producer golden");
        Reject(source.Replace("5 + 7", "incoming + 7", StringComparison.Ordinal));
        Reject(source.Replace("5 + 7", "self.count + 7", StringComparison.Ordinal));
        Reject(source.Replace("5 + 7", "await pending + 7", StringComparison.Ordinal));
        Reject(source.Replace("task<int>", "task<bool>", StringComparison.Ordinal));
        Reject(source.Replace("5 + 7", "9223372036854775808 + 7", StringComparison.Ordinal));
        Reject(source.Replace("5 + 7", "5 + 7 + 9", StringComparison.Ordinal));

        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate("source-task", maxActors: 1, maxActorWorkers: 1);
        host.Start(tenant);
        var actor = AwaitActor_TaskCounter.Create(host, tenant, 1, false);
        var scheduler = host.CreateTrustedScheduler();
        var done = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(10);
        using var stop = new CancellationTokenSource();
        var pump = scheduler.RunPumpAsync(tenant, actor, AwaitActor_TaskCounter.Layout,
            (scope, data) => {
                Require(data.Length == 8, "owned scalar mail");
                var frame = new AwaitActor_TaskCounter(
                    System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span));
                var step = frame.Start_add(scope);
                Require(step.Producer is not null && !step.Ready.IsCompleted,
                    "task must be scheduled, not precomputed by host");
                return new OresVmHost.ActorPumpStep(step.Ready, next => {
                    var result = step.Next(next);
                    done.TrySetResult(frame.Result ?? throw new Exception("missing frame result"));
                    return result;
                }, step.Producer);
            }, _ => throw new Exception("unexpected receipt"),
            (_, _) => throw new Exception("unexpected terminal"), stop.Token);
        try {
            var data = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data, 30);
            host.SendActor(tenant, actor, actor.CurrentRef, data);
            Require(done.Task.WaitAsync(timeout).GetAwaiter().GetResult() == 42,
                "source-owned task and actor result");
        } finally {
            stop.Cancel();
            try { pump.WaitAsync(timeout).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        Require(host.ActorReceiveSnapshot(tenant, actor).Live == 0, "source-task logical retirement");
        host.RetireActor(tenant, actor);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
    }

    private static void RunTwoAwaits(bool fault)
    {
        string name = fault ? "FaultTaskCounter" : "MultiTaskCounter";
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name + ".atto"));
        var plan = OresParser.InspectActorAwait(source);
        Require(plan.SecondLeft == (fault ? long.MaxValue : 3) &&
            plan.SecondRight == (fault ? 1 : 4), "second producer operands");
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Generated" + name + ".g.cs"));
        Require(ActorAwaitSourceEmitter.EmitCSharp(plan) ==
            golden.Replace("\r\n", "\n", StringComparison.Ordinal), "second await golden");
        Reject(source.Replace("await next", "await pending", StringComparison.Ordinal));
        Reject(source.Replace("Future<int> next", "Future<int> pending", StringComparison.Ordinal));
        Reject(source.Replace("saved + result + second", "saved + second + result", StringComparison.Ordinal));
        Reject(source.Replace("val int second = await next;", "val int second = await next; await next;", StringComparison.Ordinal));

        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate(fault ? "two-await-fault" : "two-await-success",
            maxActors: 1, maxActorWorkers: 1);
        host.Start(tenant);
        var actor = fault ? AwaitActor_FaultTaskCounter.Create(host, tenant, 1, true) :
            AwaitActor_MultiTaskCounter.Create(host, tenant, 1, true);
        var scheduler = host.CreateTrustedScheduler();
        var successFrame = new AwaitActor_MultiTaskCounter(23);
        var faultFrame = new AwaitActor_FaultTaskCounter(23);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(10);
        using var stop = new CancellationTokenSource();
        var leases = new HashSet<object>();
        OresVmHost.ActorDispatchScope? previous = null;
        OresVmHost.ActorTurnTicket? logical = null;
        int segments = 0;
        bool sawOriginalFault = false;

        void CheckSegment(OresVmHost.ActorDispatchScope scope)
        {
            var turn = actor.ActiveTurn ?? throw new Exception("missing running actor turn");
            logical ??= turn;
            Require(ReferenceEquals(logical, turn), "logical identity across awaits");
            Require(leases.Add(turn.ScheduledCarrierLease ??
                throw new Exception("missing physical carrier lease")), "physical lease replay");
            if (previous is not null) {
                bool rejected = false;
                try { previous.ReadInt64(0); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "previous state scope survived suspension");
            }
            previous = scope;
            ++segments;
        }

        OresVmHost.ActorPumpStep Wrap(OresVmHost.ActorPumpStep step)
        {
            Require(step.Producer is not null && !step.Ready.IsCompleted &&
                step.ObserveFaultInSegment, "source producer/await outcome binding");
            return step with { Next = scope => {
                CheckSegment(scope);
                try {
                    var next = step.Next(scope);
                    if (next is not null) return Wrap(next);
                    done.TrySetResult();
                    return null;
                } catch (OverflowException) {
                    sawOriginalFault = true;
                    throw;
                }
            }};
        }

        var pump = scheduler.RunPumpAsync(tenant, actor, name + "|1:I",
            (_, _) => throw new Exception("unexpected DATA"),
            scope => {
                CheckSegment(scope);
                return Wrap(fault ? faultFrame.Start_add(scope) : successFrame.Start_add(scope));
            }, (_, _) => throw new Exception("unexpected terminal"), stop.Token);
        try {
            scheduler.Enqueue(tenant, actor);
            if (fault) {
                bool rejected = false;
                try { pump.WaitAsync(timeout).GetAwaiter().GetResult(); }
                catch (OverflowException) { rejected = true; }
                Require(rejected && sawOriginalFault && faultFrame.Result is null,
                    "original producer overflow must reach resumed frame");
            } else {
                done.Task.WaitAsync(timeout).GetAwaiter().GetResult();
                Require(successFrame.Result == 42, "two source awaits result");
            }
        } finally {
            stop.Cancel();
            try { pump.WaitAsync(timeout).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (OverflowException) when (fault && sawOriginalFault) { }
        }
        Require(segments == 3 && leases.Count == 3, "three distinct carrier leases");
        Require(host.ActorReceiveSnapshot(tenant, actor).Live == 0, "two-await logical retirement");
        // Read through a new trusted turn after pump exit. No partial actor
        // update is permitted if the second producer failed.
        scheduler.Enqueue(tenant, actor);
        Require(scheduler.DispatchNext(tenant, actor, name + "|1:I",
            scope => scope.ReadInt64(0)) == (fault ? 0 : 42), "state after producer outcome");
        host.RetireActor(tenant, actor);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
    }

    private static void CheckTrapAwaitAbi()
    {
        var error = GuestResults.CreateError("original async error");
        var ordinary = GuestAwaitResults.TrapCompletedI64(Task.FromException<long>(GuestResults.Throw(error)));
        Require(GuestResults.IsErr(ordinary) && ReferenceEquals(GuestResults.UnwrapErr(ordinary), error),
            "ordinary async Error identity");
        var overflow = GuestAwaitResults.TrapCompletedI64(Task.FromException<long>(new OverflowException()));
        Require(GuestResults.IsErrorType(GuestResults.UnwrapErr(overflow), "ArithmeticOverflowError"),
            "async overflow nominal type");
        var zero = GuestAwaitResults.TrapCompletedI64(Task.FromException<long>(new DivideByZeroException()));
        Require(GuestResults.IsErrorType(GuestResults.UnwrapErr(zero), "DivisionByZeroError"),
            "async zero nominal type");

        void Escapes(Exception signal)
        {
            try {
                _ = GuestAwaitResults.TrapCompletedI64(Task.FromException<long>(signal));
                throw new Exception("trap swallowed a nonordinary effect");
            } catch (Exception actual) when (ReferenceEquals(actual, signal)) { }
        }
        Escapes(GuestResults.Panic(error));
        Escapes(GuestResults.Raise(error));
        Escapes(new InvalidOperationException("host invariant"));
        Escapes(new OperationCanceledException("guest cancellation"));
        try {
            _ = GuestAwaitResults.TrapCompletedI64(Task.FromCanceled<long>(new CancellationToken(true)));
            throw new Exception("trap swallowed a cancelled future");
        } catch (OperationCanceledException) { }
        var pending = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            _ = GuestAwaitResults.TrapCompletedI64(pending.Task);
            throw new Exception("trap awaited incomplete future inline");
        } catch (InvalidOperationException) { }
    }

    private static void RunTrappedAwait(bool fault)
    {
        string name = fault ? "TrapFaultCounter" : "TrapTaskCounter";
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name + ".atto"));
        var plan = OresParser.InspectActorAwait(source);
        Require(plan.TrapResult && plan.ProducerLeft.HasValue, "verified source-owned trap plan");
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Generated" + name + ".g.cs"));
        Require(ActorAwaitSourceEmitter.EmitCSharp(plan) ==
            golden.Replace("\r\n", "\n", StringComparison.Ordinal), "trapped task golden");
        string otherSpelling = fault ? source.Replace("trap(", "trap!(", StringComparison.Ordinal) :
            source.Replace("trap!(", "trap(", StringComparison.Ordinal);
        Require(ActorAwaitSourceEmitter.EmitCSharp(OresParser.InspectActorAwait(otherSpelling)) ==
            ActorAwaitSourceEmitter.EmitCSharp(plan), "trap and trap! must lower identically");
        Reject(source.Replace("Result<int, Error>", "Result<int, bool>", StringComparison.Ordinal));
        Reject(source.Replace("await pending", "await count", StringComparison.Ordinal));
        Reject(source.Replace(fault ? "trap(" : "trap!(", "trap !(", StringComparison.Ordinal));
        Reject(source.Replace("await pending", "await await pending", StringComparison.Ordinal));

        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate(fault ? "trap-fault" : "trap-ok", maxActors: 1, maxActorWorkers: 1);
        host.Start(tenant);
        var actor = fault ? AwaitActor_TrapFaultCounter.Create(host, tenant, 1, true) :
            AwaitActor_TrapTaskCounter.Create(host, tenant, 1, true);
        var scheduler = host.CreateTrustedScheduler();
        var successful = new AwaitActor_TrapTaskCounter();
        var failed = new AwaitActor_TrapFaultCounter();
        var done = new TaskCompletionSource<GuestResultI64>(TaskCreationOptions.RunContinuationsAsynchronously);
        var survived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var timeout = TimeSpan.FromSeconds(10);
        int receipts = 0;
        var pump = scheduler.RunPumpAsync(tenant, actor, name + "|1:I",
            (_, _) => throw new Exception("unexpected DATA"),
            scope => {
                if (++receipts != 1) {
                    Require(scope.ReadInt64(0) == 0, "trapped await changed actor state");
                    survived.TrySetResult();
                    return null;
                }
                var step = fault ? failed.Start_observe(scope) : successful.Start_observe(scope);
                Require(step.Producer is not null && !step.Ready.IsCompleted &&
                    step.ObserveFaultInSegment, "trap must observe asynchronously on resume");
                return step with { Next = next => {
                    var result = step.Next(next);
                    Require(result is null, "trapped frame should complete");
                    done.TrySetResult((fault ? failed.Result : successful.Result) ??
                        throw new Exception("missing typed Result"));
                    return null;
                }};
            }, (_, _) => throw new Exception("unexpected terminal"), stop.Token);
        try {
            scheduler.Enqueue(tenant, actor);
            var outcome = done.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            if (fault)
                Require(GuestResults.IsErr(outcome) &&
                    GuestResults.IsErrorType(GuestResults.UnwrapErr(outcome), "ArithmeticOverflowError"),
                    "trapped producer overflow");
            else Require(GuestResults.IsOk(outcome) && GuestResults.Unwrap(outcome) == 42,
                "trapped producer Ok(42)");
            scheduler.Enqueue(tenant, actor);
            survived.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            Require(!pump.IsCompleted, "ordinary trapped fault terminated pump");
        } finally {
            stop.Cancel();
            try { pump.WaitAsync(timeout).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        Require(host.ActorReceiveSnapshot(tenant, actor).Live == 0, "trapped receive retired");
        host.RetireActor(tenant, actor);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
    }

    private static void RunTrappedFuture()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TrapFutureCounter.atto"));
        var plan = OresParser.InspectActorAwait(source);
        Require(plan.TrapResult && !plan.ProducerLeft.HasValue, "external typed Future plan");
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GeneratedTrapFutureCounter.g.cs"));
        Require(ActorAwaitSourceEmitter.EmitCSharp(plan) ==
            golden.Replace("\r\n", "\n", StringComparison.Ordinal), "trapped Future golden");

        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate("trap-future", maxActors: 1, maxActorWorkers: 1);
        host.Start(tenant);
        var actor = AwaitActor_TrapFutureCounter.Create(host, tenant, 1, true);
        var scheduler = host.CreateTrustedScheduler();
        var expected = GuestResults.CreateError("asynchronous original");
        var pending = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new AwaitActor_TrapFutureCounter(pending.Task);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(10);
        using var stop = new CancellationTokenSource();
        var pump = scheduler.RunPumpAsync(tenant, actor, AwaitActor_TrapFutureCounter.Layout,
            (_, _) => throw new Exception("unexpected DATA"),
            scope => {
                var step = frame.Start_observe(scope);
                started.TrySetResult();
                return step with { Next = next => {
                    var result = step.Next(next);
                    finished.TrySetResult();
                    return result;
                }};
            }, (_, _) => throw new Exception("unexpected terminal"), stop.Token);
        try {
            scheduler.Enqueue(tenant, actor);
            started.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            pending.TrySetException(GuestResults.Throw(expected));
            finished.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            Require(frame.Result.HasValue &&
                ReferenceEquals(GuestResults.UnwrapErr(frame.Result.Value), expected),
                "resumed frame preserves original nominal Error");
        } finally {
            stop.Cancel();
            try { pump.WaitAsync(timeout).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        host.RetireActor(tenant, actor);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
    }

    private static void RunCapturedTasks()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CaptureTaskCounter.atto"));
        var plan = OresParser.InspectActorAwait(source);
        Require(plan.FirstCapturesSaved && plan.SecondCapturesResult &&
            plan.ProducerRight == 5 && plan.SecondRight == 11, "owned capture plan");
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GeneratedCaptureTaskCounter.g.cs"));
        Require(ActorAwaitSourceEmitter.EmitCSharp(plan) ==
            golden.Replace("\r\n", "\n", StringComparison.Ordinal), "captured task golden");
        Reject(source.Replace("return saved + 5", "return count + 5", StringComparison.Ordinal));
        Reject(source.Replace("return saved + 5", "return result + 5", StringComparison.Ordinal));
        Reject(source.Replace("return result + 11", "return second + 11", StringComparison.Ordinal));
        Reject(source.Replace("return result + 11", "return self.count + 11", StringComparison.Ordinal));
        long original = 7;
        var copy = new OresVmHost.ActorScalarTask(static (left, right) => checked(left + right), original, 5);
        original = 99;
        copy.Run();
        Require(original == 99 && copy.Future.GetAwaiter().GetResult() == 12, "scalar capture copied at construction");
        var receiver = new CaptureReceiver();
        try {
            _ = new OresVmHost.ActorScalarTask(receiver.Add, 1, 2);
            throw new Exception("instance-bound producer admitted");
        } catch (InvalidOperationException) { }

        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate("captured-task", maxActors: 1, maxActorWorkers: 1);
        host.Start(tenant);
        var actor = AwaitActor_CaptureTaskCounter.Create(host, tenant, 1, true);
        var scheduler = host.CreateTrustedScheduler();
        var frame = new AwaitActor_CaptureTaskCounter(7);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(10);
        using var stop = new CancellationTokenSource();
        OresVmHost.ActorPumpStep Wrap(OresVmHost.ActorPumpStep step) => step with {
            Next = scope => {
                var next = step.Next(scope);
                if (next is not null) return Wrap(next);
                done.TrySetResult();
                return null;
            }
        };
        var pump = scheduler.RunPumpAsync(tenant, actor, AwaitActor_CaptureTaskCounter.Layout,
            (_, _) => throw new Exception("unexpected DATA"), scope => Wrap(frame.Start_add(scope)),
            (_, _) => throw new Exception("unexpected terminal"), stop.Token);
        try {
            scheduler.Enqueue(tenant, actor);
            done.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            Require(frame.Result == 42, "captured source task result");
        } finally {
            stop.Cancel();
            try { pump.WaitAsync(timeout).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        Require(host.ActorReceiveSnapshot(tenant, actor).Live == 0, "captured frame retired");
        host.RetireActor(tenant, actor);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
    }

    private sealed class CaptureReceiver
    {
        internal long Add(long left, long right) => left + right;
    }

    private static void CheckCancelledReceiveChildRetirement()
    {
        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate("cancel-unpublished-child", maxActors: 1, maxActorWorkers: 1);
        host.Start(tenant);
        var actor = host.RegisterActor(tenant, 1, serial: false, maxLiveReceives: 2);
        host.ConfigureNamedActorState(tenant, actor, "Counter", "Counter|1:I",
            new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64) });
        var scheduler = host.CreateTrustedScheduler();
        var child = new OresVmHost.ActorScalarTask(static () => throw new Exception("cancelled child executed"));
        var siblingReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(10);
        using var stop = new CancellationTokenSource();
        int receipts = 0;
        var pump = scheduler.RunPumpAsync(tenant, actor, "Counter|1:I",
            (_, _) => throw new Exception("unexpected DATA"), scope => {
                if (++receipts == 1) {
                    siblingStarted.TrySetResult();
                    return new OresVmHost.ActorPumpStep(siblingReady.Task, next => {
                        Require(next.AddInt64(0, 42) == 42, "unrelated sibling survived cancellation");
                        finished.TrySetResult();
                        return null;
                    });
                }
                var turn = actor.ActiveTurn ?? throw new Exception("missing running turn");
                host.CancelActorTurn(tenant, actor, turn);
                return new OresVmHost.ActorPumpStep(child.Future,
                    _ => throw new Exception("cancelled child resumed"), child);
            }, (_, _) => throw new Exception("unexpected terminal"), stop.Token);
        try {
            scheduler.Enqueue(tenant, actor);
            siblingStarted.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            scheduler.Enqueue(tenant, actor);
            try {
                child.Future.WaitAsync(timeout).GetAwaiter().GetResult();
                throw new Exception("cancelled unpublished child completed successfully");
            } catch (OperationCanceledException) { }
            Require(child.Future.IsCanceled, "unpublished child was not cancelled");
            siblingReady.TrySetResult();
            finished.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            Require(!pump.IsCompleted, "acknowledged receive cancellation killed pump");
        } finally {
            stop.Cancel();
            try { pump.WaitAsync(timeout).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        Require(host.ActorReceiveSnapshot(tenant, actor).Live == 0, "cancelled receive or sibling leaked");
        host.RetireActor(tenant, actor);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
    }

    private static void CheckProducerLifecycle()
    {
        int calls = 0;
        var cancelled = new OresVmHost.ActorScalarTask(() => ++calls);
        cancelled.CancelUnstarted();
        Require(cancelled.Future.IsCanceled && calls == 0, "unstarted cancellation");
        try { cancelled.Run(); throw new Exception("cancelled producer executed"); }
        catch (InvalidOperationException) { }
        try { cancelled.RunClaimed(); throw new Exception("cancelled producer redeemed as admitted"); }
        catch (InvalidOperationException) { }
        Require(calls == 0, "cancelled producer body ran through claimed path");
        var admitted = new OresVmHost.ActorScalarTask(() => ++calls);
        admitted.Claim();
        admitted.CancelUnstarted();
        admitted.RunClaimed();
        Require(admitted.Future.GetAwaiter().GetResult() == 1 && calls == 1,
            "cancellation cannot revoke already admitted producer");
        var faulted = new OresVmHost.ActorScalarTask(static () => throw new OverflowException());
        faulted.Run();
        Require(faulted.Future.IsFaulted &&
            faulted.Future.Exception?.InnerException is OverflowException, "producer fault identity");
        try { faulted.Run(); throw new Exception("producer replayed"); }
        catch (InvalidOperationException) { }
    }

    private static void RunProfile(bool serial)
    {
        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate(serial ? "await-serial" : "await-ordinary",
            maxActors: 1, maxActorWorkers: 1);
        host.Start(tenant);
        var actor = AwaitActor_AwaitCounter.Create(host, tenant, 1, serial);
        var scheduler = host.CreateTrustedScheduler();
        var readyA = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyB = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new AwaitActor_AwaitCounter(10, readyA.Task);
        var second = new AwaitActor_AwaitCounter(20, readyB.Task);
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(10);
        using var stop = new CancellationTokenSource();
        int calls = 0;
        var pump = scheduler.RunPumpAsync(tenant, actor, AwaitActor_AwaitCounter.Layout,
            (_, _) => throw new Exception("unexpected DATA in await fixture"),
            scope => {
                int call = ++calls;
                if (call == 1) {
                    var step = first.Start_add(scope);
                    startedA.TrySetResult();
                    return new OresVmHost.ActorPumpStep(step.Ready, next => {
                        var result = step.Next(next);
                        resumedA.TrySetResult();
                        return result;
                    });
                }
                if (call == 2) {
                    var step = second.Start_add(scope);
                    startedB.TrySetResult();
                    return new OresVmHost.ActorPumpStep(step.Ready, next => {
                        var result = step.Next(next);
                        resumedB.TrySetResult();
                        return result;
                    });
                }
                Require(scope.ReadInt64(0) == 42, "compiled frame state total");
                finished.TrySetResult();
                return null;
            }, (_, _) => throw new Exception("unexpected terminal"), stop.Token);
        try {
            scheduler.Enqueue(tenant, actor);
            startedA.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            scheduler.Enqueue(tenant, actor);
            if (!serial) {
                startedB.Task.WaitAsync(timeout).GetAwaiter().GetResult();
                Require(!readyA.Task.IsCompleted, "ordinary actor did not interleave");
            }
            readyA.TrySetResult(5);
            startedB.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            if (serial) Require(first.Result == 15, "serial actor started second receive before first retired");
            readyB.TrySetResult(7);
            // Host-only witnesses wrap the emitted resume method; the frame
            // itself retains no test callback or completion source.
            Task.WhenAll(resumedA.Task, resumedB.Task).WaitAsync(timeout).GetAwaiter().GetResult();
            Require(first.Result.HasValue && second.Result.HasValue &&
                Math.Max(first.Result.Value, second.Result.Value) == 42, "compiled frame results");
            scheduler.Enqueue(tenant, actor);
            finished.Task.WaitAsync(timeout).GetAwaiter().GetResult();
        }
        finally {
            stop.Cancel();
            try { pump.WaitAsync(timeout).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        Require(host.ActorReceiveSnapshot(tenant, actor).Live == 0, "retired frame quota");
        host.RetireActor(tenant, actor);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
    }
}
