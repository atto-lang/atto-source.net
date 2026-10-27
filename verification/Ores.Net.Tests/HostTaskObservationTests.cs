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
