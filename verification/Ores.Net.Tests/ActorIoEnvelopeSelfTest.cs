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
        retireMailbox.Stop(); // must automatically retire the bound I/O generation
        ExpectStale(() => retireIo.InputAtEof(retireActor), "mailbox STOP retires input");
        retireIo.Retire(); // repeated explicit trusted-host cleanup remains harmless
        ExpectStale(() => retireIo.InputAtEof(retireActor), "retired input is inaccessible");
        ExpectStale(() => retireIo.OutputAtEof(retireActor), "retired output is inaccessible");
        var retireFresh = retireMailbox.Restart();
        ExpectStale(() => retireIo.TryFeedInput(retireFresh, new byte[] { 1 }),
            "retired envelope cannot reopen after restart");
        var retireFreshIo = new ActorIoEnvelope(retireMailbox);
        Require(retireFreshIo.TryWriteOutput(retireFresh, new byte[] { 1 }),
            "fresh incarnation gets separate output");
        retireFreshIo.Retire();

        // Direct restart without prior Stop must scrub the old generation's
        // logical I/O records and disallow its queued output reopening.
        var restartMailbox = new ActorMailbox(91003);
        var restartActor = restartMailbox.CurrentRef;
        var restartIo = new ActorIoEnvelope(restartMailbox);
        Require(restartIo.TryWriteOutput(restartActor, new byte[] { 0x40 }),
            "restart test output setup");
        var restarted = restartMailbox.Restart();
        ExpectStale(() => restartIo.TryReadOutput(restartActor, out _),
            "restart automatically retires old output");
        ExpectStale(() => restartIo.TryWriteOutput(restarted, new byte[] { 1 }),
            "restart cannot revive old envelope");
        var restartedIo = new ActorIoEnvelope(restartMailbox);
        Require(restartedIo.TryWriteOutput(restarted, new byte[] { 0x41 }),
            "restart creates independent live I/O generation");

        // Rejected foreign TryStop may not retire someone else's I/O.
        var authMailbox = new ActorMailbox(91004);
        var authActor = authMailbox.CurrentRef;
        var authIo = new ActorIoEnvelope(authMailbox);
        Require(!authMailbox.TryStop(restarted), "foreign TryStop is rejected");
        Require(authIo.TryWriteOutput(authActor, new byte[] { 20 }),
            "foreign stop must preserve live I/O");
        Require(authMailbox.TryStop(authActor), "owned generation TryStop");
        ExpectStale(() => authIo.TryWriteOutput(authActor, new byte[] { 21 }),
            "owned TryStop must retire I/O");
        authMailbox.Dispose();

        // Dispose is terminal and revokes all logical streams even when
        // there is no previously issued Stop().
        var disposeMailbox = new ActorMailbox(91005);
        var disposeActor = disposeMailbox.CurrentRef;
        var disposeIo = new ActorIoEnvelope(disposeMailbox);
        Require(disposeIo.TryFeedInput(disposeActor, new byte[] { 9 }),
            "dispose input setup");
        disposeMailbox.Dispose();
        ExpectStale(() => disposeIo.TryReadInput(disposeActor, out _),
            "mailbox Dispose retires I/O");
        try
        {
            disposeMailbox.Restart();
            throw new Exception("disposed mailbox restart was admitted");
        }
        catch (ObjectDisposedException) { }

        // Explicit retirement before mailbox stop is safe and callback
        // re-entry stays idempotent under STOP.
        restartedIo.Retire();
        restartMailbox.Stop();
        ExpectStale(() => restartedIo.OutputAtEof(restarted),
            "explicitly retired output remains retired under STOP");

        // Race the retirement callback against a bounded producer. The
        // mailbox and I/O monitors must never be held in inverted order.
        var raceMailbox = new ActorMailbox(91006);
        var raceActor = raceMailbox.CurrentRef;
        var raceIo = new ActorIoEnvelope(raceMailbox);
        using (var started = new ManualResetEventSlim())
        {
            var writer = Task.Run(() =>
            {
                started.Set();
                for (int i = 0; i < 2048; i++)
                {
                    try
                    {
                        raceIo.TryFeedInput(raceActor, new byte[] { (byte)i });
                        raceIo.TryReadInput(raceActor, out _);
                    }
                    catch (InvalidOperationException) { return; }
                }
            });
            Require(started.Wait(TimeSpan.FromSeconds(5)), "race producer admission");
            var stopper = Task.Run(() => raceMailbox.Stop());
            Require(Task.WaitAll(new Task[] { writer, stopper },
                TimeSpan.FromSeconds(10)), "mailbox/I-O retirement lock-order deadlock");
            ExpectStale(() => raceIo.TryFeedInput(raceActor, new byte[] { 1 }),
                "concurrent stop must retire all queued logical I/O");
        }

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
