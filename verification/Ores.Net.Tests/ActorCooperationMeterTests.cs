using Ores.Net.Runtime;

public static class ActorCooperationMeterTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Actor cooperation: " + message);
    }

    public static void Run()
    {
        foreach (int interval in new[] {128, 512})
        {
            var meter = new ActorCooperationMeter(interval);
            int polls = 0;
            for (int i = 1; i <= 4096; i++)
            {
                int iteration = i;
                bool due = meter.CommitBatch(1, (count, overdue) =>
                {
                    polls++;
                    Require(count == (ulong)iteration, "checkpoint reduction identity");
                    Require(overdue == (iteration >= 512), "overdue admission");
                });
                Require(due == (i % interval == 0), "no per-iteration slow poll");
            }
            Require(polls == 4096 / interval, "exact number of expensive polls");
            Require(meter.Completed == 4096, "committed work");
            meter.BeginDispatch();
            Require(meter.Completed == 0 && meter.Phase == 0, "dispatch reset");
        }

        var actorA = new ActorCooperationMeter();
        var actorB = new ActorCooperationMeter();
        int countA = 0, countB = 0;
        actorA.CommitBatch(127, (_, _) => countA++);
        actorB.CommitBatch(128, (_, _) => countB++);
        Require(countA == 0 && countB == 1, "actors have separate phase");
        actorA.CommitBatch(128, (_, _) => countA++);
        Require(countA == 1 && actorA.Phase == 127, "one crossed boundary");
        actorA.CommitBatch(1, (_, _) => countA++);
        Require(countA == 2 && actorA.Phase == 0, "phase wrap");

        foreach (int invalid in new[] {-1, 0, 129, int.MaxValue})
        {
            bool rejected = false;
            try { actorA.CommitBatch(invalid, (_, _) => {}); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "invalid work rejected");
        }
        Require(actorA.Completed == 256, "failed admission cannot mutate work");

        bool rejectedNull = false;
        try { actorA.CommitBatch(1, null!); }
        catch (ArgumentNullException) { rejectedNull = true; }
        Require(rejectedNull, "missing callback rejected");

        bool rejectedInterval = false;
        try { _ = new ActorCooperationMeter(64); }
        catch (ArgumentOutOfRangeException) { rejectedInterval = true; }
        Require(rejectedInterval, "unsupported interval rejected");
    }
}
