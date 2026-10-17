using Ores.Net.Runtime;

static class WorkerOutputIsolationSelfTest
{
    public static void Run()
    {
        GuestLogsCannotCorruptProtocol();
        GuestOutputLimitRetiresWorker();
        HostLimitsCanOnlyTighten();
        EnvironmentSettingsFailClosed();
    }

    private static void GuestLogsCannotCorruptProtocol()
    {
        using var input = new MemoryStream();
        WorkerProtocol.WriteFrame(input, WorkerFrame.Invoke(1, 2_000));
        WorkerProtocol.WriteFrame(input, WorkerFrame.Shutdown());
        input.Position = 0;

        using var output = new MemoryStream();
        using var logs = new StringWriter();
        var exit = WorkerRuntime.Run(
            () =>
            {
                Console.Out.Write("guest-stdout");
                Console.Error.Write("guest-stderr");
                return 42;
            },
            input,
            output,
            logs,
            () => 1_000,
            new WorkerRuntimeSettings(1_000, 128));

        Require(exit == 0, "bounded guest logging keeps healthy worker alive");
        Require(logs.ToString() == "guest-stdoutguest-stderr",
            "guest console output is separated into the worker log channel");

        output.Position = 0;
        Require(WorkerProtocol.ReadFrame(output) == WorkerFrame.Ready(),
            "ORW1 ready remains first stdout bytes");
        Require(WorkerProtocol.ReadFrame(output) == WorkerFrame.Response(1, "42"),
            "guest logging cannot corrupt ORW1 response");
        Require(WorkerProtocol.ReadFrame(output) is null,
            "no guest text leaked onto protocol stdout");
    }

    private static void GuestOutputLimitRetiresWorker()
    {
        using var input = new MemoryStream();
        WorkerProtocol.WriteFrame(input, WorkerFrame.Invoke(2, 2_000));
        WorkerProtocol.WriteFrame(input, WorkerFrame.Invoke(3, 2_000));
        input.Position = 0;

        using var output = new MemoryStream();
        using var logs = new StringWriter();
        var calls = 0;
        var exit = WorkerRuntime.Run(
            () =>
            {
                calls++;
                Console.Out.Write("12345");
                return 1;
            },
            input,
            output,
            logs,
            () => 1_000,
            new WorkerRuntimeSettings(1_000, 4));

        Require(exit == 70 && calls == 1,
            "output quota violation retires worker before queued reuse");
        Require(logs.ToString() == "",
            "over-limit write is rejected before partial log publication");
        output.Position = 0;
        Require(WorkerProtocol.ReadFrame(output) == WorkerFrame.Ready(),
            "output quota test ready frame");
        Require(WorkerProtocol.ReadFrame(output) ==
            WorkerFrame.Error(2, "guest-output-limit"),
            "output quota failure is an opaque bounded error code");
        Require(WorkerProtocol.ReadFrame(output) is null,
            "retired worker cannot serve later queued request");
    }

    private static void HostLimitsCanOnlyTighten()
    {
        using var input = new MemoryStream();
        WorkerProtocol.WriteFrame(input, WorkerFrame.Invoke(4, 1_101));
        WorkerProtocol.WriteFrame(input, WorkerFrame.Shutdown());
        input.Position = 0;

        using var output = new MemoryStream();
        using var logs = new StringWriter();
        var calls = 0;
        var exit = WorkerRuntime.Run(
            () => { calls++; return 1; },
            input,
            output,
            logs,
            () => 1_000,
            new WorkerRuntimeSettings(100, 128));

        Require(exit == 0 && calls == 0,
            "tightened host deadline rejects guest before entry");
        output.Position = 0;
        Require(WorkerProtocol.ReadFrame(output) == WorkerFrame.Ready(),
            "tight deadline test ready");
        Require(WorkerProtocol.ReadFrame(output) ==
            WorkerFrame.Error(4, "deadline-exceeds-worker-limit"),
            "runtime uses host-tightened deadline instead of protocol maximum");

        RejectRange(
            () => new WorkerRuntimeSettings(
                WorkerProtocol.MaxInvocationMilliseconds + 1,
                1).Validate(),
            "host cannot widen invocation ceiling");
        RejectRange(
            () => new WorkerRuntimeSettings(
                1,
                WorkerProtocol.MaxPayloadBytes + 1).Validate(),
            "host cannot widen guest output ceiling");
    }

    private static void EnvironmentSettingsFailClosed()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ORESLANG_WORKER_PROTOCOL"] = WorkerProtocol.ReadyPayload,
            ["ORESLANG_WORKER_MAX_WALL_MS"] = "500",
            ["ORESLANG_WORKER_MAX_OUTPUT_BYTES"] = "4096"
        };
        var settings = WorkerRuntimeSettings.FromEnvironment(
            name => env.TryGetValue(name, out var value) ? value : null);
        Require(settings == new WorkerRuntimeSettings(500, 4096),
            "sanitized worker environment tightens both limits");

        env["ORESLANG_WORKER_PROTOCOL"] = "other-protocol";
        RejectFormat(
            () => WorkerRuntimeSettings.FromEnvironment(
                name => env.TryGetValue(name, out var value) ? value : null),
            "protocol identity mismatch");

        env["ORESLANG_WORKER_PROTOCOL"] = WorkerProtocol.ReadyPayload;
        env["ORESLANG_WORKER_MAX_WALL_MS"] = "30001";
        RejectFormat(
            () => WorkerRuntimeSettings.FromEnvironment(
                name => env.TryGetValue(name, out var value) ? value : null),
            "environment cannot widen deadline");
    }

    private static void RejectFormat(Action action, string why)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Expected FormatException: " + why);
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
