using System.Buffers.Binary;
using System.Security;
using Ores.Net.Runtime;

static class WorkerProtocolSelfTest
{
    public static void Run()
    {
        RoundTripFrames();
        RejectMalformedFrames();
        WorkerLoopSuccess();
        WorkerLoopFailClosedCases();
        LaunchAdmission();
    }

    private static void RoundTripFrames()
    {
        var frames = new[] {
            WorkerFrame.Ready(),
            WorkerFrame.Invoke(7, 12_345, ""),
            WorkerFrame.Cancel(7),
            WorkerFrame.Response(7, "42"),
            WorkerFrame.Error(7, "deadline-expired"),
            WorkerFrame.Shutdown()
        };

        using var stream = new MemoryStream();
        foreach (var frame in frames)
            WorkerProtocol.WriteFrame(stream, frame);
        stream.Position = 0;
        foreach (var expected in frames)
            Require(WorkerProtocol.ReadFrame(stream) == expected,
                "worker frame round-trip");
        Require(WorkerProtocol.ReadFrame(stream) is null,
            "clean EOF after complete worker frames");
    }

    private static void RejectMalformedFrames()
    {
        var valid = Encoded(WorkerFrame.Response(9, "x"));

        var badMagic = (byte[])valid.Clone();
        badMagic[0] ^= 0xff;
        RejectFormat(badMagic, "bad worker magic");

        var badVersion = (byte[])valid.Clone();
        badVersion[4] = 2;
        RejectFormat(badVersion, "bad worker version");

        var badKind = (byte[])valid.Clone();
        badKind[5] = 99;
        RejectFormat(badKind, "unknown worker kind");

        var badFlags = (byte[])valid.Clone();
        badFlags[7] = 1;
        RejectFormat(badFlags, "nonzero worker flags");

        var oversized = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32BigEndian(
            oversized.AsSpan(24, 4),
            WorkerProtocol.MaxPayloadBytes + 1);
        RejectFormat(oversized, "oversized worker payload");

        var invalidUtf8 = (byte[])valid.Clone();
        invalidUtf8[^1] = 0xff;
        RejectFormat(invalidUtf8, "invalid UTF-8 worker payload");

        RejectTruncated(valid[..^1], "truncated worker payload");
        RejectTruncated(valid[..10], "truncated worker header");

        RejectWrite(
            new WorkerFrame(WorkerFrameKind.Invoke, 0, 1, ""),
            "zero request id");
        RejectWrite(
            new WorkerFrame(WorkerFrameKind.Cancel, 1, 0, "payload"),
            "cancel payload");
        RejectWrite(
            new WorkerFrame(WorkerFrameKind.Shutdown, 1, 0, ""),
            "shutdown authority");
        RejectWrite(
            new WorkerFrame(WorkerFrameKind.Error, 1, 0, "Bad Error"),
            "noncanonical error code");

        var huge = new string('x', WorkerProtocol.MaxPayloadBytes + 1);
        RejectWrite(WorkerFrame.Response(1, huge), "oversized write");
    }

    private static void WorkerLoopSuccess()
    {
        using var input = new MemoryStream();
        WorkerProtocol.WriteFrame(input, WorkerFrame.Invoke(1, 2_000));
        WorkerProtocol.WriteFrame(input, WorkerFrame.Shutdown());
        input.Position = 0;

        using var output = new MemoryStream();
        using var error = new StringWriter();
        var calls = 0;
        var exit = WorkerRuntime.Run(
            () => { calls++; return 42; },
            input,
            output,
            error,
            () => 1_000);

        Require(exit == 0 && calls == 1, "worker invokes scalar entry once");
        Require(error.ToString() == "", "successful worker has no stderr");
        output.Position = 0;
        Require(WorkerProtocol.ReadFrame(output) == WorkerFrame.Ready(),
            "worker emits ready handshake");
        Require(WorkerProtocol.ReadFrame(output) ==
            WorkerFrame.Response(1, "42"), "worker emits invariant i64 response");
        Require(WorkerProtocol.ReadFrame(output) is null,
            "worker emits no hidden stdout after shutdown");
    }

