using Ores.Net.Runtime;

internal static class AmortizedCooperationQuantumTests
{
    public static void Run()
    {
        var q = new AmortizedCooperationQuantum();
        int polls = 0;
        for (int i = 1; i <= 1_000_000; ++i)
        {
            bool due = q.CompletedBackedgeNeedsSlowPoll();
            if (due) polls++;
            if (due != (i % AmortizedCooperationQuantum.SlowPollInterval == 0))
                throw new Exception("expensive .NET cooperation poll on wrong reduction");
            if (q.CompletedThisDispatch != (ulong)i)
                throw new Exception(".NET partial reduction count changed");
            if (q.FairnessBudgetExpired != (i >= 512))
                throw new Exception(".NET fairness cap not monotonic");
        }
        if (polls != 7_812 || q.CompletedThisDispatch != 1_000_000)
            throw new Exception("exact .NET million-edge checkpoint count");
        // Independent actor task quanta never borrow a counter from siblings.
        var sibling = new AmortizedCooperationQuantum();
        for (int i = 1; i <= 128; ++i)
            if (sibling.CompletedBackedgeNeedsSlowPoll() != (i == 128))
                throw new Exception("sibling actor quantum shared reduction counter");
        if (sibling.CompletedThisDispatch != 128 || q.CompletedThisDispatch != 1_000_000)
            throw new Exception("sibling actor quantum cross-contamination");
        q.BeginDispatch();
        if (q.CompletedThisDispatch != 0 || q.FairnessBudgetExpired)
            throw new Exception("fairness reduction state survived reset");
        for (int i = 1; i <= 127; ++i)
        {
            if (q.CompletedBackedgeNeedsSlowPoll() || q.CompletedThisDispatch != (ulong)i)
                throw new Exception("poll occurred before 128 backedges after reset");
        }
        if (!q.CompletedBackedgeNeedsSlowPoll() || q.CompletedThisDispatch != 128)
            throw new Exception("checkpoint must rearm on fresh dispatch");
    }
}
