using Ores.Net.Runtime;

internal static class SelectableI64ChannelSelfTest
{
    private static void Require(bool value, string reason)
    {
        if (!value) throw new Exception("Selectable channel regression: " + reason);
    }

    private static SelectableI64Channel.Subscription BindRead(
        SelectableI64Channel channel,
        SelectArbiter.Registration registration,
        Action<long> result,
        Action callback,
        Action<SelectableI64Channel.ReadOutcome>? outcome = null)
    {
        return channel.SubscribeReadable(subscription =>
        {
            callback();
            var notifications = channel.CreateNotificationBatch();
            SelectableI64Channel.ReadResult committed = default;
            var won = registration.TryWin(() =>
                channel.TryCommitRead(subscription, out committed, notifications));
            notifications.Dispatch();
            if (!won) return;
            outcome?.Invoke(committed.Outcome);
            if (committed.HasValue) result(committed.Value);
        });
    }

    private static SelectableI64Channel.Subscription BindWrite(
        SelectableI64Channel channel,
        SelectArbiter.Registration registration,
        long value,
        Action callback,
        Action<SelectableI64Channel.WriteOutcome>? outcome = null)
    {
        return channel.SubscribeWritable(subscription =>
        {
            callback();
            var notifications = channel.CreateNotificationBatch();
            SelectableI64Channel.WriteOutcome committed = default;
            var won = registration.TryWin(() =>
                channel.TryCommitWrite(
                    subscription, value, out committed, notifications));
            notifications.Dispatch();
            if (won) outcome?.Invoke(committed);
        });
    }

    private static void FirstReadyReadWinsWithoutConsumingLoser()
    {
        var firstChannel = new SelectableI64Channel(2);
        var secondChannel = new SelectableI64Channel(2);
        Require(firstChannel.TryWrite(11), "seed first");
        Require(secondChannel.TryWrite(22), "seed second");

        using var select = new SelectArbiter(2);
        var first = select.RegisterCase(0);
        var second = select.RegisterCase(1);
        long firstValue = 0;
        long secondValue = 0;
        var firstCallbacks = 0;
        var secondCallbacks = 0;

        var firstWait = BindRead(firstChannel, first,
            value => firstValue = value, () => firstCallbacks++);
        first.Arm(() => firstWait.Close());

        var secondWait = BindRead(secondChannel, second,
            value => secondValue = value, () => secondCallbacks++);
        second.Arm(() => secondWait.Close());

        Require(select.Winner == 0, "first ready read should win priority registration order");
        Require(firstValue == 11, "winning read value");
        Require(firstCallbacks == 1 && secondCallbacks == 1,
            "both immediate readiness callbacks observed");
        Require(firstChannel.Count == 0, "winner consumed exactly once");
        Require(secondChannel.Count == 1, "loser did not consume");
        Require(firstChannel.PendingReaders == 0 &&
                secondChannel.PendingReaders == 0,
            "loser readiness registration detached");

        Require(secondChannel.TryRead(out var untouched) && untouched == 22,
            "losing value remains available");
        Require(secondValue == 0, "losing binding never assigned");
    }

    private static void LosingWriteNeverPublishes()
    {
        var firstChannel = new SelectableI64Channel(2);
        var secondChannel = new SelectableI64Channel(2);
        using var select = new SelectArbiter(2);
        var first = select.RegisterCase(0);
        var second = select.RegisterCase(1);
        var firstCallbacks = 0;
        var secondCallbacks = 0;

        var firstWait = BindWrite(firstChannel, first, 31, () => firstCallbacks++);
        first.Arm(() => firstWait.Close());

        var secondWait = BindWrite(secondChannel, second, 32, () => secondCallbacks++);
        second.Arm(() => secondWait.Close());

        Require(select.Winner == 0, "first ready write wins");
        Require(firstCallbacks == 1 && secondCallbacks == 1,
            "both immediate write callbacks observed");
        Require(firstChannel.Count == 1, "winning write published");
        Require(secondChannel.Count == 0, "losing write never published");
        Require(firstChannel.PendingWriters == 0 &&
                secondChannel.PendingWriters == 0,
            "write waiters detached");

        Require(firstChannel.TryRead(out var committed) && committed == 31,
            "winning payload preserved");
        Require(!secondChannel.TryRead(out _), "losing payload absent");
    }

