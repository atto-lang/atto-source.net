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
        Action callback)
    {
        return channel.SubscribeReadable(subscription =>
        {
            callback();
            var notifications = channel.CreateNotificationBatch();
            SelectableI64Channel.ReadResult outcome = default;
            var won = registration.TryWin(() =>
                channel.TryCommitRead(subscription, out outcome, notifications));
            notifications.Dispatch();
            if (won &&
                outcome.Status == SelectableI64Channel.ReadStatus.Value)
                result(outcome.Value);
        });
    }

    private static SelectableI64Channel.Subscription BindWrite(
        SelectableI64Channel channel,
        SelectArbiter.Registration registration,
        long value,
        Action callback)
    {
        return channel.SubscribeWritable(subscription =>
        {
            callback();
            var notifications = channel.CreateNotificationBatch();
            SelectableI64Channel.WriteStatus outcome = default;
            _ = registration.TryWin(() =>
                channel.TryCommitWrite(
                    subscription, value, out outcome, notifications));
            notifications.Dispatch();
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

    private static void PendingBufferedWritersAdmitInSubscriptionOrder()
    {
        var channel = new SelectableI64Channel(1);
        Require(channel.TryWrite(0), "seed full channel");

        using var firstSelect = new SelectArbiter(1);
        using var secondSelect = new SelectArbiter(1);
        var first = firstSelect.RegisterCase(0);
        var second = secondSelect.RegisterCase(0);
        var firstCallbacks = 0;
        var secondCallbacks = 0;

        var firstWait = BindWrite(channel, first, 1, () => firstCallbacks++);
        first.Arm(() => firstWait.Close());
        var secondWait = BindWrite(channel, second, 2, () => secondCallbacks++);
        second.Arm(() => secondWait.Close());

        Require(channel.PendingWriters == 2, "two writers pending");
        Require(firstSelect.Winner is null && secondSelect.Winner is null,
            "neither pending writer committed while full");

        Require(channel.TryRead(out var seed) && seed == 0,
            "first read frees one slot");

        Require(firstSelect.Winner == 0,
            "oldest registered writer commits first");
        Require(secondSelect.Winner is null,
            "later writer remains pending while first value occupies slot");
        Require(firstCallbacks == 1 && secondCallbacks == 1,
            "both readiness observers sampled first freed slot");
        Require(channel.PendingWriters == 1,
            "only later writer remains registered");

        Require(channel.TryRead(out var firstValue) && firstValue == 1,
            "first pending payload is FIFO-visible");
        Require(secondSelect.Winner == 0,
            "later writer commits on second freed slot");
        Require(secondCallbacks == 2,
            "later writer receives readiness again");
        Require(channel.PendingWriters == 0,
            "no writer registrations remain");

        Require(channel.TryRead(out var secondValue) && secondValue == 2,
            "second pending payload follows first");
        Require(!channel.TryRead(out _), "no extra payload delivered");
    }

    private static void CancellingOlderWriterLetsLaterWriterAdvanceWithoutGhostDelivery()
    {
        var channel = new SelectableI64Channel(1);
        Require(channel.TryWrite(0), "seed full channel");

        using var olderSelect = new SelectArbiter(1);
        using var laterSelect = new SelectArbiter(1);
        var older = olderSelect.RegisterCase(0);
        var later = laterSelect.RegisterCase(0);
        var olderCallbacks = 0;
        var laterCallbacks = 0;

        var olderWait = BindWrite(channel, older, 1, () => olderCallbacks++);
        older.Arm(() => olderWait.Close());
        var laterWait = BindWrite(channel, later, 2, () => laterCallbacks++);
        later.Arm(() => laterWait.Close());

        Require(channel.PendingWriters == 2, "two writers pending before cancellation");
        Require(olderSelect.Cancel(), "older writer cancellation wins");
        Require(channel.PendingWriters == 1,
            "older cancellation detaches exactly one writer");
        Require(olderCallbacks == 0,
            "cancelled writer did not observe readiness");

        Require(channel.TryRead(out var seed) && seed == 0,
            "read frees capacity after cancellation");

        Require(olderSelect.Winner is null,
            "cancelled older writer never becomes winner");
        Require(laterSelect.Winner == 0,
            "later live writer advances");
        Require(laterCallbacks == 1,
            "later writer observes freed capacity once");
        Require(channel.PendingWriters == 0,
            "later winner consumes final registration");

        Require(channel.TryRead(out var value) && value == 2,
            "only later live payload was admitted");
        Require(!channel.TryRead(out _),
            "cancelled older payload never appears");
        Require(olderCallbacks == 0,
            "cancelled waiter never resurrected");
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

    private static void CloseWakesPendingReadWithEof()
    {
        var channel = new SelectableI64Channel(1);
        using var select = new SelectArbiter(1);
        var read = select.RegisterCase(0);
        SelectableI64Channel.ReadResult outcome = default;
        var callbacks = 0;

        var wait = channel.SubscribeReadable(subscription =>
        {
            callbacks++;
            var notifications = channel.CreateNotificationBatch();
            _ = read.TryWin(() =>
                channel.TryCommitRead(
                    subscription, out outcome, notifications));
            notifications.Dispatch();
        });
        read.Arm(() => wait.Close());

        Require(select.Winner is null, "empty channel keeps read pending");
        Require(channel.Close(), "first close transitions channel");
        Require(!channel.Close(), "close is idempotent");
        Require(channel.Closed, "closed state visible");
        Require(select.Winner == 0 && callbacks == 1,
            "close wakes pending selected read");
        Require(outcome.Status == SelectableI64Channel.ReadStatus.Eof,
            "selected read commits EOF");
        Require(channel.PendingReaders == 0, "EOF reader detached");
        Require(!channel.TryWrite(1), "post-close writes rejected");
        Require(channel.TryReadResult().Status ==
                SelectableI64Channel.ReadStatus.Eof,
            "direct read distinguishes EOF");
    }

    private static void CloseDrainsBufferedValueBeforeEof()
    {
        var channel = new SelectableI64Channel(1);
        Require(channel.TryWrite(77), "seed close-drain buffer");
        Require(channel.Close(), "close buffered channel");

        var value = channel.TryReadResult();
        Require(value.Status == SelectableI64Channel.ReadStatus.Value &&
                value.Value == 77,
            "buffered value drains after close");
        Require(channel.TryReadResult().Status ==
                SelectableI64Channel.ReadStatus.Eof,
            "EOF follows final buffered value");
    }

    private static void CloseWakesPendingWriteAsClosed()
    {
        var channel = new SelectableI64Channel(1);
        Require(channel.TryWrite(7), "fill close-write channel");
        using var select = new SelectArbiter(1);
        var write = select.RegisterCase(0);
        SelectableI64Channel.WriteStatus outcome = default;
        var callbacks = 0;

        var wait = channel.SubscribeWritable(subscription =>
        {
            callbacks++;
            var notifications = channel.CreateNotificationBatch();
            _ = write.TryWin(() =>
                channel.TryCommitWrite(
                    subscription, 8, out outcome, notifications));
            notifications.Dispatch();
        });
        write.Arm(() => wait.Close());

        Require(select.Winner is null, "full channel keeps write pending");
        Require(channel.Close(), "close wakes pending write");
        Require(select.Winner == 0 && callbacks == 1,
            "closed write arm becomes terminal winner");
        Require(outcome == SelectableI64Channel.WriteStatus.Closed,
            "selected write reports closed outcome");
        Require(channel.PendingWriters == 0, "closed writer detached");
        Require(channel.TryRead(out var existing) && existing == 7,
            "closed write did not publish payload");
        Require(channel.TryReadResult().Status ==
                SelectableI64Channel.ReadStatus.Eof,
            "buffer drains to EOF");
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

    private static void BatchWriteAllOrNothing()
    {
        var channel = new SelectableI64Channel(3);
        Require(channel.TryWriteBatch(new long[] { 10, 11 }) ==
                SelectableI64Channel.WriteStatus.Written, "first batch admitted");
        Require(channel.TryWriteBatch(new long[] { 12, 13 }) ==
                SelectableI64Channel.WriteStatus.WouldBlock,
            "insufficient capacity denies entire batch");
        Require(channel.Count == 2, "denied batch leaves count unchanged");
        Require(channel.TryWriteBatch(new long[] { 12 }) ==
                SelectableI64Channel.WriteStatus.Written,
            "remaining single slot accepts a batch");
        Require(channel.Count == 3, "exact capacity filled");
        Require(channel.TryRead(out var a) && a == 10, "first item FIFO");
        Require(channel.TryRead(out var b) && b == 11, "second item FIFO");
        Require(channel.TryRead(out var c) && c == 12, "third item FIFO");
        Require(channel.TryReadResult().Status ==
                SelectableI64Channel.ReadStatus.WouldBlock,
            "denied payload never partially published");
    }

    private static void BatchWriteWrapsWithoutOverwriting()
    {
        var channel = new SelectableI64Channel(3);
        Require(channel.TryWriteBatch(new long[] { 1, 2, 3 }) ==
                SelectableI64Channel.WriteStatus.Written, "seed full ring");
        Require(channel.TryRead(out var first) && first == 1,
            "first ring element consumed");
        Require(channel.TryRead(out var second) && second == 2,
            "second ring element consumed");
        Require(channel.TryWriteBatch(new long[] { 4, 5 }) ==
                SelectableI64Channel.WriteStatus.Written,
            "wrapped two-value batch admitted");
        Require(channel.Count == 3, "wrapped batch fills ring");
        Require(channel.TryRead(out var third) && third == 3,
            "unread ring head preserved");
        Require(channel.TryRead(out var fourth) && fourth == 4,
            "first wrapped item FIFO");
        Require(channel.TryRead(out var fifth) && fifth == 5,
            "second wrapped item FIFO");
        Require(channel.TryWriteBatch(ReadOnlySpan<long>.Empty) ==
                SelectableI64Channel.WriteStatus.Written,
            "empty open batch is no-op");
        Require(channel.Count == 0, "empty batch preserves count");
    }

    private static void BatchWriteClosureAndReadiness()
    {
        var channel = new SelectableI64Channel(3, 2);
        var callbacks = 0;
        var subscription = channel.SubscribeReadable(_ => callbacks++);
        Require(channel.TryWriteBatch(ReadOnlySpan<long>.Empty) ==
                SelectableI64Channel.WriteStatus.Written,
            "empty batch succeeds");
        Require(callbacks == 0, "empty batch does not wake readers");
        Require(channel.TryWriteBatch(new long[] { 91, 92 }) ==
                SelectableI64Channel.WriteStatus.Written,
            "nonempty batch accepted");
        Require(callbacks == 1, "batch wakes pending reader only once");
        Require(channel.Count == 2, "batch publishes two payloads");
        Require(subscription.Close(), "readiness subscription still revocable");
        Require(channel.Close(), "close channel with buffered batch");
        Require(channel.TryWriteBatch(new long[] { 93 }) ==
                SelectableI64Channel.WriteStatus.Closed,
            "closed channel denies nonempty batch");
        Require(channel.TryWriteBatch(ReadOnlySpan<long>.Empty) ==
                SelectableI64Channel.WriteStatus.Closed,
            "closed channel denies even empty batch");
        Require(channel.Count == 2, "closed denial has no side effects");
        Require(channel.TryRead(out var a) && a == 91, "closed first retained value");
        Require(channel.TryRead(out var b) && b == 92, "closed second retained value");
        Require(channel.TryReadResult().Status ==
                SelectableI64Channel.ReadStatus.Eof, "EOF after batch drain");
    }

    private static void BatchWriteCallbackFailureDoesNotRollback()
    {
        var channel = new SelectableI64Channel(2);
        var subscription = channel.SubscribeReadable(_ =>
            throw new InvalidOperationException("batch callback failed"));
        Require(channel.TryWriteBatch(new long[] { 6, 7 }) ==
                SelectableI64Channel.WriteStatus.Written,
            "callback fault must not undo committed batch");
        Require(channel.NotificationFailure is InvalidOperationException,
            "batch callback fault retained for supervision");
        Require(channel.Count == 2, "committed batch remains intact");
        Require(subscription.Close(), "faulting batch waiter remains revocable");
        Require(channel.TryRead(out var a) && a == 6, "faulted first item retained");
        Require(channel.TryRead(out var b) && b == 7, "faulted second item retained");
    }

    private static void BatchWriteFastPathsDoNotAllocate()
    {
        var channel = new SelectableI64Channel(2);
        var payload = new long[] { 3, 4, 5 };
        // Warm each call site before measuring a no-readers path.
        Require(channel.TryWriteBatch(ReadOnlySpan<long>.Empty) ==
                SelectableI64Channel.WriteStatus.Written, "warm empty batch");
        Require(channel.TryWriteBatch(payload) ==
                SelectableI64Channel.WriteStatus.WouldBlock, "warm oversized batch");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
        {
            if (channel.TryWriteBatch(ReadOnlySpan<long>.Empty) !=
                SelectableI64Channel.WriteStatus.Written)
                throw new Exception("empty batch status changed");
            if (channel.TryWriteBatch(payload) !=
                SelectableI64Channel.WriteStatus.WouldBlock)
                throw new Exception("oversized batch status changed");
        }
        var after = GC.GetAllocatedBytesForCurrentThread();
        Require(after == before, "empty and rejected batches allocate no managed bytes");

        Require(channel.TryWriteBatch(new long[] { 1, 2 }) ==
                SelectableI64Channel.WriteStatus.Written, "fill ring");
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
        {
            if (channel.TryWriteBatch(payload) !=
                SelectableI64Channel.WriteStatus.WouldBlock)
                throw new Exception("full ring status changed");
        }
        after = GC.GetAllocatedBytesForCurrentThread();
        Require(after == before, "full-ring rejection allocates no managed bytes");

        Require(channel.Close(), "close ring");
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
        {
            if (channel.TryWriteBatch(payload) !=
                SelectableI64Channel.WriteStatus.Closed)
                throw new Exception("closed ring status changed");
        }
        after = GC.GetAllocatedBytesForCurrentThread();
        Require(after == before, "closed-ring rejection allocates no managed bytes");
    }

    private static void BatchWriteRacesCloseWithoutPartialPublication()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var channel = new SelectableI64Channel(2);
            using var start = new System.Threading.ManualResetEventSlim(false);
            var outcome = SelectableI64Channel.WriteStatus.WouldBlock;
            var writer = new System.Threading.Thread(() =>
            {
                start.Wait();
                outcome = channel.TryWriteBatch(new long[] { 31, 32 });
            });
            var closer = new System.Threading.Thread(() =>
            {
                start.Wait();
                _ = channel.Close();
            });
            writer.Start();
            closer.Start();
            start.Set();
            writer.Join();
            closer.Join();
            Require(channel.Closed, "close/write race ends terminal");
            Require(outcome == SelectableI64Channel.WriteStatus.Written ||
                    outcome == SelectableI64Channel.WriteStatus.Closed,
                "racing write is complete or closed, never partial");
            if (outcome == SelectableI64Channel.WriteStatus.Written)
            {
                Require(channel.Count == 2, "accepted race retained entire batch");
                Require(channel.TryRead(out var first) && first == 31,
                    "accepted race first element");
                Require(channel.TryRead(out var second) && second == 32,
                    "accepted race second element");
            }
            else
            {
                Require(channel.Count == 0, "denied race leaves no elements");
            }
            Require(channel.TryReadResult().Status ==
                    SelectableI64Channel.ReadStatus.Eof,
                "racing close leads to EOF only after any complete batch");
        }
    }

    public static void Run()
    {
        FirstReadyReadWinsWithoutConsumingLoser();
        LosingWriteNeverPublishes();
        DelayedReadinessWakesPendingRead();
        FreeingCapacityWakesPendingWrite();
        PendingBufferedWritersAdmitInSubscriptionOrder();
        CancellingOlderWriterLetsLaterWriterAdvanceWithoutGhostDelivery();
        CancellationDetachesWaiter();
        StaleNotificationCannotConsumeForLoser();
        ImmediateCallbackFailureDoesNotLeakWaiter();
        AsyncNotificationFailureIsRecorded();
        CloseWakesPendingReadWithEof();
        CloseDrainsBufferedValueBeforeEof();
        CloseWakesPendingWriteAsClosed();
        ForeignSubscriptionCannotCommit();
        BatchWriteAllOrNothing();
        BatchWriteWrapsWithoutOverwriting();
        BatchWriteClosureAndReadiness();
        BatchWriteCallbackFailureDoesNotRollback();
        BatchWriteFastPathsDoNotAllocate();
        BatchWriteRacesCloseWithoutPartialPublication();
    }
}
