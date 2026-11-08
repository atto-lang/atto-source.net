using Ores.Net.Runtime;

internal static class BatchedCheckpointV1SelfTest
{
    public static void Run()
    {
        foreach (int interval in new[] { 64, 128, 256, 512 })
        {
            var polls = new List<(ulong Charged, ulong Remaining)>();
            var checkpoint = new BatchedCheckpointV1((ulong)interval * 3,
                (charge, remaining) => polls.Add((charge, remaining)), interval);
            for (int i = 1; i < interval; i++)
            {
                Require(!checkpoint.CompletedWorkUnit(), "early slow-path poll");
                Require(polls.Count == 0, "unexpected slow-path invocation");
                Require(checkpoint.RemainingFuel == (ulong)(interval * 3 - i),
                    "pending fuel is not free");
            }
            Require(checkpoint.CompletedWorkUnit(), "expected poll threshold");
            Require(polls.Count == 1 && polls[0] ==
                ((ulong)interval, (ulong)interval * 2), "incorrect batch charge");
        }

        var charges = new List<ulong>();
        var final = new BatchedCheckpointV1(129,
            (charge, _) => charges.Add(charge));
        for (int i = 0; i < 128; i++) final.CompletedWorkUnit();
        Require(final.RemainingFuel == 1, "hard fuel after first batch");
        Require(final.CompletedWorkUnit() && final.RemainingFuel == 0,
            "final one-unit batch must not overshoot");
        Require(charges.SequenceEqual(new ulong[] { 128, 1 }),
            "charged batch sequence");
        Reject(() => final.CompletedWorkUnit(), "fuel exhausted");
        Reject(() => final.CompletedWorkUnit(), "terminal");

        var zero = new BatchedCheckpointV1(0, (_, _) =>
            throw new Exception("unreachable"));
        Reject(() => zero.CompletedWorkUnit(), "fuel exhausted");

        var huge = new BatchedCheckpointV1(ulong.MaxValue, (_, _) => {}, 512);
        for (int i = 0; i < 512; i++) huge.CompletedWorkUnit();
        Require(huge.RemainingFuel == ulong.MaxValue - 512,
            "unsigned 64-bit budget must not wrap");
        RejectRange(() => _ = new BatchedCheckpointV1(1, (_, _) => {}, 100));
        RejectArgument(() => _ = new BatchedCheckpointV1(1, null!));

        var failed = new BatchedCheckpointV1(1, (_, _) =>
            throw new InvalidOperationException("cancelled"));
        Reject(() => failed.CompletedWorkUnit(), "cancelled");
        Reject(() => failed.CompletedWorkUnit(), "terminal");

        BatchedCheckpointV1? recursive = null;
        recursive = new BatchedCheckpointV1(64,
            (_, _) => recursive!.CompletedWorkUnit(), 64);
        for (int i = 0; i < 63; i++) recursive.CompletedWorkUnit();
        Reject(() => recursive.CompletedWorkUnit(), "reentrant");
        Reject(() => recursive.CompletedWorkUnit(), "terminal");
        Console.WriteLine("PASS batched checkpoint 64/128/256/512 host-only tests (NOT guest execution)");
    }

    private static void Require(bool result, string what)
    {
        if (!result) throw new Exception("Checkpoint V1: " + what);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException e) when
            (e.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Checkpoint V1 missing negative rejection: " + message);
    }

    private static void RejectRange(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Checkpoint V1 invalid interval accepted");
    }

    private static void RejectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentNullException) { return; }
        throw new Exception("Checkpoint V1 missing trusted poll accepted");
    }
}
