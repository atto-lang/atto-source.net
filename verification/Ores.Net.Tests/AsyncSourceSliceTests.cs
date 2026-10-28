using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class AsyncSourceSliceTests
{
    private const string Guest =
        "fnc main(): Future<int> { return Future.from_task(task { return 42; }); }";

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception("Async guest slice: " + message);
    }

    private static void Reject(string source)
    {
        try { _ = OresAsyncSourceSlice.Parse(source); }
        catch (FormatException) { return; }
        throw new Exception("Async guest slice admitted unsupported source: " + source);
    }

    internal static async Task Run()
    {
        Require(OresAsyncSourceSlice.Parse(Guest).LiteralValue == 42,
            "source-literal admission");
        Require(OresAsyncSourceSlice.Parse(
            "fnc main(): Future<int> {return Future.from_task(task/* nested /* comment */ */{return 42;});}")
            .LiteralValue == 42, "nested trivia does not alter source");
        foreach (var bad in new[] {
            Guest + " fnc malicious():int{return 1;}",
            "fnc main(): Future<int> { return Future.new(task {return 42;}); }",
            "fnc main(): Future<int> { return Future.from_task(task {return other;}); }",
            "fnc main(): Future<int> { return Future.from_task(task {return 1+41;}); }",
            "fnc main(): Future<int> { return Future.from_task(task {return 9223372036854775808;}); }",
            "fnc main(): Future<int> { return Task.from_future(task {return 42;}); }",
            "fnc main(): Future<int> { return Observable.from_task(task {return 42;}); }",
            "fnc main(): int { return Future.from_task(task {return 42;}); }",
            "fnc main(): Future<int> { return Future.from_task(task {return 42;}); } /*",
            Guest + "\0", Guest + "\ud800",
            "fnc main(): Future<int> {return Future.from_task(task {return 42;});} /*" +
                string.Concat(Enumerable.Repeat("/*", 65)) +
                string.Concat(Enumerable.Repeat("*/", 66))
        }) Reject(bad);

        // The ordinary compile-to-CIL path MUST stay fail-closed. This new
        // NativeAOT source runner is not a guest CIL lowering implementation.
        try { _ = OresParser.Parse(Guest); }
        catch (FormatException) { goto compiler_closed; }
        throw new Exception("Async guest slice bypassed ordinary CIL admission");
        compiler_closed:;
        try { _ = OresParser.Parse(
            "fnc main():int{return Future.from_task(task {return 42;});}"); }
        catch (FormatException error) when (
            error.Message.Contains("execution task requires scheduler-aware Future",
                StringComparison.Ordinal)) { goto targeted_diagnostic; }
        throw new Exception("Inline conversion lost targeted scheduler-boundary diagnostic");
        targeted_diagnostic:;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using (var scope = new OresTaskScope(maxProducers: 1, maxObservers: 2))
        {
            var producer = scope.Start(async _ => {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                return await release.Task.ConfigureAwait(false);
            });
            var future = scope.FromTask(producer);
            Require(future.ProducerId == producer.ProducerId,
                "Future retains original producer identity");
            Require(!ReferenceEquals(future, producer), "Future and Task are distinct handles");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try { _ = scope.Start(_ => ValueTask.FromResult(99L)); }
            catch (InvalidOperationException) { goto quota_verified; }
            throw new Exception("Producer quota not enforced before execution");
            quota_verified:;

            using var cancelView = new CancellationTokenSource();
            var cancelledObserver = future.ObserveAsync(cancelView.Token);
            var siblingObserver = future.ObserveAsync();
            Require(!ReferenceEquals(cancelledObserver, siblingObserver),
                "Each observer receives a separate projection");
            try { _ = future.ObserveAsync(); }
            catch (InvalidOperationException) { goto observer_quota_verified; }
            throw new Exception("Observer quota not enforced");
            observer_quota_verified:;

            cancelView.Cancel();
            try { _ = await cancelledObserver; }
            catch (OperationCanceledException) when (cancelledObserver.IsCanceled)
            { goto view_cancelled; }
            throw new Exception("Observer cancellation not isolated");
            view_cancelled:;

            Require(!producer.Retirement.IsCompleted,
                "Detaching an observer is not physical producer retirement");
            Require(scope.ActiveProducers == 1 && calls == 1,
                "Cancelled observer must not stop or replay producer");
            release.TrySetResult(42);
            Require(await siblingObserver == 42, "Independent sibling value");
            var retirement = await producer.Retirement;
            Require(retirement.ProducerId == producer.ProducerId &&
                retirement.TerminalState == "succeeded", "producer retirement receipt");
            Require(scope.ActiveProducers == 0 && scope.ActiveObservers == 0,
                "all accounted admissions retired");
            var late = future.ObserveAsync();
            Require(await late == 42 && scope.ProducerInvocations == 1,
                "late observer cannot replay producer");
        }

        await using (var scope = new OresTaskScope(1, 1))
        {
            var producer = scope.Start<long>(_ =>
                ValueTask.FromException<long>(new OperationCanceledException(
                    "user-fault-named-cancellation")));
            var view = scope.FromTask(producer).ObserveAsync();
            try { _ = await view; }
            catch (OperationCanceledException) when (view.IsFaulted)
            { goto authenticated; }
            throw new Exception("Cancellation-named user exception was laundered");
            authenticated:;
            Require((await producer.Retirement).TerminalState == "faulted",
                "fault must have faulted retirement terminal");
        }

        // I-02: Concurrent disposers must join the SAME physical producer
        // drain. A producer that ignores cancellation remains live, so a
        // merely closed queue cannot count as a retirement receipt.
        {
            var scope = new OresTaskScope(1, 2);
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<long>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var producer = scope.Start(async _ =>
            {
                entered.TrySetResult();
                return await release.Task.ConfigureAwait(false);
            });
            var observer = scope.FromTask(producer).ObserveAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task firstDisposal = scope.DisposeAsync().AsTask();
            Task secondDisposal = scope.DisposeAsync().AsTask();
            Require(!firstDisposal.IsCompleted && !secondDisposal.IsCompleted &&
                !producer.Retirement.IsCompleted && scope.ActiveProducers == 1,
                "closed scope must not masquerade as drained producer");
            try { _ = scope.Start(_ => ValueTask.FromResult(1L)); }
            catch (InvalidOperationException) { goto after_close; }
            throw new Exception("closed scope admitted a new producer");
            after_close:;
            release.TrySetResult(42);
            Require(await observer == 42, "successful producer not revoked by close");
            Require((await producer.Retirement).TerminalState == "succeeded",
                "successful producer retirement must survive concurrent close");
            await Task.WhenAll(firstDisposal, secondDisposal).WaitAsync(
                TimeSpan.FromSeconds(10));
            Require(scope.ActiveProducers == 0 && scope.ActiveObservers == 0,
                "both concurrent disposers joined physical producer drain");
        }

        // I-07: Scope cancellation is authenticated by its exact token.
        // A foreign cancelled token (or a tokenless user OCE) is never proof
        // that the producer obeyed scope cancellation.
        {
            var scope = new OresTaskScope(1, 1);
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var producer = scope.Start<long>(async _ =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                throw new OperationCanceledException(
                    "foreign cancellation", new CancellationToken(canceled: true));
            });
            var observer = scope.FromTask(producer).ObserveAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var draining = scope.DisposeAsync().AsTask();
            Require(!draining.IsCompleted, "dispose cannot pre-retire live producer");
            release.TrySetResult();
            try { _ = await observer; }
            catch (OperationCanceledException) when (observer.IsFaulted)
            { goto foreign_fault; }
            throw new Exception("foreign cancellation was laundered as scope cancellation");
            foreign_fault:;
            Require((await producer.Retirement).TerminalState == "faulted",
                "foreign cancellation must produce fault retirement");
            await draining.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // A body that does not need to call Dispose must not accidentally
        // self-join the one serialized pump and deadlock producer retirement.
        {
            var scope = new OresTaskScope(1, 1);
            var producer = scope.Start(async _ =>
            {
                try { await scope.DisposeAsync(); }
                catch (InvalidOperationException) { return 42L; }
                throw new Exception("producer incorrectly joined its own pump");
            });
            Require(await scope.FromTask(producer).ObserveAsync() == 42,
                "self-disposal must fail closed, without deadlocking");
            Require((await producer.Retirement).TerminalState == "succeeded",
                "self-join rejection preserves retirement");
            await scope.DisposeAsync();
        }

        // Authentic cancellation through the scope token is still terminal
        // cancellation rather than a user fault.
        {
            var scope = new OresTaskScope(1, 1);
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var producer = scope.Start<long>(async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return 42L;
            });
            var observer = scope.FromTask(producer).ObserveAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await scope.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            try { _ = await observer; }
            catch (OperationCanceledException) when (observer.IsCanceled)
            { goto verified_scope_cancellation; }
            throw new Exception("authenticated producer cancellation was not cancelled");
            verified_scope_cancellation:;
            Require((await producer.Retirement).TerminalState == "cancelled",
                "authenticated cancellation retirement marker");
        }

        await using (var left = new OresTaskScope(1, 1))
        await using (var right = new OresTaskScope(1, 1))
        {
            var producer = left.Start(_ => ValueTask.FromResult(42L));
            try { _ = right.FromTask(producer); }
            catch (InvalidOperationException) { goto no_cross_scope; }
            throw new Exception("Cross-scope foreign producer was admitted");
            no_cross_scope:;
            Require((await producer.Retirement).TerminalState == "succeeded",
                "cross-scope denial must not revoke producer");
        }
    }
}
