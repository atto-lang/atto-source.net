using Ores.Net.Runtime;

internal static class HostTaskObservationTests
{
    internal static async Task Run()
    {
        static TaskCompletionSource<int> Producer() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        static void Require(bool condition, string reason)
        {
            if (!condition) throw new Exception("Host observation: " + reason);
        }
        static async Task Drained(HostTaskObservationScope scope)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (scope.PendingObservations != 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        var scope = new HostTaskObservationScope(1);
        bool invalidCapacity = false;
        try { _ = new HostTaskObservationScope(0); }
        catch (ArgumentOutOfRangeException) { invalidCapacity = true; }
        Require(invalidCapacity, "nonpositive quota admitted");
        foreach (int value in new[] { 0, 1, 42 })
        {
            Task<int> source = Task.FromResult(value);
            Task<int> view = scope.Observe(source);
            Require(!ReferenceEquals(source, view), "ready/cached result identity leaked");
            Require(await view.WaitAsync(TimeSpan.FromSeconds(10)) == value, "ready value changed");
            await Drained(scope);
        }
        Task<string?> nullable = Task.FromResult<string?>(null);
        Require(await scope.Observe(nullable) is null, "null success became absent/error");
        await Drained(scope);

        var producer = Producer();
        using (var observerCancellation = new CancellationTokenSource())
        {
            var view = scope.Observe(producer.Task, observerCancellation.Token);
            Require(!ReferenceEquals(view, producer.Task), "pending producer identity leaked");
            observerCancellation.Cancel();
            try { await view.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (view.IsCanceled) { }
            Require(view.IsCanceled && !producer.Task.IsCompleted,
                "observer cancellation altered independent producer");
            await Drained(scope);
            // Cancelled registration releases quota while producer is STILL pending.
            Require(await scope.Observe(Task.FromResult(7)) == 7, "cancel stranded quota");
            producer.SetResult(42);
            Require(await producer.Task == 42, "producer could not complete after detach");
        }
        await Drained(scope);

        using (var sourceCancellation = new CancellationTokenSource())
        {
            sourceCancellation.Cancel();
            var source = Task.FromCanceled<int>(sourceCancellation.Token);
            var view = scope.Observe(source);
            try { await view; }
            catch (OperationCanceledException error)
            {
                Require(view.IsCanceled, "authenticated cancellation became ordinary failure");
                Require(error.CancellationToken != sourceCancellation.Token,
                    "source cancellation token escaped through observation");
            }
            Require(view.IsCanceled, "cancelled source became successful");
        }
        await Drained(scope);

        var userError = new OperationCanceledException("ordinary user error");
        var faulted = scope.Observe(Task.FromException<int>(userError));
        try { await faulted; }
        catch (OperationCanceledException error)
        {
            Require(ReferenceEquals(error, userError) && faulted.IsFaulted && !faulted.IsCanceled,
                "cancellation-named user failure was reclassified");
        }
        Require(faulted.IsFaulted, "user failure became successful");
        await Drained(scope);
        var causes = Producer();
        var first = new InvalidOperationException("first");
        var second = new ArgumentException("second");
        causes.SetException(new Exception[] { first, second });
        var multi = scope.Observe(causes.Task);
        try { await multi; } catch (InvalidOperationException) { }
        var failures = multi.Exception?.InnerExceptions;
        Require(failures is { Count: 2 } &&
            ReferenceEquals(failures[0], first) &&
            ReferenceEquals(failures[1], second), "failure causes changed");
        await Drained(scope);

        producer = Producer();
        var pending = scope.Observe(producer.Task);
        bool rejected = false;
        try { _ = scope.Observe(Task.FromResult(99)); }
        catch (InvalidOperationException error) when (error.Message.Contains("quota"))
        { rejected = true; }
        Require(rejected && scope.PendingObservations == 1, "quota failed before registration");
        producer.SetResult(12);
        Require(await pending == 12, "quota rejection damaged admitted waiter");
        await Drained(scope);

        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            producer = Producer();
            var view = scope.Observe(producer.Task, alreadyCancelled.Token);
            try { await view; } catch (OperationCanceledException) { }
            Require(view.IsCanceled && !producer.Task.IsCompleted,
                "pre-cancelled observer modified source");
            await Drained(scope);
            producer.SetResult(3);
        }

        // Two views share a producer but have independent cancellation rights.
        var independentScope = new HostTaskObservationScope(2);
        producer = Producer();
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelledView = independentScope.Observe(producer.Task, cancellation.Token);
            var survivingView = independentScope.Observe(producer.Task);
            cancellation.Cancel();
            try { await cancelledView.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (cancelledView.IsCanceled) { }
            Require(cancelledView.IsCanceled && !survivingView.IsCompleted &&
                !producer.Task.IsCompleted, "one observer revoked another observer or producer");
            producer.SetResult(19);
            Require(await survivingView.WaitAsync(TimeSpan.FromSeconds(10)) == 19,
                "independent observer lost producer result");
            await Drained(independentScope);
        }

        // Cancellation after success cannot change an already-published outcome.
        using (var lateCancellation = new CancellationTokenSource())
        {
            var view = scope.Observe(Task.FromResult(23), lateCancellation.Token);
            Require(await view == 23, "late cancellation setup");
            lateCancellation.Cancel();
            Require(view.IsCompletedSuccessfully && await view == 23,
                "late cancellation rewrote a terminal result");
            await Drained(scope);
        }

        // Race admission itself: exactly capacity permits are issued while all
        // admitted observations remain pending, not merely at a sampled instant.
        var concurrentScope = new HostTaskObservationScope(4);
        producer = Producer();
        using (var stopObserving = new CancellationTokenSource())
        {
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                try { return concurrentScope.Observe(producer.Task, stopObserving.Token); }
                catch (InvalidOperationException error) when (error.Message.Contains("quota"))
                { return (Task<int>?)null; }
            })).ToArray();
            start.SetResult(true);
            Task<int>?[] observations = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));
            Require(observations.Count(view => view is not null) == 4 &&
                concurrentScope.PendingObservations == 4, "concurrent quota oversubscribed or lost permits");
            stopObserving.Cancel();
            foreach (var view in observations.OfType<Task<int>>())
            {
                try { await view.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) when (view.IsCanceled) { }
                Require(view.IsCanceled, "concurrent observer did not cancel");
            }
            await Drained(concurrentScope);
            Require(!producer.Task.IsCompleted, "quota cleanup cancelled independent producer");
            producer.SetResult(31);
        }

        // Host race coverage only: neither task here is an Oreslang actor/Task.
        for (int i = 0; i < 128; i++)
        {
            producer = Producer();
            using var cancellation = new CancellationTokenSource();
            var view = scope.Observe(producer.Task, cancellation.Token);
            int expected = i;
            await Task.WhenAll(Task.Run(() => producer.SetResult(expected)),
                Task.Run(() => cancellation.Cancel())).WaitAsync(TimeSpan.FromSeconds(10));
            try { Require(await view.WaitAsync(TimeSpan.FromSeconds(10)) == expected, "race value"); }
            catch (OperationCanceledException) when (view.IsCanceled) { }
            Require(await producer.Task == expected && producer.Task.IsCompletedSuccessfully,
                "racing observer cancelled producer");
            await Drained(scope);
        }
    }
}