    private static void DelayedReadinessWakesPendingRead()
    {
        var firstChannel = new SelectableI64Channel(1);
        var secondChannel = new SelectableI64Channel(1);
        using var select = new SelectArbiter(2);
        var first = select.RegisterCase(0);
        var second = select.RegisterCase(1);
        long firstValue = 0;
        long secondValue = 0;
        var firstCallbacks = 0;
        var secondCallbacks = 0;

        var firstWait = BindRead(firstChannel, first,
            value => firstValue = value, () => firstCallbacks++);
        var secondWait = BindRead(secondChannel, second,
            value => secondValue = value, () => secondCallbacks++);
        first.Arm(() => firstWait.Close());
        second.Arm(() => secondWait.Close());

        Require(select.Winner is null, "empty channels leave select pending");
        Require(firstChannel.PendingReaders == 1 &&
                secondChannel.PendingReaders == 1,
            "both pending readers registered");

        Require(secondChannel.TryWrite(42), "producer write");
        Require(select.Winner == 1 && secondValue == 42,
            "second channel readiness wins and binds value");
        Require(firstValue == 0 && firstCallbacks == 0 && secondCallbacks == 1,
            "only ready callback ran");
        Require(firstChannel.PendingReaders == 0 &&
                secondChannel.PendingReaders == 0,
            "winner and loser registrations closed");

        Require(firstChannel.TryWrite(9), "later first-channel write");
        Require(firstCallbacks == 0,
            "detached loser callback never resurrected");
        Require(firstChannel.TryRead(out var untouched) && untouched == 9,
            "late loser channel value remains untouched");
    }

    private static void FreeingCapacityWakesPendingWrite()
    {
        var channel = new SelectableI64Channel(1);
        Require(channel.TryWrite(7), "fill channel");

        using var select = new SelectArbiter(1);
        var write = select.RegisterCase(0);
        var callbacks = 0;
        var wait = BindWrite(channel, write, 8, () => callbacks++);
        write.Arm(() => wait.Close());

        Require(select.Winner is null && channel.PendingWriters == 1,
            "full channel keeps write pending");

        Require(channel.TryRead(out var existing) && existing == 7,
            "read frees capacity");
        Require(select.Winner == 0 && callbacks == 1,
            "pending write wakes and wins");
        Require(channel.PendingWriters == 0,
            "winning write registration closes");
        Require(channel.TryRead(out var committed) && committed == 8,
            "winning write committed after capacity release");
    }

    private static void CancellationDetachesWaiter()
    {
        var channel = new SelectableI64Channel(1);
        using var select = new SelectArbiter(1);
        var read = select.RegisterCase(0);
        long value = 0;
        var callbacks = 0;

        var wait = BindRead(channel, read,
            v => value = v, () => callbacks++);
        read.Arm(() => wait.Close());
        Require(channel.PendingReaders == 1, "reader registered");

        Require(select.Cancel(), "select cancellation");
        Require(channel.PendingReaders == 0, "cancel detached channel waiter");
        Require(channel.TryWrite(55), "post-cancel producer");
        Require(callbacks == 0 && value == 0,
            "cancelled waiter never receives later readiness");
    }

    private static void StaleNotificationCannotConsumeForLoser()
    {
        var channel = new SelectableI64Channel(1);
        using var select = new SelectArbiter(2);
        var read = select.RegisterCase(0);
        var write = select.RegisterCase(1);
        long readValue = 0;
        var readCallbacks = 0;
        var writeCallbacks = 0;

        var readWait = BindRead(channel, read,
            value => readValue = value, () => readCallbacks++);
        read.Arm(() => readWait.Close());

        // The winning write captures the pending reader callback before it
        // mutates the buffer. Select loser cleanup then closes that reader
        // before the captured notification dispatches.
        var writeWait = BindWrite(channel, write, 99, () => writeCallbacks++);
        write.Arm(() => writeWait.Close());

        Require(select.Winner == 1, "write wins");
        Require(writeCallbacks == 1 && readCallbacks == 1,
            "stale captured reader callback may execute once");
        Require(readValue == 0, "stale loser callback cannot bind");
        Require(channel.PendingReaders == 0 &&
                channel.PendingWriters == 0,
            "no stale subscriptions remain");
        Require(channel.TryRead(out var value) && value == 99,
            "stale loser did not consume winning write value");
    }

    private static void CloseWakesPendingReadAsEof()
    {
        var channel = new SelectableI64Channel(1);
        using var select = new SelectArbiter(1);
        var read = select.RegisterCase(0);
        long value = 123;
        var callbacks = 0;
        var outcome = SelectableI64Channel.ReadOutcome.NotReady;

        var wait = BindRead(channel, read,
            v => value = v,
            () => callbacks++,
            o => outcome = o);
        read.Arm(() => wait.Close());

        Require(select.Winner is null && channel.PendingReaders == 1,
            "empty read remains pending before close");
        Require(channel.Close(), "first close wins");
        Require(channel.IsClosed, "closed flag visible");
        Require(select.Winner == 0, "close wakes pending read");
        Require(outcome == SelectableI64Channel.ReadOutcome.Eof,
            "closed drained read commits EOF");
        Require(value == 123, "EOF does not fabricate value");
        Require(callbacks == 1 && channel.PendingReaders == 0,
            "EOF waiter consumed exactly once");
        Require(!channel.Close(), "close idempotent");
    }

