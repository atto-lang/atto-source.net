using Ores.Net.Runtime;

static class WorkerTextAbiSelfTest
{
    public static void Run()
    {
        PayloadRoundTrip();
        OversizedResponseRetires();
        InvalidUtf8ResponseRetires();
        NullResponseRetires();
        FailureIsOpaqueAndRetires();
    }

    private static void PayloadRoundTrip()
    {
        using var input = new MemoryStream();
        WorkerProtocol.WriteFrame(input, WorkerFrame.Invoke(1, 2_000, "ping"));
        WorkerProtocol.WriteFrame(input, WorkerFrame.Invoke(2, 2_000, "other"));
        WorkerProtocol.WriteFrame(input, WorkerFrame.Shutdown());
        input.Position = 0;

        using var output = new MemoryStream();
        using var logs = new StringWriter();
        var exit = WorkerRuntime.RunText(
            value => value == "ping" ? "pong" : "unsupported",
            input,
            output,
            logs,
            () => 1_000);

        Require(exit == 0, "text ABI healthy loop exits cleanly");
        output.Position = 0;
        Require(WorkerProtocol.ReadFrame(output) == WorkerFrame.Ready(),
            "text ABI emits ready");
        Require(WorkerProtocol.ReadFrame(output) ==
            WorkerFrame.Response(1, "pong"),
            "text ABI forwards invocation payload");
        Require(WorkerProtocol.ReadFrame(output) ==
            WorkerFrame.Response(2, "unsupported"),
            "text ABI preserves subsequent invocation isolation");
        Require(WorkerProtocol.ReadFrame(output) is null,
            "text ABI emits no out-of-band stdout");
    }

    private static void OversizedResponseRetires()
    {
        var giant = new string('x', WorkerProtocol.MaxPayloadBytes + 1);
        AssertRetiringError(
            _ => giant,
            "response-too-large",
            "oversized text response");
    }

    private static void InvalidUtf8ResponseRetires()
    {
        AssertRetiringError(
            _ => "\ud800",
            "response-invalid-utf8",
            "unpaired surrogate response");
    }

    private static void NullResponseRetires()
    {
        AssertRetiringError(
            _ => null!,
            "guest-null-response",
            "null text response");
    }

    private static void FailureIsOpaqueAndRetires()
    {
        AssertRetiringError(
            _ => throw new InvalidOperationException("secret guest detail"),
            "guest-failure",
            "unexpected text guest failure");
    }

    private static void AssertRetiringError(
        Func<string, string> entry,
        string expectedCode,
        string why)
    {
        using var input = new MemoryStream();
        WorkerProtocol.WriteFrame(input, WorkerFrame.Invoke(9, 2_000, "event"));
        WorkerProtocol.WriteFrame(input, WorkerFrame.Invoke(10, 2_000, "must-not-run"));
        input.Position = 0;

        using var output = new MemoryStream();
        using var logs = new StringWriter();
        var exit = WorkerRuntime.RunText(
            entry,
            input,
            output,
            logs,
            () => 1_000);

        Require(exit == 70, why + " retires generation");
        output.Position = 0;
        Require(WorkerProtocol.ReadFrame(output) == WorkerFrame.Ready(),
            why + " ready");
        Require(WorkerProtocol.ReadFrame(output) ==
            WorkerFrame.Error(9, expectedCode),
            why + " emits expected opaque error");
        Require(WorkerProtocol.ReadFrame(output) is null,
            why + " does not execute queued invocation after retirement");
    }

    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("FAILED: " + why);
    }
}
