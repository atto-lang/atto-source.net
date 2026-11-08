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
        var siblingA = new BatchedCheckpointV1(129, (_, _) => { });
        var siblingB = new BatchedCheckpointV1(257, (_, _) => { });
        for (int i = 0; i < 129; i++)
        {
            siblingA.CompletedWorkUnit();
            siblingB.CompletedWorkUnit();
        }
        Require(siblingA.RemainingFuel == 0 && siblingB.RemainingFuel == 128,
            "logical siblings cannot refill each other");
        Reject(() => siblingA.CompletedWorkUnit(), "fuel exhausted");
        for (int i = 0; i < 128; i++) siblingB.CompletedWorkUnit();
        Require(siblingB.RemainingFuel == 0, "sibling remaining quota");
        Reject(() => siblingB.CompletedWorkUnit(), "fuel exhausted");

        RunDeterministicModelMatrix();
        Console.WriteLine("PASS batched checkpoint 64/128/256/512 + 204-case differential matrix (host-only; NOT guest execution)");
    }

    private static void RunDeterministicModelMatrix()
    {
        int[] intervals = { 64, 128, 256, 512 };
        ulong[] lengths = { 0, 1, 2, 63, 64, 65, 127, 128, 129,
            255, 256, 257, 511, 512, 513, 1023, 1024, 1025, 4097 };
        uint seed = 0x1eaf4b37;
        foreach (int interval in intervals)
        {
            foreach (ulong fuel in lengths) CheckOne(interval, fuel);
            for (int i = 0; i < 32; i++)
            {
                seed = unchecked(seed * 1664525u + 1013904223u);
                CheckOne(interval, seed % 2049u);
            }
        }
    }

    private static void CheckOne(int interval, ulong fuel)
    {
        var receipts = new List<(ulong Charge, ulong Remaining)>();
        var quota = new BatchedCheckpointV1(fuel,
            (charge, remaining) => receipts.Add((charge, remaining)), interval);
        for (ulong completed = 1; completed <= fuel; completed++)
        {
            bool expected = completed % (ulong)interval == 0 || completed == fuel;
            Require(quota.CompletedWorkUnit() == expected, "incorrect poll boundary");
            Require(quota.RemainingFuel == fuel - completed, "fuel drift");
        }
        var model = new List<(ulong Charge, ulong Remaining)>();
        for (ulong boundary = (ulong)interval; boundary < fuel; boundary += (ulong)interval)
            model.Add(((ulong)interval, fuel - boundary));
        if (fuel > 0)
            model.Add((fuel % (ulong)interval == 0 ? (ulong)interval :
                fuel % (ulong)interval, 0));
        Require(receipts.SequenceEqual(model), "slow poll model mismatch");
        Reject(() => quota.CompletedWorkUnit(), "fuel exhausted");
        Reject(() => quota.CompletedWorkUnit(), "terminal");
        Require(receipts.SequenceEqual(model), "exhaustion called trusted poll");
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