    private static void ClosedChannelDrainsBufferedValueBeforeEof()
    {
        var channel = new SelectableI64Channel(2);
        Require(channel.TryWrite(41), "seed buffered value");
        Require(channel.Close(), "close with buffered value");
        Require(!channel.TryWrite(42), "direct write denied after close");

        using (var firstSelect = new SelectArbiter(1))
        {
            var first = firstSelect.RegisterCase(0);
            long value = 0;
            var outcome = SelectableI64Channel.ReadOutcome.NotReady;
            var wait = BindRead(channel, first,
                v => value = v,
                () => { },
                o => outcome = o);
            first.Arm(() => wait.Close());

            Require(firstSelect.Winner == 0,
                "buffered closed read is immediately selectable");
            Require(outcome == SelectableI64Channel.ReadOutcome.Value &&
                    value == 41,
                "buffer drains before EOF");
        }

        using var eofSelect = new SelectArbiter(1);
        var eof = eofSelect.RegisterCase(0);
        long eofValue = 77;
        var eofOutcome = SelectableI64Channel.ReadOutcome.NotReady;
        var eofWait = BindRead(channel, eof,
            v => eofValue = v,
            () => { },
            o => eofOutcome = o);
        eof.Arm(() => eofWait.Close());

        Require(eofSelect.Winner == 0 &&
                eofOutcome == SelectableI64Channel.ReadOutcome.Eof,
            "drained closed channel yields EOF");
        Require(eofValue == 77, "EOF leaves value untouched");
    }

    private static void CloseWakesPendingWriteAsClosedWithoutPublication()
    {
        var channel = new SelectableI64Channel(1);
        Require(channel.TryWrite(7), "fill channel");

        using var select = new SelectArbiter(1);
        var write = select.RegisterCase(0);
        var callbacks = 0;
        var outcome = SelectableI64Channel.WriteOutcome.NotReady;
        var wait = BindWrite(channel, write, 8,
            () => callbacks++,
            o => outcome = o);
        write.Arm(() => wait.Close());

        Require(select.Winner is null && channel.PendingWriters == 1,
            "full-channel write pending");
        Require(channel.Close(), "close wakes writer");
        Require(select.Winner == 0, "closed write becomes terminal winner");
        Require(outcome == SelectableI64Channel.WriteOutcome.Closed,
            "closed write reports terminal closed outcome");
        Require(callbacks == 1 && channel.PendingWriters == 0,
            "closed writer consumed exactly once");
        Require(channel.Count == 1, "closed write did not publish");
        Require(!channel.TryWrite(9), "later writes rejected");

        Require(channel.TryRead(out var existing) && existing == 7,
            "pre-close buffered value preserved");
        Require(channel.Count == 0, "buffer drained");
    }

    private static void ImmediateCallbackFailureDoesNotLeakWaiter()
    {
        var channel = new SelectableI64Channel(1, 2);
        Require(channel.TryWrite(1), "seed readable channel");

        var observed = false;
        try
        {
            _ = channel.SubscribeReadable(_ =>
                throw new InvalidOperationException("callback failed"));
        }
        catch (InvalidOperationException)
        {
            observed = true;
        }

        Require(observed, "immediate callback failure surfaced");
        Require(channel.PendingReaders == 0,
            "failed immediate subscriber removed");
        Require(channel.Count == 1, "callback failure did not mutate buffer");
    }

    private static void AsyncNotificationFailureIsRecorded()
    {
        var channel = new SelectableI64Channel(1, 2);
        var wait = channel.SubscribeReadable(_ =>
            throw new InvalidOperationException("async callback failed"));
        Require(channel.PendingReaders == 1, "reader registered");

        Require(channel.TryWrite(5), "write commits despite callback failure");
        Require(channel.Count == 1, "committed write remains visible");
        Require(channel.NotificationFailure is InvalidOperationException,
            "notification failure retained for supervision");
        Require(wait.Close(), "failed callback registration remains revocable");
        Require(channel.PendingReaders == 0, "failed waiter detached");
    }

    private static void ForeignSubscriptionCannotCommit()
    {
        var first = new SelectableI64Channel(1);
        var second = new SelectableI64Channel(1);
        var foreign = first.SubscribeReadable(_ => { });

        var batch = second.CreateNotificationBatch();
        Require(!second.TryCommitRead(foreign, out _, batch),
            "foreign read subscription rejected");
        Require(!second.TryCommitWrite(foreign, 1, out _, batch),
            "foreign write subscription rejected");
        Require(foreign.Close(), "foreign source subscription closes");
    }

    public static void Run()
    {
        FirstReadyReadWinsWithoutConsumingLoser();
        LosingWriteNeverPublishes();
        DelayedReadinessWakesPendingRead();
        FreeingCapacityWakesPendingWrite();
        CancellationDetachesWaiter();
        StaleNotificationCannotConsumeForLoser();
        CloseWakesPendingReadAsEof();
        ClosedChannelDrainsBufferedValueBeforeEof();
        CloseWakesPendingWriteAsClosedWithoutPublication();
        ImmediateCallbackFailureDoesNotLeakWaiter();
        AsyncNotificationFailureIsRecorded();
        ForeignSubscriptionCannotCommit();
    }
}
