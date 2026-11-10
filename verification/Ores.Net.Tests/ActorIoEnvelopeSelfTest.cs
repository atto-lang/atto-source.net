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

        // Closing one logical stream must not silently close the other stream,
        // ordinary DATA mailbox, or the typed diagnostic/event channels.
        Require(io.TryFeedInput(actor, new byte[] { 10 }), "feed before input EOF");
        Require(io.CloseInput(actor), "first input close");
        Require(!io.CloseInput(actor), "duplicate input close is idempotent");
        Require(!io.TryFeedInput(actor, new byte[] { 11 }), "input rejects post-EOF writes");
        Require(!io.InputAtEof(actor), "queued input must drain before EOF");
        Require(io.TryReadInput(actor, out var pendingInput) &&
            pendingInput!.SequenceEqual(new byte[] { 10 }), "drain input after close");
        Require(io.InputAtEof(actor), "input EOF only after drain");

        Require(io.TryWriteOutput(actor, new byte[] { 12 }), "output independent of input EOF");
        Require(io.CloseOutput(actor), "first output close");
        Require(!io.CloseOutput(actor), "duplicate output close is idempotent");
        Require(!io.TryWriteOutput(actor, new byte[] { 13 }), "no output admission after close");
        Require(!io.OutputAtEof(actor), "output must drain before EOF");
        Require(io.TryReadOutput(actor, out var pendingOutput) &&
            pendingOutput!.SequenceEqual(new byte[] { 12 }), "drain output after close");
        Require(io.OutputAtEof(actor), "output EOF only after drain");
        Require(io.TryEmitError(actor, new ActorIoDiagnostic("E_LATE", "independent")),
            "typed diagnostics independent of stream EOF");
        Require(io.TryEmitEvent(actor, new ActorIoEvent("closed", "input-output")),
            "events independent of stream EOF");
        mailbox.Send(actor, new byte[] { 42 });
        Require(mailbox.TryReceive(out var independentMail) &&
            independentMail!.SequenceEqual(new byte[] { 42 }),
            "closing logical I/O must not stop actor mailbox");

        // Retirement must be safe even after physical mailbox STOP and never
        // reopen under a fresh generation. It scrubs only unread owned records.
        var retireMailbox = new ActorMailbox(91002);
        var retireActor = retireMailbox.CurrentRef;
        var retireIo = new ActorIoEnvelope(retireMailbox);
        Require(retireIo.TryFeedInput(retireActor, new byte[] { 99 }), "retirement input setup");
        Require(retireIo.TryWriteOutput(retireActor, new byte[] { 98 }), "retirement output setup");
        Require(retireIo.TryEmitError(retireActor, new ActorIoDiagnostic("E", "owned")),
            "retirement diagnostic setup");
        retireMailbox.Stop();
        retireIo.Retire();
        retireIo.Retire(); // idempotent trusted-host cleanup
        ExpectStale(() => retireIo.InputAtEof(retireActor), "retired input is inaccessible");
        ExpectStale(() => retireIo.OutputAtEof(retireActor), "retired output is inaccessible");
        var retireFresh = retireMailbox.Restart();
        ExpectStale(() => retireIo.TryFeedInput(retireFresh, new byte[] { 1 }),
            "retired envelope cannot reopen after restart");
        var retireFreshIo = new ActorIoEnvelope(retireMailbox);
        Require(retireFreshIo.TryWriteOutput(retireFresh, new byte[] { 1 }),
            "fresh incarnation gets separate output");
        retireFreshIo.Retire();

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

        Console.WriteLine(
            "PASS .NET actor io bounded input/output/error/events/metadata and generation fencing");
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
