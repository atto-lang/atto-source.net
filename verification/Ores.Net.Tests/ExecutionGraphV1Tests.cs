using Ores.Net.Runtime;

internal static class ExecutionGraphV1Tests
{
    private static void Need(bool condition, string why)
    {
        if (!condition) throw new Exception("typed execution graph: " + why);
    }

    private static void MustRejectImportedResult<T>(
        ExecutionGraphV1.Node[] nodes, ExecutionGraphV1.Kind kind)
    {
        // The trusted IR import constructor must fail before evaluation,
        // never admit a graph that would fail as an InvalidCastException.
        var constructor = typeof(ExecutionGraphV1.Plan<T>).GetConstructor(
            System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(ExecutionGraphV1.Node[]), typeof(int),
                typeof(ExecutionGraphV1.Kind) },
            modifiers: null) ?? throw new Exception("missing trusted plan constructor");
        try
        {
            _ = constructor.Invoke(new object[] { nodes, nodes.Length - 1, kind });
        }
        catch (System.Reflection.TargetInvocationException error)
            when (error.InnerException is ArgumentException)
        {
            return;
        }
        throw new Exception("forged Plan<T> result kind was admitted");
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

        // Trusted imported IR must be an immutable snapshot, not a shallow
        // copy of caller-owned IReadOnlyList<int> operands.
        var originalEdges = new List<int> { 0, 1 };
        var imported = new[] {
            new ExecutionGraphV1.Node(0, ExecutionGraphV1.Domain.Cpu,
                ExecutionGraphV1.Operation.ConstantI64, ExecutionGraphV1.Kind.HostI64,
                Array.Empty<int>(), 20),
            new ExecutionGraphV1.Node(1, ExecutionGraphV1.Domain.Cpu,
                ExecutionGraphV1.Operation.ConstantI64, ExecutionGraphV1.Kind.HostI64,
                Array.Empty<int>(), 22),
            new ExecutionGraphV1.Node(2, ExecutionGraphV1.Domain.Cpu,
                ExecutionGraphV1.Operation.AddI64, ExecutionGraphV1.Kind.HostI64,
                originalEdges, 0)
        };
        // Verification-only reflection reaches the internal import
        // constructor without making arbitrary graph construction public.
        var importConstructor = typeof(ExecutionGraphV1.Plan<long>).GetConstructor(
            System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(ExecutionGraphV1.Node[]), typeof(int),
                typeof(ExecutionGraphV1.Kind) },
            modifiers: null) ?? throw new Exception("trusted plan constructor not found");
        var frozen = (ExecutionGraphV1.Plan<long>)importConstructor.Invoke(
            new object[] { imported, 2, ExecutionGraphV1.Kind.HostI64 });
        originalEdges[0] = 999999;
        imported[0] = imported[0] with { Constant = -100 };
        Need(frozen.ExecuteCpu() == 42, "caller mutation changed admitted graph");
        Need(frozen.Nodes[2].Inputs[0] == 0, "operand snapshot retained alias");
        Need(frozen.Nodes[2].Inputs is System.Collections.Generic.IList<int> readOnly &&
             readOnly.IsReadOnly, "plan edges must be immutable");

        // Trusted IR imports must bind the static CLR type to the declared
        // graph kind; previously Plan<long> over Unit passed construction and
        // failed only at ExecuteCpu() as an unchecked InvalidCastException.
        var importedUnit = new[] {
            new ExecutionGraphV1.Node(0, ExecutionGraphV1.Domain.Cpu,
                ExecutionGraphV1.Operation.Unit, ExecutionGraphV1.Kind.Unit,
                Array.Empty<int>(), 0)
        };
        MustRejectImportedResult<long>(importedUnit, ExecutionGraphV1.Kind.Unit);
        MustRejectImportedResult<ExecutionGraphV1.Unit>(imported,
            ExecutionGraphV1.Kind.HostI64);
        MustRejectImportedResult<ExecutionGraphV1.DeviceI64>(imported,
            ExecutionGraphV1.Kind.HostI64);
        MustRejectImportedResult<object>(imported, ExecutionGraphV1.Kind.HostI64);
        // Positive typed IR remains admissible for the actual Unit singleton.
        var unitImport = typeof(ExecutionGraphV1.Plan<ExecutionGraphV1.Unit>)
            .GetConstructor(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(ExecutionGraphV1.Node[]), typeof(int),
                    typeof(ExecutionGraphV1.Kind) }, modifiers: null)
            ?? throw new Exception("unit import constructor missing");
        var checkedUnit = (ExecutionGraphV1.Plan<ExecutionGraphV1.Unit>)
            unitImport.Invoke(new object[] { importedUnit, 0, ExecutionGraphV1.Kind.Unit });
        Need(ReferenceEquals(checkedUnit.ExecuteCpu(), ExecutionGraphV1.Unit.Instance),
            "valid imported Unit result rejected");

        // An executor may incorrectly dispatch its one-shot task twice;
        // only the first claimant is permitted to evaluate it.
        Action? scheduled = null;
        var once = plan.SubmitCpu(action => scheduled = action);
        Need(scheduled is not null, "executor did not receive task");
        var first = new Thread(() => scheduled!());
        var second = new Thread(() => scheduled!());
        first.Start();
        second.Start();
        first.Join();
        second.Join();
        Need(once.Task.GetAwaiter().GetResult() == 42, "duplicate dispatch result");

        // Match the native Forge/LLVM host-graph stress admission contract:
        // duplicate executor delivery must be one-shot under 128 real races.
        for (int trial = 0; trial < 128; trial++)
        {
            Action? duplicate = null;
            var single = plan.SubmitCpu(action => duplicate = action);
            Need(duplicate is not null, "missing duplicate-dispatch task");
            var left = new Thread(() => duplicate!());
            var right = new Thread(() => duplicate!());
            left.Start();
            right.Start();
            left.Join();
            right.Join();
            Need(single.Task.IsCompletedSuccessfully &&
                 single.Task.GetAwaiter().GetResult() == 42,
                "duplicate task scheduled twice or lost completion");
        }

        // Competing cancellation/claim races must resolve to exactly one
        // terminal outcome: successful one-shot i64 or cancellation.
        for (int trial = 0; trial < 64; trial++)
        {
            Action? action = null;
            var racing = plan.SubmitCpu(submitted => action = submitted);
            Need(action is not null, "missing cancellation-race task");
            var worker = new Thread(() => action!());
            worker.Start();
            _ = racing.Cancel();
            worker.Join();
            Need(racing.Task.IsCompleted, "racing Future left pending");
            Need(racing.Task.IsCanceled || racing.Task.IsCompletedSuccessfully,
                "racing Future had unexpected terminal state");
            if (racing.Task.IsCompletedSuccessfully)
                Need(racing.Task.GetAwaiter().GetResult() == 42,
                    "racing Future had incorrect result");
        }

        var rejected = plan.SubmitCpu(_ => throw new InvalidOperationException("executor full"));
        Need(rejected.Task.IsFaulted, "scheduler admission fails Future");

        Action? queuedBeforeFailure = null;
        var lateRejected = plan.SubmitCpu(action =>
        {
            queuedBeforeFailure = action;
            throw new InvalidOperationException("rejected after enqueue");
        });
        Need(queuedBeforeFailure is not null && lateRejected.Task.IsFaulted,
            "late executor rejection must fail the Future");
        queuedBeforeFailure!();
        Need(lateRejected.Task.IsFaulted, "late queued dispatch published after rejection");

        // Even the host-only graph needs a committed scheduling boundary:
        // a hostile executor may run a worker concurrently, then throw.
        var inline = plan.SubmitCpu(job => job());
        Need(inline.Task.IsFaulted &&
             inline.Task.Exception?.InnerException?.Message.Contains("inline dispatch rejected") == true,
             "inline executor was not rejected before CPU evaluation");

        var inlineThenThrow = plan.SubmitCpu(job =>
        {
            job();
            throw new InvalidOperationException("actual inline-then-throw rejection");
        });
        Need(inlineThenThrow.Task.IsFaulted &&
             inlineThenThrow.Task.Exception?.InnerException?.Message ==
                 "actual inline-then-throw rejection",
             "inline-then-throw did not preserve scheduler failure");

        for (int trial = 0; trial < 128; trial++)
        {
            using var workerLaunched = new System.Threading.ManualResetEventSlim(false);
            Thread? earlyWorker = null;
            var raceRejected = plan.SubmitCpu(job =>
            {
                earlyWorker = new Thread(() =>
                {
                    workerLaunched.Set();
                    job();
                });
                earlyWorker.Start();
                Need(workerLaunched.Wait(TimeSpan.FromSeconds(5)),
                     "early worker never launched");
                throw new InvalidOperationException("worker-start-then-reject");
            });
            Need(earlyWorker is not null && earlyWorker.Join(TimeSpan.FromSeconds(5)),
                 "early worker stranded at scheduler admission");
            Need(raceRejected.Task.IsFaulted &&
                 raceRejected.Task.Exception?.InnerException?.Message == "worker-start-then-reject",
                 "started worker published a result before scheduler rejection");
        }

        var limited = new ExecutionGraphV1.Builder();
        for (int i = 0; i < 4096; i++) limited.CpuUnit();
        try { limited.CpuUnit(); throw new Exception("node limit bypassed"); }
        catch (ArgumentException) { }

        Console.WriteLine("typed .NET CPU/GPU graph host contract: PASS");
    }
}
