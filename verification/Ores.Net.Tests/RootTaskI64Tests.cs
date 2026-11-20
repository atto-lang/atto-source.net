using Ores.Net.Runtime;

internal static class RootTaskI64Tests
{
    internal static async Task Run()
    {
        static void Require(bool condition, string reason)
        { if (!condition) throw new Exception("Root Task-i64: " + reason); }
        var receipt = await RootTaskI64.RunAsync(() => 42).WaitAsync(TimeSpan.FromSeconds(10));
        Require(receipt.Value == 42 && receipt.ProducerExecutions == 1 &&
            receipt.ObserverDistinct && receipt.ProducerBodyRetired, "root result/lifecycle");

        // Gate the one root carrier to make preclaim cancellation deterministic.
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = RootTaskI64.Start(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return 1;
        });
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(10)), "carrier did not start");
            var cancelled = RootTaskI64.Start(() => throw new Exception("cancelled producer ran"));
            var future = cancelled.Observe();
            Require(cancelled.CancelBeforeClaim(), "owner cancellation rejected before claim");
            Require(!cancelled.PhysicalDone.IsCompleted, "queued cancellation forged physical retirement");
            var queued = Enumerable.Range(0, 62).Select(_ => RootTaskI64.Start(() => 7)).ToArray();
            bool quotaRejected = false;
            try { _ = RootTaskI64.Start(() => 99); }
            catch (InvalidOperationException error) when (error.Message.Contains("quota"))
            { quotaRejected = true; }
            Require(quotaRejected, "65th live root producer bypassed quota");
            foreach (var pending in queued) Require(pending.CancelBeforeClaim(), "queued claim raced blocked carrier");
            release.Set();
            Task<long> cancelledResult = future.AwaitAsync();
            try { await cancelledResult.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (cancelledResult.IsCanceled) { }
            await cancelled.PhysicalDone.WaitAsync(TimeSpan.FromSeconds(10));
            Require(cancelledResult.IsCanceled && cancelled.ProducerExecutions == 0, "unauthenticated cancellation");
            foreach (var pending in queued)
            {
                await pending.PhysicalDone.WaitAsync(TimeSpan.FromSeconds(10));
                Require(pending.ProducerExecutions == 0, "cancelled quota occupant executed");
            }
        }
        finally { release.Set(); await blocker.PhysicalDone.WaitAsync(TimeSpan.FromSeconds(10)); }

        entered.Reset(); release.Reset();
        var owner = RootTaskI64.Start(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return 19;
        });
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(10)), "owner did not enter body");
            var future = owner.Observe();
            using var cancellation = new CancellationTokenSource();
            Task<long> observer = future.AwaitAsync(cancellation.Token);
            cancellation.Cancel();
            try { await observer.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (observer.IsCanceled) { }
            Require(observer.IsCanceled && !owner.PhysicalDone.IsCompleted,
                "observer cancellation retired running producer");
            Require(!owner.CancelBeforeClaim(), "running producer accepted preclaim cancellation");
        }
        finally { release.Set(); await owner.PhysicalDone.WaitAsync(TimeSpan.FromSeconds(10)); }
        Require(owner.ProducerExecutions == 1, "observer replayed producer");

        var userFailure = new OperationCanceledException("user failure");
        var failedOwner = RootTaskI64.Start(() => throw userFailure);
        Task<long> failedView = failedOwner.Observe().AwaitAsync();
        try { await failedView; }
        catch (OperationCanceledException error) { Require(ReferenceEquals(error, userFailure), "failure cause changed"); }
        await failedOwner.PhysicalDone;
        Require(failedView.IsFaulted && !failedView.IsCanceled, "user failure became CANCELLED");
        var failedRoot = RootTaskI64.RunAsync(() => throw userFailure);
        try { await failedRoot; } catch (OperationCanceledException) { }
        Require(failedRoot.IsFaulted, "root scope reclassified cancellation-named fault");
    }
}
