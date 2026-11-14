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

        var guarded = new ActorCooperationMeter();
        bool deniedNestedCharge = false, deniedNestedReset = false;
        guarded.CommitBatch(128, (_, _) =>
        {
            try { guarded.CommitBatch(1, (_, _) => {}); }
            catch (InvalidOperationException) { deniedNestedCharge = true; }
            try { guarded.BeginDispatch(); }
            catch (InvalidOperationException) { deniedNestedReset = true; }
        });
        Require(deniedNestedCharge && deniedNestedReset, "callback reentrancy denied");
        Require(guarded.Completed == 128 && guarded.Phase == 0, "reentrancy cannot mutate state");

        bool callbackFailed = false;
        try { guarded.CommitBatch(128, (_, _) => throw new InvalidOperationException("scheduler")); }
        catch (InvalidOperationException) { callbackFailed = true; }
        Require(callbackFailed && guarded.Completed == 256 && guarded.Phase == 0,
            "exception leaves committed work with no replayable checkpoint");
        int retries = 0;
        guarded.CommitBatch(127, (_, _) => retries++);
        Require(retries == 0, "callback exception cannot force early retry");
        guarded.CommitBatch(1, (_, _) => retries++);
        Require(retries == 1, "checkpoint guard restored on exceptional path");

        // Deterministic 10k batch differential against an arithmetic oracle.
        foreach (int interval in new[] {128, 512})
        {
            var meter = new ActorCooperationMeter(interval);
            uint seed = 0x12345678u;
            ulong total = 0;
            int observed = 0;
            for (int i = 0; i < 10000; ++i)
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                int work = (int)(seed % (uint)interval) + 1;
                ulong before = total;
                bool due = meter.CommitBatch(work, (amount, overdue) =>
                {
                    observed++;
                    Require(amount == before + (ulong)work, "checkpoint amount matches committed batch");
                    Require(overdue == (amount >= 512), "batch overdue flag");
                });
                total += (ulong)work;
                Require(due == (total / (ulong)interval != before / (ulong)interval),
                    "batch crossing oracle");
                Require(meter.Completed == total && (ulong)meter.Phase == total % (ulong)interval,
                    "per-actor work and phase equal mathematical model");
            }
            Require(observed == (int)(total / (ulong)interval), "no lost or duplicate polls");
        }

        // Independent real threads race against a callback with a deliberately
        // held physical turn. A logical actor's ownership must not transfer
        // until the original scheduler callback has fully returned.
        var concurrent = new ActorCooperationMeter();
        using (var callbackEntered = new System.Threading.ManualResetEventSlim(false))
        using (var callbackRelease = new System.Threading.ManualResetEventSlim(false))
        {
            var owner = System.Threading.Tasks.Task.Run(() =>
                concurrent.CommitBatch(128, (_, _) =>
                {
                    callbackEntered.Set();
                    if (!callbackRelease.Wait(TimeSpan.FromSeconds(10)))
                        throw new Exception("Cooperation callback timed out");
                }));
            try
            {
                Require(callbackEntered.Wait(TimeSpan.FromSeconds(10)),
                    "physical turn reached checkpoint callback");
                bool crossChargeDenied = false, crossResetDenied = false;
                try { concurrent.CommitBatch(1, (_, _) => {}); }
                catch (InvalidOperationException) { crossChargeDenied = true; }
                try { concurrent.BeginDispatch(); }
                catch (InvalidOperationException) { crossResetDenied = true; }
                Require(crossChargeDenied && crossResetDenied,
                    "concurrent carrier cannot mutate an active actor");
                Require(concurrent.Completed == 128 && concurrent.Phase == 0,
                    "concurrent violation did not alter committed reductions");
            }
            finally
            {
                callbackRelease.Set();
                Require(owner.Wait(TimeSpan.FromSeconds(10)),
                    "owning carrier callback can complete");
            }
        }
        concurrent.CommitBatch(1, (_, _) => throw new Exception("unexpected checkpoint"));
        Require(concurrent.Completed == 129 && concurrent.Phase == 1,
            "ownership is released after physical callback returns");

        bool rejectedInterval = false;
        try { _ = new ActorCooperationMeter(64); }
        catch (ArgumentOutOfRangeException) { rejectedInterval = true; }
        Require(rejectedInterval, "unsupported interval rejected");
    }
}
