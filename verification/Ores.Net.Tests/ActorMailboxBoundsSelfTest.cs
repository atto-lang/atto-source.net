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
        // Numeric actor ID and generation are not credentials. Another
        // mailbox with identical visible identifiers must still reject
        // this actor reference before reserving bytes or a queue slot.
        var lookalike = new ActorMailbox(2, maxMessages: 1, maxQueuedBytes: 8);
        var ownLookalikeRef = lookalike.CurrentRef;
        DenySend(() => lookalike.Send(reference, new byte[] { 11 }));
        DenySend(() => box.Send(ownLookalikeRef, new byte[] { 12 }));
        if (lookalike.QueuedMessages != 0 || box.QueuedMessages != 0)
            throw new Exception("forged same-ID actor authority consumed mailbox capacity");
        lookalike.Send(ownLookalikeRef, new byte[] { 13 });
        if (!lookalike.TryReceive(out var ownData) || ownData is null || ownData[0] != 13)
            throw new Exception("forged send poisoned the legitimate destination");

        // An oversized forged payload must fail for lack of authority, not
        // disclose the size/budget policy before the receiver is authenticated.
        var oversized = new byte[ActorMailbox.MaxMessageBytes + 1];
        DenySend(() => lookalike.Send(reference, oversized));
        DenySend(() => box.Send(ownLookalikeRef, oversized));
        if (lookalike.QueuedMessages != 0 || box.QueuedMessages != 0)
            throw new Exception("oversized forged send mutated mailbox state");
        var validOversizeRejected = false;
        try { box.Send(reference, oversized); }
        catch (ArgumentOutOfRangeException) { validOversizeRejected = true; }
        if (!validOversizeRejected)
            throw new Exception("valid authority bypassed 1 MiB payload limit");

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
        Console.WriteLine("PASS .NET mailbox limits, exact byte refund, empty data, restart");
    }
}
