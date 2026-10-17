using Ores.Net.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Region = Ores.Net.Runtime.OwnedF32RegionV1;
using Bridge = Ores.Net.Runtime.OwnedF32GraphV1;

internal static class OwnedF32GraphV1Tests
{
    private static long _checks;
    private static void Need(bool condition, string why)
    {
        Interlocked.Increment(ref _checks);
        if (!condition) throw new Exception("owned F32 graph: " + why);
    }

    private static void Reject(Task<long> task, string why)
    {
        Interlocked.Increment(ref _checks);
        try { _ = task.GetAwaiter().GetResult(); }
        catch (Exception) { return; }
        throw new Exception("owned F32 graph admitted: " + why);
    }

    private static ExecutionGraphV1.Plan<long> Answer()
    {
        var builder = new ExecutionGraphV1.Builder();
        return builder.Build(builder.CpuAdd(builder.CpuI64(20), builder.CpuI64(22)));
    }

    internal static void Run()
    {
        foreach (Region.Layout layout in Enum.GetValues<Region.Layout>())
        {
            var shape = new Region.Shape(layout, 9, 3,
                layout == Region.Layout.AoSoA ? 4 : 1);
            var input = new Region.HostBuffer(shape);
            var output = new Region.HostBuffer(shape);
            for (int row = 0; row < 9; ++row)
                for (int field = 0; field < 3; ++field)
                    input.Write(row, field, row * 3 + field);

            Region.Area source = input.Region(Region.Mode.Read, 1, 2, 6);
            Region.Area destination = output.Region(Region.Mode.Write, 1, 2, 6);
            Action? queued = null;
            Action<Action> schedule = action => queued = action;

            Bridge.Ticket cancelled = Bridge.Submit(
                input, output, source, destination, 2, 1, Answer(), schedule);
            Need(queued is not null && !cancelled.Task.IsCompleted,
                "preclaim task missing");
            Need(cancelled.Cancel(), "preclaim cancellation not accepted");
            queued!();
            Reject(cancelled.Task, "cancelled task published");
            Need(output.Read(2, 1) == 0, "cancelled task mutated output");

            Bridge.Ticket good = Bridge.Submit(
                input, output, source, destination, 2, 1, Answer(), schedule);
            Action callback = queued ?? throw new Exception("missing accepted callback");
            callback();
            callback(); // hostile duplicate delivery
            Need(good.Task.GetAwaiter().GetResult() == 42, "I64 result");
            Need(!good.Cancel(), "completed ticket accepted cancellation");
            for (int row = 0; row < 9; ++row)
                for (int field = 0; field < 3; ++field)
                    Need(output.Read(row, field) ==
                        (field == 1 && row >= 2 && row < 6 ?
                            2 * (row * 3 + field) + 1 : 0), "F32 graph slice");

            queued = null;
            var gpu = new ExecutionGraphV1.Builder();
            var device = gpu.UploadI64(gpu.CpuI64(1));
            var hostAgain = gpu.DownloadI64(device);
            Bridge.Ticket forbidden = Bridge.Submit(
                input, output, source, destination, 1, 0,
                gpu.Build(hostAgain), schedule);
            Reject(forbidden.Task, "GPU graph fell back to CPU");
            Need(queued is null, "GPU graph reached scheduler");

            Bridge.Ticket wrongOwner = Bridge.Submit(
                output, input, source, destination, 1, 0, Answer(), schedule);
            Reject(wrongOwner.Task, "foreign area ownership");
            Need(queued is null, "foreign area reached scheduler");

            using (var readLoan = output.BorrowRead())
            {
                Bridge.Ticket blocked = Bridge.Submit(
                    input, output, source, destination, 1, 0, Answer(), schedule);
                queued!();
                Reject(blocked.Task, "read-borrowed output overwritten");
                Need(readLoan.Read(2, 1) == 15, "failed physical loan changed bytes");
            }

            queued = null;
            Bridge.Ticket rejected = Bridge.Submit(
                input, output, source, destination, 1, 0, Answer(),
                action =>
                {
                    queued = action;
                    throw new InvalidOperationException("executor full after enqueue");
                });
            Reject(rejected.Task, "throw-after-enqueue scheduler");
            Need(queued is not null, "throwing scheduler did not retain callback");
            output.Write(2, 1, 123);
            queued!();
            Need(output.Read(2, 1) == 123, "late rejected callback mutated bytes");

            Bridge.Ticket inline = Bridge.Submit(
                input, output, source, destination, 1, 0, Answer(),
                action => action());
            Reject(inline.Task, "inline executor admitted");

            Bridge.Ticket inlineThrow = Bridge.Submit(
                input, output, source, destination, 1, 0, Answer(),
                action =>
                {
                    action();
                    throw new InvalidOperationException("inline executor actual failure");
                });
            try
            {
                _ = inlineThrow.Task.GetAwaiter().GetResult();
                throw new Exception("inline-then-throw unexpectedly succeeded");
            }
            catch (InvalidOperationException error)
            {
                Need(error.Message == "inline executor actual failure",
                    "inline rejection masked scheduler exception");
            }

            for (int trial = 0; trial < 64; ++trial)
            {
                using var launched = new System.Threading.ManualResetEventSlim(false);
                Thread? early = null;
                Bridge.Ticket acceptedEarly = Bridge.Submit(
                    input, output, source, destination, 1, 0, Answer(),
                    action =>
                    {
                        early = new Thread(() =>
                        {
                            launched.Set();
                            action();
                        });
                        early.Start();
                        Need(launched.Wait(TimeSpan.FromSeconds(5)),
                            "accepted early worker never launched");
                        // Normal return commits admission.
                    });
                Need(early is not null && early.Join(TimeSpan.FromSeconds(5)),
                    "accepted early worker stranded at admission gate");
                Need(acceptedEarly.Task.GetAwaiter().GetResult() == 42,
                    "accepted early worker did not execute after admission");
            }

            for (int trial = 0; trial < 64; ++trial)
            {
                using var launched = new System.Threading.ManualResetEventSlim(false);
                Thread? early = null;
                Bridge.Ticket rejectedEarly = Bridge.Submit(
                    input, output, source, destination, 1, 0, Answer(),
                    action =>
                    {
                        early = new Thread(() =>
                        {
                            launched.Set();
                            action();
                        });
                        early.Start();
                        Need(launched.Wait(TimeSpan.FromSeconds(5)),
                            "rejected early worker never launched");
                        throw new InvalidOperationException(
                            "owned worker began before rejection");
                    });
                Need(early is not null && early.Join(TimeSpan.FromSeconds(5)),
                    "rejected early worker stranded at admission gate");
                try
                {
                    _ = rejectedEarly.Task.GetAwaiter().GetResult();
                    throw new Exception("rejected early worker unexpectedly succeeded");
                }
                catch (InvalidOperationException error)
                {
                    Need(error.Message == "owned worker began before rejection",
                        "started-worker scheduler rejection was masked");
                }
            }

            for (int trial = 0; trial < 64; ++trial)
            {
                Bridge.Ticket once = Bridge.Submit(
                    input, output, source, destination, 1, 0, Answer(), schedule);
                callback = queued ?? throw new Exception("missing duplicate-race callback");
                Task.WaitAll(Task.Run(callback), Task.Run(callback));
                Need(once.Task.GetAwaiter().GetResult() == 42,
                    "duplicate callback changed result");
            }

            for (int trial = 0; trial < 64; ++trial)
            {
                Bridge.Ticket racing = Bridge.Submit(
                    input, output, source, destination, 1, 0, Answer(), schedule);
                callback = queued ?? throw new Exception("missing cancellation-race callback");
                Task worker = Task.Run(callback);
                _ = racing.Cancel();
                worker.GetAwaiter().GetResult();
                Need(racing.Task.IsCompleted, "cancel/claim race remained pending");
                try
                {
                    Need(racing.Task.GetAwaiter().GetResult() == 42,
                        "unexpected successful race result");
                }
                catch (OperationCanceledException)
                {
                    Need(racing.Task.IsCanceled, "cancelled race was not terminal");
                }
            }

            // Closing after structural preflight but before queued execution
            // cannot be bypassed by the retained callback.
            var closeOutput = new Region.HostBuffer(shape);
            Region.Area closeDestination =
                closeOutput.Region(Region.Mode.Write, 1, 2, 6);
            Bridge.Ticket closeRace = Bridge.Submit(
                input, closeOutput, source, closeDestination, 1, 0, Answer(), schedule);
            callback = queued ?? throw new Exception("missing close-race callback");
            closeOutput.Close();
            callback();
            Reject(closeRace.Task, "closed allocation executed after preflight");
        }

        Console.WriteLine($"PASS {_checks} .NET owned F32/I64 graph assertions");
    }
}
