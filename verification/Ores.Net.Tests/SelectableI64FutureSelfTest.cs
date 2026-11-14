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
using Ores.Net.Runtime;

internal static class SelectableI64FutureSelfTest
{
    private static void Require(bool value, string reason)
    {
        if (!value) throw new Exception("Selectable Future regression: " + reason);
    }

    private static SelectableI64Future.Subscription Bind(
        SelectableI64Future future,
        SelectArbiter.Registration registration,
        Action<SelectableI64Future.Result> result,
        Action? callback = null)
    {
        return future.SubscribeReady(subscription =>
        {
            callback?.Invoke();
            SelectableI64Future.Result outcome = default;
            var won = registration.TryWin(() =>
                future.TryCommit(subscription, out outcome));
            if (won) result(outcome);
        });
    }

    private static void PendingResolveWinsWithValue()
    {
        var future = new SelectableI64Future();
        using var select = new SelectArbiter(1);
        var arm = select.RegisterCase(0);
        SelectableI64Future.Result result = default;
        var callbacks = 0;
        var wait = Bind(future, arm, r => result = r, () => callbacks++);
        arm.Arm(() => wait.Close());

        Require(select.Winner is null, "pending Future keeps select pending");
        Require(future.Resolve(42), "first resolution succeeds");
        Require(select.Winner == 0, "resolved Future wins");
        Require(callbacks == 1, "one readiness callback");
        Require(result.Status == SelectableI64Future.FutureStatus.Value &&
                result.Value == 42,
            "winning result carries value");
        Require(future.PendingWaiters == 0, "winning subscription closes");
    }

    private static void CompletionIsImmutableAndNonConsuming()
    {
        var future = new SelectableI64Future();
        Require(future.Resolve(7), "initial completion");
        Require(!future.Resolve(8), "second resolution rejected");
        Require(!future.Cancel(), "terminal Future cannot cancel");

        for (var i = 0; i < 2; i++)
        {
            using var select = new SelectArbiter(1);
            var arm = select.RegisterCase(0);
            SelectableI64Future.Result result = default;
            var wait = Bind(future, arm, r => result = r);
            arm.Arm(() => wait.Close());
            Require(select.Winner == 0, "terminal Future immediately ready");
            Require(result.Status == SelectableI64Future.FutureStatus.Value &&
                    result.Value == 7,
                "independent observer sees same immutable value");
        }
    }

    private static void StaleSameFutureNotificationCannotWinLoser()
    {
        var future = new SelectableI64Future();
        using var select = new SelectArbiter(2);
        var first = select.RegisterCase(0);
        var second = select.RegisterCase(1);
        var firstCallbacks = 0;
        var secondCallbacks = 0;
        var sideEffects = 0;

        var firstWait = Bind(future, first,
            _ => sideEffects++, () => firstCallbacks++);
        var secondWait = Bind(future, second,
            _ => sideEffects += 100, () => secondCallbacks++);
        first.Arm(() => firstWait.Close());
        second.Arm(() => secondWait.Close());

        Require(future.Resolve(9), "completion");
        Require(select.Winner == 0, "first registration wins");
        Require(firstCallbacks == 1 && secondCallbacks == 1,
            "captured stale loser callback may execute");
        Require(sideEffects == 1,
            "stale losing callback cannot run arm side effect");
        Require(future.PendingWaiters == 0, "winner and loser detached");
    }

    private static void SelectCancellationRevokesSubscription()
    {
        var future = new SelectableI64Future();
        using var select = new SelectArbiter(1);
        var arm = select.RegisterCase(0);
        var callbacks = 0;
        var wait = Bind(future, arm, _ => { }, () => callbacks++);
        arm.Arm(() => wait.Close());

        Require(future.PendingWaiters == 1, "waiter registered");
        Require(select.Cancel(), "select cancellation");
        Require(future.PendingWaiters == 0, "waiter revoked");
        Require(future.Resolve(5), "Future may complete after select cancel");
        Require(callbacks == 0, "revoked callback never fires");
    }

    private static void CancellationAndFailureAreTerminalOutcomes()
    {
        var cancelled = new SelectableI64Future();
        using (var select = new SelectArbiter(1))
        {
            var arm = select.RegisterCase(0);
            SelectableI64Future.Result result = default;
            var wait = Bind(cancelled, arm, r => result = r);
            arm.Arm(() => wait.Close());
            Require(cancelled.Cancel(), "Future cancellation");
            Require(select.Winner == 0 &&
                    result.Status == SelectableI64Future.FutureStatus.Cancelled,
                "cancelled Future is selectable");
        }

        var failed = new SelectableI64Future();
        using (var select = new SelectArbiter(1))
        {
            var arm = select.RegisterCase(0);
            SelectableI64Future.Result result = default;
            var wait = Bind(failed, arm, r => result = r);
            arm.Arm(() => wait.Close());
            var failure = new InvalidOperationException("boom");
            Require(failed.Fail(failure), "Future failure");
            Require(select.Winner == 0 &&
                    result.Status == SelectableI64Future.FutureStatus.Failed &&
                    ReferenceEquals(result.Error, failure),
                "failed Future preserves failure");
        }
    }

    private static void NotificationFailureIsRecorded()
    {
        var future = new SelectableI64Future(2);
        var wait = future.SubscribeReady(_ =>
            throw new InvalidOperationException("notify failed"));
        Require(future.Resolve(1), "completion survives callback failure");
        Require(future.NotificationFailure is InvalidOperationException,
            "notification failure retained");
        Require(wait.Close(), "failed callback waiter remains revocable");
        Require(future.PendingWaiters == 0, "failed waiter detached");
    }

    private static void ForeignSubscriptionFailsClosed()
    {
        var first = new SelectableI64Future();
        var second = new SelectableI64Future();
        var foreign = first.SubscribeReady(_ => { });
        Require(!second.TryCommit(foreign, out _),
            "foreign subscription cannot commit");
        Require(foreign.Close(), "source subscription remains revocable");
    }

    public static void Run()
    {
        PendingResolveWinsWithValue();
        CompletionIsImmutableAndNonConsuming();
        StaleSameFutureNotificationCannotWinLoser();
        SelectCancellationRevokesSubscription();
        CancellationAndFailureAreTerminalOutcomes();
        NotificationFailureIsRecorded();
        ForeignSubscriptionFailsClosed();
    }
}
