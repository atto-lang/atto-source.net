using Ores.Net.Runtime;

internal static class ActorIoEnvelopeSelfTest
{
    public static void Run()
    {
        var mailbox = new ActorMailbox(91001, maxMessages: 4, maxQueuedBytes: 64);
        var actor = mailbox.CurrentRef;
        var io = new ActorIoEnvelope(mailbox);

        byte[] source = { 1, 2, 3 };
        Require(io.TryFeedInput(actor, source), "input admission");
        source[0] = 9;
        Require(io.TryReadInput(actor, out var input) && input is not null &&
            input.SequenceEqual(new byte[] { 1, 2, 3 }), "input must be owned copy");

        Require(io.TryWriteOutput(actor, new byte[] { 4, 5 }), "output admission");
        Require(io.TryEmitError(actor, new ActorIoDiagnostic("E_PARSE", "bad token")),
            "diagnostic admission");
        Require(io.TryEmitEvent(actor, new ActorIoEvent("ready", "v1")),
            "event admission");

        Require(io.TryReadOutput(actor, out var output) && output is not null &&
            output.SequenceEqual(new byte[] { 4, 5 }), "output FIFO");
        Require(io.TryReadError(actor, out var diagnostic) &&
            diagnostic.Code == "E_PARSE" && diagnostic.Message == "bad token",
            "typed error stream");
        Require(io.TryReadEvent(actor, out var evt) &&
            evt.Kind == "ready" && evt.Payload == "v1", "typed event stream");

        var initial = io.Metadata(actor);
        Require(initial.Version == 1 && initial.Values.Count == 0, "initial metadata");
        var updated = io.ReplaceMetadata(actor, new Dictionary<string, string>
        {
            ["trace_id"] = "abc",
            ["route"] = "/v1"
        });
        Require(updated.Version == 2 && updated.Values["trace_id"] == "abc",
            "versioned metadata");
        var mutableSource = new Dictionary<string, string> { ["key"] = "one" };
        var snap = io.ReplaceMetadata(actor, mutableSource);
        mutableSource["key"] = "mutated";
        Require(snap.Values["key"] == "one", "metadata must snapshot caller state");

        // Channel records are bounded independently; failure never mutates another channel.
        var fullRecord = new byte[ActorIoEnvelope.MaxBytesPerChannel];
        Require(io.TryWriteOutput(actor, fullRecord), "maximum bounded record");
        Require(!io.TryWriteOutput(actor, new byte[] { 1 }), "output backpressure");
        Require(io.TryEmitEvent(actor, new ActorIoEvent("still", "independent")),
            "event channel remains independent when output is full");
        Require(io.TryReadOutput(actor, out var full) && full?.Length == fullRecord.Length,
            "full output drains exactly");

        var oversizedMetadataValue = new string('x', ActorIoEnvelope.MaxMetadataBytes + 1);
        ExpectRange(
            () => io.ReplaceMetadata(actor, new Dictionary<string, string>
            {
                ["oversized"] = oversizedMetadataValue
            }),
            "oversized metadata must fail before counter mutation");
        Require(
            !io.TryEmitError(
                actor,
                new ActorIoDiagnostic(
                    "E_BIG",
                    new string('x', ActorIoEnvelope.MaxBytesPerChannel + 1))),
            "oversized diagnostic must fail closed");
        Require(
            !io.TryEmitEvent(
                actor,
                new ActorIoEvent(
                    "big",
                    new string('x', ActorIoEnvelope.MaxBytesPerChannel + 1))),
            "oversized event must fail closed");

        var stale = actor;
        mailbox.Stop();
        var fresh = mailbox.Restart();
        ExpectStale(() => io.TryWriteOutput(stale, new byte[] { 7 }), "stale output");
        ExpectStale(() => io.Metadata(stale), "stale metadata");
        ExpectStale(() => io.TryWriteOutput(fresh, new byte[] { 8 }),
            "fresh ActorRef must not reopen the retired generation envelope");
        var freshIo = new ActorIoEnvelope(mailbox);
        Require(freshIo.TryWriteOutput(fresh, new byte[] { 8 }),
            "fresh generation receives a fresh logical envelope");

        var foreignMailbox = new ActorMailbox(91001);
        var foreign = foreignMailbox.CurrentRef;
        ExpectStale(() => io.TryWriteOutput(foreign, new byte[] { 9 }), "foreign authority");

        TestTerminalRetirement();
        TestHalfCloseKeepsQueuedFrames();
        Console.WriteLine("ACTOR_IO_ENVELOPE_RETIREMENT_PASS");
        Console.WriteLine("ACTOR_IO_HALF_CLOSE_PASS");

        Console.WriteLine(
            "PASS .NET actor io bounded input/output/error/events/metadata and generation fencing");
    }

