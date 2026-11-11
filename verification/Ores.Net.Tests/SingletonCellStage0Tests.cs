using Ores.Net.Compiler;
using Ores.Net.Runtime;

// Trusted-host stage-0 substrate tests, not Oreslang guest, actor scheduler,
// deep-immutability, cross-task deadlock, tenant or NativeAOT execution proof.
internal static class SingletonCellStage0Tests
{
    private static TaskCompletionSource<T> Pending<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception("singleton cell stage0: " + message);
    }

    private static async Task ExpectDiagnostic<T>(Task<T> task, string category)
    {
        try { _ = await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (InvalidOperationException error) when (
            error.Message.Contains(category, StringComparison.Ordinal))
        {
            Require(task.IsFaulted && !task.IsCanceled, "diagnostic did not remain a fault");
            return;
        }
        throw new Exception("singleton cell stage0: missing " + category);
    }

    private static async Task ExpectSameFailure<T>(Task<T> task, Exception expected)
    {
        try { _ = await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (ReferenceEquals(error, expected))
        {
            Require(task.IsFaulted && !task.IsCanceled,
                "initializer exception was reclassified as waiter cancellation");
            return;
        }
        throw new Exception("singleton cell stage0: sticky failure identity changed");
    }

    public static async Task Run()
    {
        // 64 simultaneous callers must obtain the same fully published value.
        var entered = Pending<bool>();
        var release = Pending<object>();
        int starts = 0;
        var cell = new SingletonCellStage0<object>(async () =>
        {
            Interlocked.Increment(ref starts);
            entered.TrySetResult(true);
            return await release.Task.ConfigureAwait(false);
        });
        var start = Pending<bool>();
        Task<object>[] callers = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await start.Task.ConfigureAwait(false);
            return await cell.ResolveAsync().ConfigureAwait(false);
        })).ToArray();
        start.SetResult(true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(Volatile.Read(ref starts) == 1, "first-access burst launched duplicate initializer");
        var shared = new object();
        release.SetResult(shared);
        object[] values = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
        Require(values.All(value => ReferenceEquals(value, shared)) &&
                ReferenceEquals(await cell.ResolveAsync(), shared) && starts == 1,
            "published value incomplete, different, or initialized twice");

        // Pre-cancelled observers do not trigger a publication attempt.
        int preStarts = 0;
        var preCell = new SingletonCellStage0<int>(() =>
        {
            Interlocked.Increment(ref preStarts);
            return ValueTask.FromResult(7);
        });
        using (var beforeAdmission = new CancellationTokenSource())
        {
            beforeAdmission.Cancel();
            var denied = preCell.ResolveAsync(beforeAdmission.Token);
            Require(denied.IsCanceled && preStarts == 0,
                "pre-canceled observer triggered initialization");
        }
        Require(await preCell.ResolveAsync() == 7 && preStarts == 1,
            "pre-cancelled observer consumed the cell's single attempt");

        // A canceled waiter must not cancel or reset an in-flight producer.
        var producerEntered = Pending<bool>();
        var producerRelease = Pending<int>();
        int producerStarts = 0;
        var producer = new SingletonCellStage0<int>(async () =>
        {
            Interlocked.Increment(ref producerStarts);
            producerEntered.TrySetResult(true);
            return await producerRelease.Task.ConfigureAwait(false);
        });
        Task<int> survivor = producer.ResolveAsync();
        await producerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var cancelWaiter = new CancellationTokenSource())
        {
            Task<int> abandoned = producer.ResolveAsync(cancelWaiter.Token);
            cancelWaiter.Cancel();
            try { _ = await abandoned.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (abandoned.IsCanceled) { }
            Require(abandoned.IsCanceled && !survivor.IsCompleted && producerStarts == 1,
                "waiter cancellation altered singleton producer");
        }
        producerRelease.SetResult(31);
        Require(await survivor.WaitAsync(TimeSpan.FromSeconds(10)) == 31 &&
                await producer.ResolveAsync() == 31 && producerStarts == 1,
            "live waiter did not receive producer's single publication");

        // All waiters observe the same sticky failure, without implicit retry.
        var failure = new InvalidOperationException("one initializer failure");
        int failedStarts = 0;
        var failed = new SingletonCellStage0<int>(() =>
        {
            Interlocked.Increment(ref failedStarts);
            return ValueTask.FromException<int>(failure);
        });
        await ExpectSameFailure(failed.ResolveAsync(), failure);
        await ExpectSameFailure(failed.ResolveAsync(), failure);
        Require(failedStarts == 1, "failed initializer retried");

        var cancellationNamedFailure = new OperationCanceledException("user initializer failure");
        var cancellationFault = new SingletonCellStage0<int>(() =>
            ValueTask.FromException<int>(cancellationNamedFailure));
        await ExpectSameFailure(cancellationFault.ResolveAsync(), cancellationNamedFailure);
        await ExpectSameFailure(cancellationFault.ResolveAsync(), cancellationNamedFailure);

        var nullCell = new SingletonCellStage0<object?>(() =>
            ValueTask.FromResult<object?>(null));
        await ExpectDiagnostic(nullCell.ResolveAsync(), "ORES-SINGLETON-NULL-PUBLICATION");
        await ExpectDiagnostic(nullCell.ResolveAsync(), "ORES-SINGLETON-NULL-PUBLICATION");

        // A->A and A->B->A same-flow dependencies fail without blocking.
        SingletonCellStage0<int>? recursive = null;
        recursive = new SingletonCellStage0<int>(async () =>
        {
            await Task.Yield(); // Cycle remains visible after an async suspension.
            return await recursive!.ResolveAsync().ConfigureAwait(false);
        });
        await ExpectDiagnostic(recursive.ResolveAsync(), "ORES-SINGLETON-INIT-CYCLE");
        await ExpectDiagnostic(recursive.ResolveAsync(), "ORES-SINGLETON-INIT-CYCLE");

        SingletonCellStage0<int>? left = null, right = null;
        left = new SingletonCellStage0<int>(async () =>
            await right!.ResolveAsync().ConfigureAwait(false));
        right = new SingletonCellStage0<int>(async () =>
            await left!.ResolveAsync().ConfigureAwait(false));
        await ExpectDiagnostic(left.ResolveAsync(), "ORES-SINGLETON-INIT-CYCLE");
        await ExpectDiagnostic(right.ResolveAsync(), "ORES-SINGLETON-INIT-CYCLE");

        // Domain retirement wins over late success; it is terminal and cannot
        // launch a second initializer. Already issued success leases survive.
        var retirementEntered = Pending<bool>();
        var retirementRelease = Pending<int>();
        int retirementStarts = 0;
        var retiring = new SingletonCellStage0<int>(async () =>
        {
            Interlocked.Increment(ref retirementStarts);
            retirementEntered.TrySetResult(true);
            return await retirementRelease.Task.ConfigureAwait(false);
        });
        Task<int> pending = retiring.ResolveAsync();
        await retirementEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(retiring.Retire() && !retiring.Retire(), "retirement not single-winner");
        await ExpectDiagnostic(pending, "ORES-SINGLETON-RETIRED");
        retirementRelease.SetResult(99);
        await ExpectDiagnostic(retiring.ResolveAsync(), "ORES-SINGLETON-RETIRED");
        Require(retirementStarts == 1, "retirement rearmed initializer");
        Require(cell.Retire(), "ready cell retirement not accepted");
        await ExpectDiagnostic(cell.ResolveAsync(), "ORES-SINGLETON-RETIRED");
        Require(values.All(value => ReferenceEquals(value, shared)),
            "retiring a ready cell corrupted a previously acquired value");

        var neverStarted = new SingletonCellStage0<int>(() =>
            throw new Exception("unexpected retired initializer"));
        Require(neverStarted.Retire(), "uninitialized cell did not retire");
        await ExpectDiagnostic(neverStarted.ResolveAsync(), "ORES-SINGLETON-RETIRED");

        // Cross the existing opt-in source checking/IR boundary. Host reference
        // execution is not an emitted guest or NativeAOT binary receipt.
        var plan = SingletonStage0Compiler.Compile(
            "define singleton Settings as end\nfnc main():int{return 42;}",
            "app/settings.ores");
        int compiledStarts = 0;
        var compiledCell = new SingletonCellStage0<long>(() =>
        {
            Interlocked.Increment(ref compiledStarts);
            return ValueTask.FromResult(OresIrReferenceEvaluator.Evaluate(plan.Initializer));
        });
        Require(await compiledCell.ResolveAsync() == 42 &&
                await compiledCell.ResolveAsync() == 42 && compiledStarts == 1,
            "compiled source stage0 initializer was not published once");
        await CrossTaskCyclesAndWaiterBudgets();
        await CarrierBurstMatrix();
        Console.WriteLine("singleton stage0: once-publication, sticky failure, same-flow cycles, cancellation and retirement host tests PASS (not guest or NativeAOT proof)");
    }

    // Physical CLR Task concurrency over a trusted-host cell. The 3/7/15
    // matrix aligns the carrier counts with the shared cross-slice oracle;
    // it does not prove separate AttoLang actor heaps or compiled guest access.
    private static async Task CarrierBurstMatrix()
    {
        foreach (int carrierCount in new[] { 3, 7, 15 })
        {
            var entered = Pending<bool>();
            var release = Pending<long>();
            var start = Pending<bool>();
            int initializes = 0;
            var cell = new SingletonCellStage0<long>(async () =>
            {
                Interlocked.Increment(ref initializes);
                entered.TrySetResult(true);
                return await release.Task.ConfigureAwait(false);
            }, maxPendingWaiters: 16);

            Task<long>[] requests = Enumerable.Range(0, carrierCount)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    return await cell.ResolveAsync().ConfigureAwait(false);
                })).ToArray();
            start.SetResult(true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.SetResult(42L);
            long[] observed = await Task.WhenAll(requests)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Require(observed.Length == carrierCount &&
                    observed.All(value => value == 42) &&
                    initializes == 1 && cell.PendingWaiters == 0,
                "3/7/15 carrier burst broke singleton publication or leaked waiters");
            Require(await cell.ResolveAsync() == 42 && initializes == 1,
                "ready singleton publication was reinitialized after burst");

            var failure = new InvalidOperationException(
                "stage0 singleton factory failure at carrier count " + carrierCount);
            int failureStarts = 0;
            var failed = new SingletonCellStage0<long>(() =>
            {
                Interlocked.Increment(ref failureStarts);
                return ValueTask.FromException<long>(failure);
            }, maxPendingWaiters: 16);
            var failStart = Pending<bool>();
            Task[] observers = Enumerable.Range(0, carrierCount)
                .Select(_ => Task.Run(async () =>
                {
                    await failStart.Task.ConfigureAwait(false);
                    await ExpectSameFailure(failed.ResolveAsync(), failure)
                        .ConfigureAwait(false);
                })).ToArray();
            failStart.SetResult(true);
            await Task.WhenAll(observers).WaitAsync(TimeSpan.FromSeconds(10));
            await ExpectSameFailure(failed.ResolveAsync(), failure);
            Require(failureStarts == 1 && failed.PendingWaiters == 0,
                "3/7/15 failure burst retried the initializer or leaked waiters");
        }
    }

    private static async Task CrossTaskCyclesAndWaiterBudgets()
    {
        // Two independently started task flows each begin their own factory.
        // Both wait until they are initializing before establishing A -> B and
        // B -> A. Graph registration is serialized, so one must be rejected.
        var graph = new SingletonStage0DependencyGraph(maxActive: 16, maxDependencies: 32);
        var enteredA = Pending<bool>();
        var enteredB = Pending<bool>();
        var go = Pending<bool>();
        SingletonCellStage0<int>? a = null, b = null;
        a = new SingletonCellStage0<int>(async () =>
        {
            enteredA.TrySetResult(true);
            await go.Task.ConfigureAwait(false);
            return await b!.ResolveAsync().ConfigureAwait(false);
        }, graph);
        b = new SingletonCellStage0<int>(async () =>
        {
            enteredB.TrySetResult(true);
            await go.Task.ConfigureAwait(false);
            return await a!.ResolveAsync().ConfigureAwait(false);
        }, graph);
        var runningA = Task.Run(async () => await a!.ResolveAsync());
        var runningB = Task.Run(async () => await b!.ResolveAsync());
        await Task.WhenAll(enteredA.Task, enteredB.Task).WaitAsync(TimeSpan.FromSeconds(10));
        Require(graph.ActiveInitializers == 2, "independent initializers not admitted");
        go.SetResult(true);
        await ExpectDiagnostic(runningA, "ORES-SINGLETON-INIT-CYCLE");
        await ExpectDiagnostic(runningB, "ORES-SINGLETON-INIT-CYCLE");
        Require(graph.PendingDependencies == 0,
            "cycle failure leaked dependency registration");
        Require(a.PendingWaiters == 0 && b.PendingWaiters == 0,
            "cycle failure leaked waiter registrations");

        // Count registrations, not just a sampled number of live tasks.
        // Canceling one waiter frees exactly one slot without resetting the
        // factory or revoking another caller.
        var slotGraph = new SingletonStage0DependencyGraph(maxActive: 4, maxDependencies: 4);
        var started = Pending<bool>();
        var released = Pending<int>();
        int starts = 0;
        var bounded = new SingletonCellStage0<int>(async () =>
        {
            Interlocked.Increment(ref starts);
            started.TrySetResult(true);
            return await released.Task.ConfigureAwait(false);
        }, slotGraph, maxPendingWaiters: 2);
        Task<int> first = bounded.ResolveAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        Task<int> canceled = bounded.ResolveAsync(cancellation.Token);
        Require(bounded.PendingWaiters == 2, "waiter slots undercounted");
        await ExpectDiagnostic(bounded.ResolveAsync(), "ORES-SINGLETON-WAITER-CAPACITY");
        Require(starts == 1 && bounded.PendingWaiters == 2,
            "quota denial spawned a second factory or consumed a slot");
        cancellation.Cancel();
        try { _ = await canceled.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) when (canceled.IsCanceled) { }
        Require(canceled.IsCanceled && bounded.PendingWaiters == 1,
            "canceled waiter retained its quota");
        Task<int> replacement = bounded.ResolveAsync();
        Require(bounded.PendingWaiters == 2, "freed slot could not be re-admitted");
        released.SetResult(57);
        Require(await first.WaitAsync(TimeSpan.FromSeconds(10)) == 57 &&
                await replacement.WaitAsync(TimeSpan.FromSeconds(10)) == 57 &&
                bounded.PendingWaiters == 0 && starts == 1,
            "bounded waiters did not observe one publication / refund slots");

        // Bound the graph itself. This is not a domain authorization oracle.
        var oneInitializerGraph = new SingletonStage0DependencyGraph(maxActive: 1, maxDependencies: 1);
        var firstStarted = Pending<bool>();
        var firstReleased = Pending<int>();
        var held = new SingletonCellStage0<int>(async () =>
        {
            firstStarted.TrySetResult(true);
            return await firstReleased.Task.ConfigureAwait(false);
        }, oneInitializerGraph);
        Task<int> heldTask = held.ResolveAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deniedCell = new SingletonCellStage0<int>(
            () => ValueTask.FromResult(3), oneInitializerGraph);
        await ExpectDiagnostic(deniedCell.ResolveAsync(), "ORES-SINGLETON-GRAPH-CAPACITY");
        await ExpectDiagnostic(deniedCell.ResolveAsync(), "ORES-SINGLETON-GRAPH-CAPACITY");
        firstReleased.SetResult(17);
        Require(await heldTask.WaitAsync(TimeSpan.FromSeconds(10)) == 17,
            "graph capacity rejection interrupted healthy initializer");

        // Separate host graphs must not silently share an initialization
        // authority scope. Cross-graph access is explicitly rejected.
        var foreignGraph = new SingletonStage0DependencyGraph();
        var foreignCell = new SingletonCellStage0<int>(
            () => ValueTask.FromResult(2), foreignGraph);
        var crossingCell = new SingletonCellStage0<int>(async () =>
            await foreignCell.ResolveAsync().ConfigureAwait(false),
            new SingletonStage0DependencyGraph());
        await ExpectDiagnostic(crossingCell.ResolveAsync(), "ORES-SINGLETON-GRAPH-BOUNDARY");
        Require(await foreignCell.ResolveAsync() == 2, "host setup of foreign ready cell failed");
        var readyCrossing = new SingletonCellStage0<int>(async () =>
            await foreignCell.ResolveAsync().ConfigureAwait(false),
            new SingletonStage0DependencyGraph());
        await ExpectDiagnostic(readyCrossing.ResolveAsync(), "ORES-SINGLETON-GRAPH-BOUNDARY");
    }

}
