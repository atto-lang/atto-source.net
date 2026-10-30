using Ores.Net.Runtime;

internal static class ExecutionCallbackContextTests
{
    internal static void Run()
    {
        static void Require(bool condition, string reason)
        {
            if (!condition) throw new Exception("Execution callback context: " + reason);
        }

        var active = new OresExecutionContext(
            runtimeDomain: 17,
            execution: 41,
            dispatch: 7,
            generation: 3,
            parentExecution: 11,
            continuation: 5);

        OresExecutionCallbackContext captured;
        using (var activeScope = OresExecutionContexts.EnterActive(active))
        {
            Require(OresExecutionContexts.Active?.Execution == 41,
                "active execution scope missing");
            captured = OresExecutionContexts.CaptureCallback(
                OresCallbackBoundary.HostCallback)!.Value;
            Require(captured.Execution == 41 && captured.Dispatch == 7,
                "capture lost source identity");
        }
        Require(OresExecutionContexts.Active is null,
            "active scope leaked after dispose");

        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Require(OresExecutionContexts.Active is null,
                    "active execution flowed to foreign thread");
                Require(OresExecutionContexts.Callback is null,
                    "callback provenance flowed implicitly to foreign thread");

                OresExecutionContexts.RunCallback(captured, () =>
                {
                    Require(OresExecutionContexts.Active is null,
                        "callback provenance granted active execution authority");
                    Require(OresExecutionContexts.Callback?.Execution == 41,
                        "explicit callback scope missing");
                });

                Require(OresExecutionContexts.Callback is null,
                    "callback scope leaked on foreign thread");
            }
            catch (Exception error)
            {
                workerFailure = error;
            }
        });
        worker.Start();
        worker.Join();
        if (workerFailure is not null) throw workerFailure;

        var outer = captured;
        OresExecutionCallbackContext inner;
        OresExecutionContexts.RunCallback(outer, () =>
        {
            inner = OresExecutionContexts.CaptureCallback(
                OresCallbackBoundary.Timer)!.Value;
            Require(inner.Execution == outer.Execution,
                "nested capture lost lineage");
            Require(inner.Boundary == OresCallbackBoundary.Timer,
                "nested capture did not replace boundary");
            try
            {
                OresExecutionContexts.RunCallback(inner, () =>
                {
                    Require(OresExecutionContexts.Callback?.Boundary ==
                        OresCallbackBoundary.Timer, "inner scope missing");
                    throw new InvalidOperationException("boom");
                });
            }
            catch (InvalidOperationException error) when (error.Message == "boom")
            {
            }
            Require(OresExecutionContexts.Callback?.Boundary ==
                OresCallbackBoundary.HostCallback,
                "nested failure did not restore outer scope");
        });
        Require(OresExecutionContexts.Callback is null,
            "outer scope leaked after nested failure");

        var callbackOuterScope = OresExecutionContexts.EnterCallback(captured);
        var callbackInnerScope = OresExecutionContexts.EnterCallback(captured);
        bool callbackOutOfOrderRejected = false;
        try
        {
            callbackOuterScope.Dispose();
        }
        catch (InvalidOperationException)
        {
            callbackOutOfOrderRejected = true;
        }
        Require(callbackOutOfOrderRejected,
            "callback scopes admitted out-of-order disposal");
        Require(OresExecutionContexts.Callback?.Execution == captured.Execution,
            "failed out-of-order callback dispose corrupted current scope");
        callbackInnerScope.Dispose();
        callbackOuterScope.Dispose();
        Require(OresExecutionContexts.Callback is null,
            "callback LIFO cleanup leaked provenance");

        var activeOuterScope = OresExecutionContexts.EnterActive(active);
        var activeInnerScope = OresExecutionContexts.EnterActive(active);
        bool activeOutOfOrderRejected = false;
        try
        {
            activeOuterScope.Dispose();
        }
        catch (InvalidOperationException)
        {
            activeOutOfOrderRejected = true;
        }
        Require(activeOutOfOrderRejected,
            "active scopes admitted out-of-order disposal");
        Require(OresExecutionContexts.Active?.Execution == active.Execution,
            "failed out-of-order active dispose corrupted current authority");
        activeInnerScope.Dispose();
        activeOuterScope.Dispose();
        Require(OresExecutionContexts.Active is null,
            "active LIFO cleanup leaked execution authority");

        bool invalid = false;
        try
        {
            _ = OresExecutionContexts.CaptureCallback((OresCallbackBoundary)99);
        }
        catch (ArgumentOutOfRangeException)
        {
            invalid = true;
        }
        Require(invalid, "invalid callback boundary admitted");

        var future = new SelectableI64Future();
        SelectableI64Future.Subscription? subscription = null;
        OresExecutionCallbackContext? observed = null;
        using (var activeScope = OresExecutionContexts.EnterActive(active))
        {
            subscription = future.SubscribeReady(s =>
            {
                observed = OresExecutionContexts.Callback;
                Require(OresExecutionContexts.Active is null,
                    "producer callback inherited active execution");
                Require(future.TryCommit(s, out var result) &&
                    result.Status == SelectableI64Future.FutureStatus.Value &&
                    result.Value == 42, "future callback commit failed");
            });
        }

        var producer = new Thread(() =>
        {
            Require(OresExecutionContexts.Active is null,
                "producer thread has unexpected active execution");
            Require(future.Resolve(42), "future did not settle");
            Require(OresExecutionContexts.Callback is null,
                "future callback provenance leaked after resolution");
        });
        producer.Start();
        producer.Join();

        Require(observed?.Execution == 41 &&
            observed?.Boundary == OresCallbackBoundary.HostCallback,
            "future readiness callback lost captured provenance");
        Require(subscription is not null && !subscription.Active,
            "future callback did not retire subscription");
    }
}
