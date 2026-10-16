using Ores.Net.Runtime;

internal static class ExecutionGraphV1Tests
{
    private static void Need(bool condition, string why)
    {
        if (!condition) throw new Exception("typed execution graph: " + why);
    }

    public static void Run()
    {
        var unitBuilder = new ExecutionGraphV1.Builder();
        Need(ReferenceEquals(unitBuilder.Build(unitBuilder.CpuUnit()).ExecuteCpu(),
            ExecutionGraphV1.Unit.Instance), "empty CPU Unit is singleton");

        var cpu = new ExecutionGraphV1.Builder();
        var a = cpu.CpuI64(20L);
        var b = cpu.CpuI64(22L);
        var plan = cpu.Build(cpu.CpuAdd(a, b));
        Need(plan.ResultKind == ExecutionGraphV1.Kind.HostI64, "result kind");
        Need(plan.ExecuteCpu() == 42, "checked CPU arithmetic");
        var submitted = plan.SubmitCpu(action => action());
        Need(submitted.Task.GetAwaiter().GetResult() == 42, "nonblocking future");
        Need(!submitted.Cancel(), "cannot cancel completed result");

        var queue = new Queue<Action>();
        var cancelled = plan.SubmitCpu(queue.Enqueue);
        Need(!cancelled.Task.IsCompleted && queue.Count == 1, "pending executor task");
        Need(cancelled.Cancel(), "cancel pending future");
        queue.Dequeue()();
        Need(cancelled.Task.IsCanceled, "cancel suppresses publication");

        var unsafeGraph = new ExecutionGraphV1.Builder();
        var host = unsafeGraph.CpuI64(10);
        var device = unsafeGraph.UploadI64(host);
        var result = unsafeGraph.Build(unsafeGraph.DownloadI64(device));
        Need(!result.CpuOnly, "GPU transfer belongs to device graph");
        bool executed = false;
        var failed = result.SubmitCpu(action => executed = true);
        Need(!executed && failed.Task.IsFaulted, "GPU fails before CPU enqueue");
        try
        {
            result.ExecuteCpu();
            throw new Exception("GPU plan silently ran on CPU");
        }
        catch (NotSupportedException ex)
        {
            Need(ex.Message.Contains("no implicit CPU fallback", StringComparison.Ordinal),
                "GPU fail-closed diagnostic");
        }

        var foreign = new ExecutionGraphV1.Builder();
        var alien = foreign.CpuI64(2);
        try { cpu.CpuAdd(a, alien); throw new Exception("foreign dependency accepted"); }
        catch (ArgumentException ex)
        {
            Need(ex.Message.Contains("graph ownership", StringComparison.Ordinal),
                "foreign dependency diagnostic");
        }

        var overflow = new ExecutionGraphV1.Builder();
        var max = overflow.CpuI64(long.MaxValue);
        var one = overflow.CpuI64(1);
        try
        {
            overflow.Build(overflow.CpuAdd(max, one)).ExecuteCpu();
            throw new Exception("unchecked arithmetic overflow");
        }
        catch (OverflowException) { }

        var rejected = plan.SubmitCpu(_ => throw new InvalidOperationException("executor full"));
        Need(rejected.Task.IsFaulted, "scheduler admission fails Future");

        var limited = new ExecutionGraphV1.Builder();
        for (int i = 0; i < 4096; i++) limited.CpuUnit();
        try { limited.CpuUnit(); throw new Exception("node limit bypassed"); }
        catch (ArgumentException) { }

        Console.WriteLine("typed .NET CPU/GPU graph host contract: PASS");
    }
}
