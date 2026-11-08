using Ores.Net.Runtime;

internal static class AmortizedCooperationQuantumTests
{
    public static void Run()
    {
        var q = new AmortizedCooperationQuantum();
        int polls = 0;
        for (int i = 1; i <= 1024; ++i)
        {
            bool due = q.CompletedBackedgeNeedsSlowPoll();
            if (due) polls++;
            if (due != (i % 128 == 0))
                throw new Exception("expensive .NET cooperation poll on wrong reduction");
            if (q.FairnessBudgetExpired != (i >= 512))
                throw new Exception(".NET fairness cap not monotonic");
        }
        if (polls != 8 || q.CompletedThisDispatch != 1024)
            throw new Exception("exact .NET 128/512 checkpoint count");
        q.BeginDispatch();
        for (int i = 1; i <= 127; ++i)
            if (q.CompletedBackedgeNeedsSlowPoll())
                throw new Exception("poll occurred before 128 backedges after reset");
        if (!q.CompletedBackedgeNeedsSlowPoll() || q.CompletedThisDispatch != 128)
            throw new Exception("checkpoint must rearm on fresh dispatch");
    }
}
