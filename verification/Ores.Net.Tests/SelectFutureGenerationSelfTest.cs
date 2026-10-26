using Ores.Net.Runtime;

internal static class SelectFutureGenerationSelfTest
{
    private static void Require(bool value, string reason)
    {
        if (!value)
            throw new Exception(
                "Select Future/generation regression: " + reason);
    }

    private static void FutureValueAndCancellationAreReady()
    {
        var future = new SelectableFutureI64();
        using var select = new SelectArbiter(1);
        var futureCase = select.RegisterCase(0);
        var outcome = default(SelectableFutureI64.Outcome);
        var callbacks = 0;

        var wait = future.SubscribeReady(subscription =>
        {
            callbacks++;
            _ = futureCase.TryWin(() =>
                future.TryCommit(subscription, out outcome));
        });
        futureCase.Arm(() => wait.Close());

        Require(select.Winner is null, "pending future keeps select pending");
        Require(future.Resolve(42), "first resolution wins");
        Require(!future.Resolve(43), "future is one-shot");
        Require(select.Winner == 0 && callbacks == 1,
            "future readiness wins once");
        Require(outcome.Kind == SelectableFutureI64.OutcomeKind.Value &&
                outcome.Value == 42,
            "future value preserved");

        var cancelled = new SelectableFutureI64();
        using var cancelledSelect = new SelectArbiter(1);
        var cancelledCase = cancelledSelect.RegisterCase(0);
        var cancelledOutcome = default(SelectableFutureI64.Outcome);

        var cancelledWait = cancelled.SubscribeReady(subscription =>
        {
            _ = cancelledCase.TryWin(() =>
                cancelled.TryCommit(subscription, out cancelledOutcome));
        });
        cancelledCase.Arm(() => cancelledWait.Close());

        Require(cancelled.Cancel(), "future cancellation settles");
        Require(cancelledSelect.Winner == 0, "cancelled future is ready");
        Require(cancelledOutcome.Kind ==
                SelectableFutureI64.OutcomeKind.Cancelled,
            "cancel outcome preserved");
    }

    private static void StaleGenerationCannotConsume()
    {
        var channel = new SelectableI64Channel(1);
        var fence = new ActorGenerationFence();
        var lease = fence.Capture();

        using var select = new SelectArbiter(1);
        var read = select.RegisterCase(0);
        var result = default(SelectableI64Channel.ReadResult);
        var callbacks = 0;

        var wait = channel.SubscribeReadable(subscription =>
        {
            callbacks++;
            var notifications = channel.CreateNotificationBatch();
            _ = read.TryWin(() => lease.TryRun(() =>
                channel.TryCommitRead(
                    subscription, out result, notifications)));
            notifications.Dispatch();
        });
        read.Arm(() => wait.Close());

        Require(fence.AdvanceGeneration() == 2,
            "restart advances generation");
        Require(!lease.IsCurrent, "old lease is stale");
        Require(channel.TryWrite(9), "producer publishes after restart");
        Require(callbacks == 1, "stale continuation may be notified");
        Require(select.Winner is null &&
                result.Status ==
                    SelectableI64Channel.ReadStatus.WouldBlock,
            "stale continuation cannot win or consume");

        var untouched = channel.TryReadResult();
        Require(untouched.Status ==
                    SelectableI64Channel.ReadStatus.Value &&
                untouched.Value == 9,
            "stale continuation left value untouched");
        Require(select.Cancel(), "cleanup stale select");
        Require(channel.PendingReaders == 0, "cleanup detaches stale waiter");
    }

    private static void CurrentGenerationMayCommit()
    {
        var channel = new SelectableI64Channel(1);
        var fence = new ActorGenerationFence();
        var lease = fence.Capture();

        using var select = new SelectArbiter(1);
        var read = select.RegisterCase(0);
        var result = default(SelectableI64Channel.ReadResult);

        var wait = channel.SubscribeReadable(subscription =>
        {
            var notifications = channel.CreateNotificationBatch();
            _ = read.TryWin(() => lease.TryRun(() =>
                channel.TryCommitRead(
                    subscription, out result, notifications)));
            notifications.Dispatch();
        });
        read.Arm(() => wait.Close());

        Require(lease.IsCurrent, "fresh lease");
        Require(channel.TryWrite(11), "producer write");
        Require(select.Winner == 0 &&
                result.Status ==
                    SelectableI64Channel.ReadStatus.Value &&
                result.Value == 11,
            "current generation may consume");
    }

    private static void GenerationFenceRejectsReentrantAdvance()
    {
        var fence = new ActorGenerationFence();
        var lease = fence.Capture();

        var observed = false;
        try
        {
            _ = lease.TryRun(() =>
            {
                _ = fence.AdvanceGeneration();
                return true;
            });
        }
        catch (InvalidOperationException)
        {
            observed = true;
        }

        Require(observed, "reentrant generation advance rejected");
        Require(fence.Generation == 1,
            "rejected reentrant advance leaves generation unchanged");
    }

    public static void Run()
    {
        FutureValueAndCancellationAreReady();
        StaleGenerationCannotConsume();
        CurrentGenerationMayCommit();
        GenerationFenceRejectsReentrantAdvance();
    }
}