    private static void WorkerLoopFailClosedCases()
    {
        var calls = 0;
        var expired = RunOne(
            WorkerFrame.Invoke(2, 1_000),
            () => { calls++; return 1; },
            now: 1_000);
        Require(calls == 0 && expired == WorkerFrame.Error(2, "deadline-expired"),
            "expired invocation never reaches guest");

        var farFuture = RunOne(
            WorkerFrame.Invoke(3, 31_001),
            () => { calls++; return 1; },
            now: 1_000);
        Require(calls == 0 &&
            farFuture == WorkerFrame.Error(3, "deadline-exceeds-worker-limit"),
            "worker rejects deadline above 30 seconds");

        var badClock = RunOne(
            WorkerFrame.Invoke(4, 2_000),
            () => { calls++; return 1; },
            now: -1);
        Require(calls == 0 &&
            badClock == WorkerFrame.Error(4, "host-clock-invalid"),
            "invalid host clock never reaches guest");

        var payload = RunOne(
            WorkerFrame.Invoke(5, 2_000, "not-admitted"),
            () => { calls++; return 1; },
            now: 1_000);
        Require(calls == 0 &&
            payload == WorkerFrame.Error(
                5, "payload-unsupported-by-scalar-bootstrap"),
            "scalar bootstrap refuses false String ABI");

        var cancelled = RunOne(
            WorkerFrame.Cancel(6),
            () => { calls++; return 1; },
            now: 1_000);
        Require(calls == 0 &&
            cancelled == WorkerFrame.Error(
                6, "cancel-requires-cooperative-guest"),
            "synchronous bootstrap does not claim cooperative cancellation");

        var overflow = RunOne(
            WorkerFrame.Invoke(7, 2_000),
            () => throw new OverflowException(),
            now: 1_000);
        Require(overflow == WorkerFrame.Error(7, "arithmetic-overflow"),
            "worker maps arithmetic overflow without stack disclosure");

        var divide = RunOne(
            WorkerFrame.Invoke(8, 2_000),
            () => throw new DivideByZeroException(),
            now: 1_000);
        Require(divide == WorkerFrame.Error(8, "division-by-zero"),
            "worker maps division error without stack disclosure");

        using (var guestInput = new MemoryStream())
        {
            WorkerProtocol.WriteFrame(
                guestInput, WorkerFrame.Invoke(9, 2_000));
            WorkerProtocol.WriteFrame(
                guestInput, WorkerFrame.Invoke(10, 2_000));
            guestInput.Position = 0;
            using var guestOutput = new MemoryStream();
            using var guestError = new StringWriter();
            var guestCalls = 0;
            var guestExit = WorkerRuntime.Run(
                () => {
                    guestCalls++;
                    throw new InvalidOperationException("secret detail");
                },
                guestInput,
                guestOutput,
                guestError,
                () => 1_000);
            Require(guestExit == 70 && guestCalls == 1,
                "unexpected guest failure retires worker before reuse");
            guestOutput.Position = 0;
            Require(WorkerProtocol.ReadFrame(guestOutput) == WorkerFrame.Ready(),
                "guest failure test ready");
            Require(WorkerProtocol.ReadFrame(guestOutput) ==
                WorkerFrame.Error(9, "guest-failure"),
                "worker does not reflect arbitrary guest exception text");
            Require(WorkerProtocol.ReadFrame(guestOutput) is null,
                "retired worker does not serve queued invocation");
        }

        using (var replayInput = new MemoryStream())
        {
            WorkerProtocol.WriteFrame(
                replayInput, WorkerFrame.Invoke(11, 2_000));
            WorkerProtocol.WriteFrame(
                replayInput, WorkerFrame.Invoke(11, 2_000));
            WorkerProtocol.WriteFrame(replayInput, WorkerFrame.Shutdown());
            replayInput.Position = 0;
            using var replayOutput = new MemoryStream();
            using var replayError = new StringWriter();
            var replayCalls = 0;
            Require(WorkerRuntime.Run(
                () => { replayCalls++; return 11; },
                replayInput,
                replayOutput,
                replayError,
                () => 1_000) == 0,
                "replay rejection keeps healthy worker available");
            Require(replayCalls == 1,
                "replayed request id cannot execute guest twice");
            replayOutput.Position = 0;
            Require(WorkerProtocol.ReadFrame(replayOutput) == WorkerFrame.Ready(),
                "replay test ready");
            Require(WorkerProtocol.ReadFrame(replayOutput) ==
                WorkerFrame.Response(11, "11"),
                "first request id executes exactly once");
            Require(WorkerProtocol.ReadFrame(replayOutput) ==
                WorkerFrame.Error(11, "request-id-replay"),
                "duplicate request id is rejected");
        }

        using var invalidInput = new MemoryStream(new byte[] { 1, 2, 3 });
        using var invalidOutput = new MemoryStream();
        using var invalidError = new StringWriter();
        Require(WorkerRuntime.Run(
            () => 1,
            invalidInput,
            invalidOutput,
            invalidError,
            () => 1_000) == WorkerRuntime.ProtocolFailureExitCode,
            "malformed host stream terminates worker");
        Require(invalidError.ToString().Contains(
            "protocol failure", StringComparison.Ordinal),
            "protocol failure is reported only on stderr");
    }

