using Ores.Net.Runtime;

internal static class ActorMailboxBoundsSelfTest
{
    public static void Run()
    {
        static void DenyLimit(Action action)
        {
            try { action(); }
            catch (ArgumentOutOfRangeException) { return; }
            throw new Exception("unbounded actor mailbox accepted");
        }
        static void DenySend(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("actor mailbox capacity bypass");
        }
        DenyLimit(() => _ = new ActorMailbox(1, int.MaxValue, 1));
        DenyLimit(() => _ = new ActorMailbox(1, 1, int.MaxValue));
        DenyLimit(() => _ = new ActorMailbox(1, ActorMailbox.MaxMailboxMessages + 1, 1));
        DenyLimit(() => _ = new ActorMailbox(1, 1, ActorMailbox.MaxMailboxBytes + 1));
        var box = new ActorMailbox(2, ActorMailbox.MaxMailboxMessages,
            ActorMailbox.MaxMailboxBytes);
        var reference = box.CurrentRef;
        var input = new byte[ActorMailbox.MaxMessageBytes];
        input[0] = 7;
        for (var i = 0; i < 16; ++i) box.Send(reference, input);
        DenySend(() => box.Send(reference, input));
        input[0] = 9;
        if (!box.TryReceive(out var first) || first is null || first[0] != 7)
            throw new Exception("payload ownership violation");
        box.Send(reference, input);
        DenySend(() => box.Send(reference, input));
        box.Stop();
        DenySend(() => box.Send(reference, input));
        var fresh = box.Restart();
        DenySend(() => box.Send(reference, input));
        box.Send(fresh, Array.Empty<byte>());
        if (!box.TryReceive(out var empty) || empty is null || empty.Length != 0)
            throw new Exception("empty mailbox DATA was lost");
        var single = new ActorMailbox(3, maxMessages: 1, maxQueuedBytes: 1);
        var refSingle = single.CurrentRef;
        single.Send(refSingle, Array.Empty<byte>());
        DenySend(() => single.Send(refSingle, Array.Empty<byte>()));
        if (!single.TryReceive(out _)) throw new Exception("empty slot was not received");
        single.Send(refSingle, new byte[] { 42 });
        if (!single.TryReceive(out var payload) || payload is null || payload[0] != 42)
            throw new Exception("slot not released");
        // Drain closes admission without erasing previously admitted bytes.
        // A forged or stale ActorRef must never claim drain authority.
        var draining = new ActorMailbox(5, maxMessages: 2, maxQueuedBytes: 8);
        var drainRef = draining.CurrentRef;
        var wrongDrainRef = new ActorMailbox(5).CurrentRef;
        draining.Send(drainRef, new byte[] { 4, 5 });
        draining.Send(drainRef, new byte[] { 6 });
        if (draining.TryBeginDrain(wrongDrainRef) ||
            !draining.TryBeginDrain(drainRef) ||
            draining.TryBeginDrain(drainRef))
            throw new Exception("drain admission must be generation/nonce fenced and once-only");
        DenySend(() => draining.Send(drainRef, new byte[] { 7 }));
        if (!draining.TryReceive(out var drainedFirst) ||
            drainedFirst is null || !drainedFirst.SequenceEqual(new byte[] { 4, 5 }) ||
            !draining.TryReceive(out var drainedSecond) ||
            drainedSecond is null || !drainedSecond.SequenceEqual(new byte[] { 6 }) ||
            draining.TryReceive(out _))
            throw new Exception("draining lost or reordered admitted DATA");
        draining.Stop();
        var postDrain = draining.Restart();
        if (draining.TryBeginDrain(drainRef))
            throw new Exception("stale generation could drain replacement actor");
        draining.Send(postDrain, new byte[] { 8 });
        draining.Dispose();

        // Disposal is terminal authority retirement, unlike a restartable Stop.
        // Inspect only the already-owned DATA array to prove queued secrets
        // are physically scrubbed rather than merely removed from the queue.
        var disposable = new ActorMailbox(4, maxMessages: 2, maxQueuedBytes: 8);
        var leased = disposable.CurrentRef;
        var sensitive = new byte[] { 7, 8, 9 };
        disposable.Send(leased, sensitive);
        var pendingField = typeof(ActorMailbox).GetField("_messages",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("Cannot inspect owned mailbox DATA for scrubbing");
        var queue = (Queue<byte[]>)pendingField.GetValue(disposable)!;
        var ownedBytes = queue.Peek();
        sensitive[0] = 99;
        if (ownedBytes[0] != 7)
            throw new Exception("Mailbox did not take a private DATA copy");
        disposable.Dispose();
        disposable.Dispose(); // idempotent and still terminal
        if (ownedBytes.Any(value => value != 0))
            throw new Exception("Disposed mailbox retained queued DATA bytes");
        if (disposable.TryReceive(out _))
            throw new Exception("Disposed mailbox delivered an erased envelope");
        DenySend(() => disposable.Send(leased, sensitive));
        try
        {
            _ = disposable.Restart();
            throw new Exception("Disposed mailbox resurrected local ActorRef authority");
        }
        catch (ObjectDisposedException) { }

        Console.WriteLine("PASS .NET mailbox limits, drain admission/FIFO, exact byte refund, empty data, restart, dispose scrub");
    }
}
