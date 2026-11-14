using Ores.Net.Runtime;

internal static class NativeTraceContinuationV1SelfTest
{
    private static int checks;

    private static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception("Native tracing parity: " + message);
    }

    private static void Reject<T>(Action action, string message) where T : Exception
    {
        checks++;
        try { action(); }
        catch (Exception error) when (error is T) { return; }
        throw new Exception("Expected rejection: " + message);
    }

    public static void Run()
    {
        Require(!NativeTraceParentV1.TryParse(null, out _), "missing parent");
        Require(!NativeTraceParentV1.TryParse("00-", out _), "truncated parent");
        Require(!NativeTraceParentV1.TryParse(
            "00-00000000000000000000000000000000-0123456789abcdef-01", out _),
            "zero trace id denied");
        Require(!NativeTraceParentV1.TryParse(
            "00-0123456789abcdef0123456789abcdef-0000000000000000-01", out _),
            "zero span id denied");
        Require(!NativeTraceParentV1.TryParse(
            "01-0123456789abcdef0123456789abcdef-0123456789abcdef-01", out _),
            "version 01 denied");
        Require(!NativeTraceParentV1.TryParse(
            "00-0123456789ABCDEF0123456789abcdef-0123456789abcdef-01", out _),
            "noncanonical uppercase denied");
        Require(NativeTraceParentV1.TryParse(
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-03",
            out var input), "valid W3C context parsed");
        Require(input.ToString() ==
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-03",
            "W3C format roundtrip");

        // A malformed incoming traceparent is an untrusted, frequent case.
        // Verify the entire negative parser path stays on the managed-heap
        // allocation-free path after warmup, not merely the disabled tracer.
        string[] malformed = {
            "01-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
            "00-00000000000000000000000000000000-0123456789abcdef-01",
            "00-0123456789abcdef0123456789abcdef-0000000000000000-01",
            "00-0123456789ABCDEF0123456789abcdef-0123456789abcdef-01",
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-0g",
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-",
            "wrong"
        };
        for (int i = 0; i < 256; i++)
            foreach (string candidate in malformed)
                if (NativeTraceParentV1.TryParse(candidate, out _))
                    throw new Exception("invalid warmup traceparent was accepted");
        long rejectedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool rejectedAll = true;
        for (int i = 0; i < 10_000; i++)
            foreach (string candidate in malformed)
                if (NativeTraceParentV1.TryParse(candidate, out _))
                    rejectedAll = false;
        long rejectedBytes = GC.GetAllocatedBytesForCurrentThread() - rejectedBefore;
        Require(rejectedAll, "all malformed W3C headers rejected");
        Require(rejectedBytes == 0,
            "70,000 rejected W3C headers must allocate zero CLR bytes on this carrier");

        var off = new NativeTraceContinuationV1();
        var offOwner = off.MintOwner(1);
        Require(off.Begin(offOwner, 1, 2) is null && off.Active == 0,
            "off-by-default no scope");
        Reject<ArgumentOutOfRangeException>(() => off.MintOwner(0),
            "zero generation cannot create owner");

        var tracer = new NativeTraceContinuationV1(enabled: true,
            maxActive: 2, maxReceipts: 2);
        var owner = tracer.MintOwner(7);
        var unrelated = tracer.MintOwner(7);
        // Public value types may be constructed without using TryParse;
        // invalid parent identities must fail before scope reservation.
        var forgedParent = new NativeTraceParentV1(
            "00000000000000000000000000000000", "0123456789abcdef", 1);
        Reject<FormatException>(() => tracer.Begin(owner, 7, 10, forgedParent),
            "zero-identity constructed parent rejected");
        Reject<FormatException>(() => tracer.Begin(owner, 7, 10,
            new NativeTraceParentV1("0123456789abcdef0123456789abcdef", "ABCDEF0123456789", 1)),
            "uppercase constructed parent rejected");
        Require(tracer.Active == 0 && tracer.Completed == 0 && tracer.Dropped == 0,
            "invalid constructed parents never reserve a trace scope");
        var scope = tracer.Begin(owner, 7, 10, input)!;
        Require(scope.Parent == input, "same exact immutable parent");
        Require(scope.Context.TraceId == input.TraceId &&
            scope.Context.Flags == input.Flags, "child preserves W3C trace and flags");
        Require(scope.Context.SpanId != input.SpanId, "child gets unique span");
        Require(NativeTraceParentV1.TryParse(scope.Context.ToString(), out var parsed) &&
            parsed == scope.Context, "cryptographic child W3C roundtrip");
        Require(!tracer.Suspend(unrelated, scope, 7), "foreign owner cannot suspend");
        Require(!tracer.Suspend(owner, scope, 8), "stale generation cannot suspend");
        Require(tracer.Suspend(owner, scope, 7), "first suspension");
        Require(!tracer.Suspend(owner, scope, 7), "no double suspension");
        bool onSecondCarrier = false;
        var thread = new Thread(() => onSecondCarrier = tracer.Resume(owner, scope, 7));
        thread.Start(); thread.Join();
        Require(onSecondCarrier, "explicit owner can resume after carrier migration");
        Require(!tracer.Resume(owner, scope, 7), "no double resume");

        var second = tracer.Begin(owner, 7, 11, scope.Context)!;
        Require(second.Parent == scope.Context, "nested explicit parent");
        Require(tracer.Begin(owner, 7, 12) is null && tracer.Dropped == 1,
            "bounded active scope admission");
        Require(tracer.Finish(owner, second, 7, NativeTraceContinuationV1.Outcome.Error),
            "nested error exactly once");
        Require(!tracer.Finish(owner, second, 7, NativeTraceContinuationV1.Outcome.Ok),
            "duplicate finish denied");
        Require(tracer.Finish(owner, scope, 7, NativeTraceContinuationV1.Outcome.Ok),
            "root completed");
        Require(tracer.Completed == 2 && tracer.Active == 0,
            "two scopes retired and accounted");
        var receiptOne = tracer.PollReceipt();
        var receiptTwo = tracer.PollReceipt();
        Require(receiptOne.HasValue && receiptTwo.HasValue &&
            receiptOne.Value.Result == NativeTraceContinuationV1.Outcome.Error &&
            receiptTwo.Value.Result == NativeTraceContinuationV1.Outcome.Ok,
            "bounded FIFO terminal receipts");
        Require(!tracer.PollReceipt().HasValue, "no duplicate receipt");

        var suspended = tracer.Begin(owner, 7, 13)!;
        Require(tracer.Suspend(owner, suspended, 7), "retire suspended scope");
        tracer.Retire(owner);
        Require(tracer.Active == 0 && tracer.Completed == 3,
            "supervisor retirement forces exactly one terminal completion");
        Require(tracer.PollReceipt()?.Result ==
            NativeTraceContinuationV1.Outcome.Cancelled, "retirement receipt cancelled");
        Require(!tracer.Resume(owner, suspended, 7), "retired task cannot resurrect");
        Require(!tracer.Finish(owner, suspended, 7, NativeTraceContinuationV1.Outcome.Ok),
            "retired task cannot finish again");
        Reject<InvalidOperationException>(() => tracer.Begin(owner, 7, 14),
            "revoked owner cannot create scope");
        Reject<InvalidOperationException>(() => tracer.Begin(unrelated, 9, 14),
            "stale owner generation denied");
        tracer.Retire(owner);
        Require(tracer.Active == 0 && tracer.Completed == 3,
            "repeat retire is idempotent");

        var tiny = new NativeTraceContinuationV1(true, 1, 1);
        var smallOwner = tiny.MintOwner(1);
        for (int i = 0; i < 3; i++)
        {
            var a = tiny.Begin(smallOwner, 1, 1)!;
            Require(tiny.Finish(smallOwner, a, 1, NativeTraceContinuationV1.Outcome.Ok),
                "small active slot reopens");
        }
        Require(tiny.Completed == 3 && tiny.Dropped == 2,
            "receipt overflow accounted independently from completion");
        Require(tiny.PollReceipt().HasValue && !tiny.PollReceipt().HasValue,
            "strict receipt limit 1");
        // Race normal completion vs supervisor teardown, verifying exactly one
        // receipt and no actor generation resurrection.
        for (int n = 0; n < 128; n++)
        {
            var racer = new NativeTraceContinuationV1(true, 1, 1);
            var ticket = racer.MintOwner(1);
            var active = racer.Begin(ticket, 1, 2)!;
            var t1 = new Thread(() =>
                racer.Finish(ticket, active, 1, NativeTraceContinuationV1.Outcome.Ok));
            var t2 = new Thread(() => racer.Retire(ticket));
            t1.Start(); t2.Start(); t1.Join(); t2.Join();
            Require(racer.Active == 0 && racer.Completed == 1 &&
                racer.PollReceipt().HasValue && !racer.PollReceipt().HasValue,
                "exactly once across retire vs finish race");
        }
        Console.WriteLine("PASS .NET NativeAOT host W3C continuation scopes: " + checks + " checks");
    }
}