    private static void TestHalfCloseKeepsQueuedFrames()
    {
        using var box = new ActorMailbox(91009);
        var actor = box.CurrentRef;
        using var io = new ActorIoEnvelope(box);
        Require(io.TryFeedInput(actor, new byte[] { 1, 2, 3 }),
            "half-close input buffer admission");
        Require(!io.InputEof(actor), "open input is not EOF");
        io.CompleteInput(actor);
        io.CompleteInput(actor); // idempotent
        Require(!io.InputEof(actor), "input EOF waits for queued frames");
        Require(!io.TryFeedInput(actor, new byte[] { 4 }),
            "host input write after half-close rejected");
        Require(!io.TryFeedInput(actor, Array.Empty<byte>()),
            "empty input cannot reopen completed stream");
        Require(io.TryReadInput(actor, out var input) && input is not null &&
            input.SequenceEqual(new byte[] { 1, 2, 3 }),
            "input buffered before half-close must still drain");
        Require(io.InputEof(actor) && !io.TryReadInput(actor, out _),
            "input EOF becomes observable only after drain");

        Require(io.TryWriteOutput(actor, new byte[] { 5, 6 }),
            "half-close output buffer admission");
        io.CompleteOutput(actor);
        io.CompleteOutput(actor);
        Require(!io.OutputEof(actor), "output EOF waits for queued frames");
        Require(!io.TryWriteOutput(actor, new byte[] { 7 }),
            "actor output write after half-close rejected");
        Require(!io.TryWriteOutput(actor, Array.Empty<byte>()),
            "empty output cannot reopen completed stream");
        Require(io.TryReadOutput(actor, out var output) && output is not null &&
            output.SequenceEqual(new byte[] { 5, 6 }),
            "output queued before half-close must still drain");
        Require(io.OutputEof(actor) && !io.TryReadOutput(actor, out _),
            "output EOF becomes observable only after drain");

        var wrong = new ActorMailbox(91009).CurrentRef;
        ExpectStale(() => io.CompleteInput(wrong), "foreign input EOF");
        ExpectStale(() => io.CompleteOutput(wrong), "foreign output EOF");
        ExpectStale(() => io.InputEof(wrong), "foreign input EOF observation");
        ExpectStale(() => io.OutputEof(wrong), "foreign output EOF observation");
    }

    private static void TestTerminalRetirement()
    {
        using var mailbox = new ActorMailbox(91008, maxMessages: 2, maxQueuedBytes: 64);
        var actor = mailbox.CurrentRef;
        var io = new ActorIoEnvelope(mailbox);
        byte[] input = { 11, 22, 33 };
        byte[] output = { 44, 55, 66 };
        Require(io.TryFeedInput(actor, input), "retirement input admission");
        Require(io.TryWriteOutput(actor, output), "retirement output admission");
        Require(io.TryEmitError(actor, new ActorIoDiagnostic("E", "retired")),
            "retirement error admission");
        Require(io.TryEmitEvent(actor, new ActorIoEvent("close", "retired")),
            "retirement event admission");
        _ = io.ReplaceMetadata(actor,
            new Dictionary<string, string> { ["token"] = "not-current" });

        // Inspect only buffers owned by the transport, not delivered buffers.
        // A lifecycle that merely clears the queue would fail this regression.
        static byte[] Pending(ActorIoEnvelope envelope, string field)
        {
            var member = typeof(ActorIoEnvelope).GetField(field,
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Cannot inspect pending I/O buffer " + field);
            return ((Queue<byte[]>)member.GetValue(envelope)!).Peek();
        }
        var ownedInput = Pending(io, "_input");
        var ownedOutput = Pending(io, "_output");
        Require(!ReferenceEquals(ownedInput, input) &&
            !ReferenceEquals(ownedOutput, output), "I/O must own byte snapshots");
        input[0] = output[0] = 99;
        Require(ownedInput[0] == 11 && ownedOutput[0] == 44,
            "I/O inputs must not alias mutable caller buffers");

        mailbox.Stop();
        io.Dispose();
        io.Dispose(); // idempotent terminal retirement
        Require(ownedInput.All(x => x == 0) && ownedOutput.All(x => x == 0),
            "terminal retirement must physically scrub pending I/O bytes");

        static void ExpectDisposed(Action action, string scenario)
        {
            try { action(); }
            catch (ObjectDisposedException) { return; }
            throw new Exception("Retired I/O envelope accepted " + scenario);
        }
        ExpectDisposed(() => io.TryFeedInput(actor, new byte[] { 1 }),
            "input after retirement");
        ExpectDisposed(() => io.TryReadInput(actor, out _),
            "input read after retirement");
        ExpectDisposed(() => io.TryWriteOutput(actor, new byte[] { 2 }),
            "output after retirement");
        ExpectDisposed(() => io.TryReadOutput(actor, out _),
            "output read after retirement");
        ExpectDisposed(() => io.Metadata(actor), "metadata after retirement");

        var fresh = mailbox.Restart();
        ExpectDisposed(() => io.TryWriteOutput(fresh, new byte[] { 3 }),
            "fresh incarnation on retired I/O");
        using var replacement = new ActorIoEnvelope(mailbox);
        Require(replacement.TryWriteOutput(fresh, new byte[] { 4 }),
            "replacement generation must admit new I/O");
        Require(replacement.TryReadOutput(fresh, out var received) &&
            received is not null && received.SequenceEqual(new byte[] { 4 }),
            "replacement generation must retain FIFO");
        // Ownership of a delivered buffer is transferred to the caller. It
        // must not be silently modified when the transport is later disposed.
        replacement.Dispose();
        Require(received is not null && received[0] == 4,
            "retirement must not mutate caller-owned delivered bytes");
    }

    private static void ExpectRange(Action action, string scenario)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Actor I/O quota accepted invalid input: " + scenario);
    }

    private static void ExpectStale(Action action, string scenario)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Actor I/O stale/foreign authority accepted: " + scenario);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition) throw new Exception("Actor I/O regression: " + scenario);
    }
}
