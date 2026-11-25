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
        Reject(source.Replace("5 + 7", "saved + 7", StringComparison.Ordinal));
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

    private static void CheckProducerLifecycle()
    {
        int calls = 0;
        var cancelled = new OresVmHost.ActorScalarTask(() => ++calls);
        cancelled.CancelUnstarted();
        Require(cancelled.Future.IsCanceled && calls == 0, "unstarted cancellation");
        try { cancelled.Run(); throw new Exception("cancelled producer executed"); }
        catch (InvalidOperationException) { }
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
