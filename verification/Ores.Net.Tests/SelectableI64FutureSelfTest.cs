using Ores.Net.Runtime;

internal static class SelectableI64FutureSelfTest
{
    private static void Require(bool ok, string reason)
    {
        if (!ok) throw new Exception("Future select regression: " + reason);
    }

    private static void ResolutionSelectsOnce()
    {
        var future = new SelectableI64Future();
        using var select = new SelectArbiter(1);
        var registration = select.RegisterCase(0);
        SelectableI64Future.Receipt receipt = default;
        var token = future.SubscribeReady(s =>
        {
            Require(registration.TryWin(() => future.TryCommitReady(s, out receipt)),
                "future commits ready winner");
        });
        registration.Arm(() => token.Close());
        Require(select.Winner is null && future.PendingWaiters == 1,
            "pending future leaves select pending");
        Require(future.Resolve(42) && !future.Resolve(43), "resolve exactly once");
        Require(select.Winner == 0 && receipt.Status ==
            SelectableI64Future.FutureStatus.Resolved && receipt.Value == 42,
            "resolved i64 value delivered");
        Require(future.PendingWaiters == 0, "winner revokes readiness slot");
    }

    private static void RejectionAndCancellationHaveDistinctOutcomes()
    {
        foreach (var rejected in new[] { true, false })
        {
            var future = new SelectableI64Future();
            using var select = new SelectArbiter(1);
            var registration = select.RegisterCase(0);
            SelectableI64Future.Receipt receipt = default;
            var token = future.SubscribeReady(s =>
            {
                Require(registration.TryWin(() => future.TryCommitReady(s, out receipt)),
                    "terminal future wins");
            });
            registration.Arm(() => token.Close());
            if (rejected)
            {
                Require(future.Reject(new InvalidOperationException("failed")), "reject");
                Require(receipt.Status == SelectableI64Future.FutureStatus.Rejected &&
                    receipt.Error is InvalidOperationException, "rejection receipt");
            }
            else
            {
                Require(future.Cancel(), "cancel future");
                Require(receipt.Status == SelectableI64Future.FutureStatus.Cancelled &&
                    receipt.Error is null, "cancellation receipt");
            }
        }
    }

    private static void CancelledWaiterDoesNotReenterSelect()
    {
        var future = new SelectableI64Future();
        using var select = new SelectArbiter(1);
        var registration = select.RegisterCase(0);
        var callbacks = 0;
        var token = future.SubscribeReady(_ => callbacks++);
        registration.Arm(() => token.Close());
        Require(select.Cancel(), "select cancelled");
        Require(future.PendingWaiters == 0, "waiter detached");
        Require(future.Resolve(9), "future later resolved");
        Require(callbacks == 0 && select.Winner is null,
            "cancelled waiter never wins");
    }

    private static void RetiredActorCannotResumeStaleFuture()
    {
        var fence = new ActorGenerationFence();
        var captured = fence.Capture();
        var future = new SelectableI64Future();
        using var select = new SelectArbiter(1);
        var registration = select.RegisterCase(0);
        SelectableI64Future.Receipt receipt = default;
        var token = future.SubscribeReady(s =>
        {
            _ = registration.TryWin(() =>
                captured.Current && future.TryCommitReady(s, out receipt));
        });
        registration.Arm(() => token.Close());
        Require(fence.Retire() && !captured.Current, "retired token is stale");
        Require(future.Resolve(77), "future settles after actor retired");
        Require(select.Winner is null &&
            receipt.Status == SelectableI64Future.FutureStatus.Pending,
            "stale continuation cannot commit");
        Require(fence.PhysicallyRetired, "no callback lease alive");
        Require(select.Cancel(), "select cleans stale waiter");
    }

    private static void ActiveContinuationBlocksPhysicalRetirement()
    {
        var fence = new ActorGenerationFence();
        var token = fence.Capture();
        Require(token.RunIfCurrent(() =>
        {
            Require(fence.Retire(), "logical actor retirement");
            Require(!fence.PhysicallyRetired, "physical retirement held by lease");
        }), "continuation accepted before retirement");
        Require(fence.PhysicallyRetired, "physical retirement after lease exit");
        var invoked = false;
        Require(!token.RunIfCurrent(() => invoked = true), "old token blocked");
        Require(!invoked, "stale callback suppressed");
    }

    private static void CallbackExceptionsCannotUnsettleFuture()
    {
        var future = new SelectableI64Future();
        var token = future.SubscribeReady(_ => throw new InvalidOperationException("observer failed"));
        Require(future.Resolve(6), "future resolves despite observer error");
        Require(future.Status == SelectableI64Future.FutureStatus.Resolved &&
            future.NotificationFailure is InvalidOperationException,
            "callback failure captured after settlement");
        Require(token.Close(), "failed observer still revocable");
    }

    public static void Run()
    {
        ResolutionSelectsOnce();
        RejectionAndCancellationHaveDistinctOutcomes();
        CancelledWaiterDoesNotReenterSelect();
        RetiredActorCannotResumeStaleFuture();
        ActiveContinuationBlocksPhysicalRetirement();
        CallbackExceptionsCannotUnsettleFuture();
    }
}
