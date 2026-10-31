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
        Console.WriteLine("singleton stage0: once-publication, sticky failure, same-flow cycles, cancellation and retirement host tests PASS (not guest or NativeAOT proof)");
    }
}
