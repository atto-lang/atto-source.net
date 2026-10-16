using Ores.Net.Runtime;

internal static class ActorPhysicalTurnCreditsSelfTest
{
    public static void Run()
    {
        static void Check(bool ok, string why)
        {
            if (!ok) throw new Exception("actor physical turn credit: " + why);
        }
        var serial = new ActorPhysicalTurnCredits();
        Check(serial.Maximum == 1, "default max=1");
        var a = serial.TryAcquire();
        Check(a is not null && serial.Active == 1, "first credit");
        Check(serial.TryAcquire() is null, "serial actor rejects overlap");
        a.Dispose();
        a.Dispose();
        Check(serial.Active == 0, "idempotent release");

        var parallel = new ActorPhysicalTurnCredits(3);
        var first = parallel.TryAcquire();
        var second = parallel.TryAcquire();
        var third = parallel.TryAcquire();
        Check(first is not null && second is not null && third is not null,
            "three independent physical credits");
        Check(parallel.Active == 3 && parallel.TryAcquire() is null,
            "fourth turn denied");
        second.Dispose();
        second.Dispose();
        Check(parallel.Active == 2, "duplicate release denied");
        var replacement = parallel.TryAcquire();
        Check(replacement is not null && parallel.Active == 3, "recovered credit");
        parallel.Close();
        Check(parallel.IsClosed && parallel.TryAcquire() is null, "retirement fences admission");
        first.Dispose(); third.Dispose(); replacement.Dispose();
        Check(parallel.Active == 0 && parallel.IsClosed, "quiesced closed actor");
        foreach (int invalid in new[] {-1, 0, 4, int.MaxValue})
        {
            bool rejected = false;
            try { _ = new ActorPhysicalTurnCredits(invalid); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "invalid max accepted");
        }
        // Run twelve simultaneous *host* claims. These barriers exist only
        // in the test; the admission primitive never parks an actor carrier.
        for (int replay = 0; replay < 100; replay++)
        {
            var gate = new ActorPhysicalTurnCredits(3);
            using var start = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var ready = new CountdownEvent(12);
            using var attempted = new CountdownEvent(12);
            int maxObserved = 0;
            var workers = new Thread[12];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(() =>
                {
                    ready.Signal();
                    if (!start.Wait(TimeSpan.FromSeconds(10)))
                        throw new Exception("start gate timeout");
                    var permit = gate.TryAcquire();
                    attempted.Signal();
                    if (permit is null) return;
                    using (permit)
                    {
                        int active = gate.Active;
                        int snapshot;
                        do { snapshot = Volatile.Read(ref maxObserved); }
                        while (snapshot < active &&
                            Interlocked.CompareExchange(ref maxObserved, active, snapshot) != snapshot);
                        if (!release.Wait(TimeSpan.FromSeconds(10)))
                            throw new Exception("release gate timeout");
                    }
                }) { IsBackground = true };
                workers[i].Start();
            }
            Check(ready.Wait(TimeSpan.FromSeconds(10)), "workers not ready");
            start.Set();
            try
            {
                Check(attempted.Wait(TimeSpan.FromSeconds(10)), "workers did not claim");
                Check(maxObserved <= 3 && gate.Active <= 3,
                    "more than three concurrent physical credits");
            }
            finally { release.Set(); }
            foreach (var thread in workers) Check(thread.Join(10000), "worker stuck");
            Check(gate.Active == 0, "credit leaked");
        }
        Console.WriteLine("PASS .NET host actor turn credits 1..3, retirement and 100 contention rounds");
    }
}