    private static void LaunchAdmission()
    {
        var semi = WorkerLaunchManifest.StrictFaas(WorkloadTrust.SemiTrusted);
        var fakeExecutable = Path.GetFullPath("oreslang-worker-test");
        var start = WorkerProcessStartInfoFactory.Create(fakeExecutable, semi);
        Require(start.UseShellExecute is false &&
            start.RedirectStandardInput &&
            start.RedirectStandardOutput &&
            start.RedirectStandardError &&
            start.CreateNoWindow,
            "generic worker launch uses redirected no-shell stdio");
        Require(start.ArgumentList.Count == 1 && start.ArgumentList[0] == "--worker",
            "worker mode is explicit");
        Require(start.Environment.Count == 3 &&
            start.Environment["ORESLANG_WORKER_PROTOCOL"] ==
                WorkerProtocol.ReadyPayload &&
            !start.Environment.ContainsKey("PATH"),
            "worker launch clears ambient environment");

        RejectSecurity(
            () => WorkerProcessStartInfoFactory.Create(
                fakeExecutable,
                WorkerLaunchManifest.StrictFaas(WorkloadTrust.Untrusted)),
            "generic launcher cannot start untrusted worker");

        RejectUnsupported(
            () => new WorkerLaunchManifest(
                WorkloadTrust.SemiTrusted,
                WorkerResourceLimits.StrictFaas,
                WorkerCapabilities.Create("read:/tmp"))
                .ValidateForGenericLauncher(),
            "scoped capability must wait for verified enforcement adapter");

        var immutableCapabilities =
            WorkerCapabilities.Create("read:/tmp", "env:HOME");
        Require(immutableCapabilities.Grants is not string[],
            "capability grants do not expose mutable backing array");
        try
        {
            ((IList<string>)immutableCapabilities.Grants)[0] = "run:/bin/sh";
            throw new Exception("FAILED: immutable capability collection mutated");
        }
        catch (NotSupportedException) { }

        RejectFormat(
            () => WorkerCapabilities.Create("unknown:value"),
            "unknown capability kind");
        RejectFormat(
            () => WorkerCapabilities.Create("read:/tmp", "read:/tmp"),
            "duplicate capability");
        RejectFormat(
            () => WorkerCapabilities.Create("net:"),
            "empty capability value");
        RejectFormat(
            () => WorkerCapabilities.Create("read:\ud800"),
            "invalid Unicode capability");

        RejectRange(
            () => new WorkerResourceLimits(
                WorkerProtocol.MaxInvocationMilliseconds + 1,
                128L * 1024 * 1024,
                1,
                WorkerProtocol.MaxPayloadBytes).Validate(),
            "wall time");
    }

    private static WorkerFrame RunOne(
        WorkerFrame frame,
        Func<long> entry,
        long now)
    {
        using var input = new MemoryStream();
        WorkerProtocol.WriteFrame(input, frame);
        WorkerProtocol.WriteFrame(input, WorkerFrame.Shutdown());
        input.Position = 0;
        using var output = new MemoryStream();
        using var error = new StringWriter();
        Require(WorkerRuntime.Run(entry, input, output, error, () => now) == 0,
            "worker test invocation exits cleanly");
        output.Position = 0;
        Require(WorkerProtocol.ReadFrame(output) == WorkerFrame.Ready(),
            "worker test ready frame");
        return WorkerProtocol.ReadFrame(output)
            ?? throw new Exception("FAILED: expected worker response");
    }

    private static byte[] Encoded(WorkerFrame frame)
    {
        using var stream = new MemoryStream();
        WorkerProtocol.WriteFrame(stream, frame);
        return stream.ToArray();
    }

    private static void RejectFormat(byte[] encoded, string why)
    {
        try
        {
            using var stream = new MemoryStream(encoded);
            _ = WorkerProtocol.ReadFrame(stream);
        }
        catch (FormatException) { return; }
        throw new Exception("Expected FormatException: " + why);
    }

    private static void RejectTruncated(byte[] encoded, string why)
    {
        try
        {
            using var stream = new MemoryStream(encoded);
            _ = WorkerProtocol.ReadFrame(stream);
        }
        catch (EndOfStreamException) { return; }
        throw new Exception("Expected EndOfStreamException: " + why);
    }

    private static void RejectWrite(WorkerFrame frame, string why)
    {
        RejectFormat(() => {
            using var stream = new MemoryStream();
            WorkerProtocol.WriteFrame(stream, frame);
        }, why);
    }

    private static void RejectFormat(Action action, string why)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Expected FormatException: " + why);
    }

    private static void RejectSecurity(Action action, string why)
    {
        try { action(); }
        catch (SecurityException) { return; }
        throw new Exception("Expected SecurityException: " + why);
    }

    private static void RejectUnsupported(Action action, string why)
    {
        try { action(); }
        catch (NotSupportedException) { return; }
        throw new Exception("Expected NotSupportedException: " + why);
    }

    private static void RejectRange(Action action, string why)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected ArgumentOutOfRangeException: " + why);
    }

    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("FAILED: " + why);
    }
}
