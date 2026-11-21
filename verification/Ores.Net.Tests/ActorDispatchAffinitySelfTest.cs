using Ores.Net.Runtime;

/// <summary>
/// Lane affinity and idle stealing in ActorDispatchDomains
/// (CONTINUATION_LOCALITY_V1 rule 5), mirroring Java #3161.
/// </summary>
internal static class ActorDispatchAffinitySelfTest
{
    public static void Run()
    {
        PreferredIdleLaneIsReused();
        TurnBehindBlockedLaneIsStolenByIdleLane();
        StealingDisabledLeavesTurnHome();
        ProbeWhoseLaneGetsWorkLeavesTurnHome();
        foreach (int seed in new[] { 1, 8, 64, 2048, 99991 })
            EveryTurnRunsExactlyOnce(seed);
        DisposeDrainsEveryAcceptedTurnOnceWithProbesPending();
        Console.WriteLine(
            "PASS .NET actor lane affinity, idle stealing and exactly-once turns");
    }

    private static ActorDispatchDomains Domains(int workers, int graceMicros, int capacity = 4096) =>
        new(new ActorDispatcherOptions
        {
            PrivateWorkers = workers,
            SharedWorkers = 1,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = capacity,
            StealGraceMicroseconds = graceMicros
        });

    /// <summary>Runs one turn and reports the lane and thread it ran on.</summary>
    private static (int Lane, Thread Thread) RunOne(ActorDispatchDomains domains, int preferred)
    {
        int lane = -2;
        Thread? thread = null;
        using var done = new ManualResetEventSlim();
        Require(domains.TryDispatch(ActorKind.Private, () =>
        {
            lane = ActorDispatchDomains.CurrentLane;
            thread = Thread.CurrentThread;
            done.Set();
        }, preferred, out _), "dispatch admitted");
        Require(done.Wait(TimeSpan.FromSeconds(5)), "turn ran");
        return (lane, thread!);
    }

    /// <summary>Holds lane 0 and starts lane 1, which goes idle again.</summary>
    private static ManualResetEventSlim HoldLaneZeroWithIdleLaneOne(ActorDispatchDomains domains)
    {
        var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        Require(domains.TryDispatch(ActorKind.Private, () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }, -1, out int blockerLane), "blocker admitted");
        Require(blockerLane == 0 && entered.Wait(TimeSpan.FromSeconds(5)), "blocker holds lane 0");
        var (lane, _) = RunOne(domains, -1); // every lane busy: lane 1 starts
        Require(lane == 1, "a second lane starts when every started lane is busy");
        Require(SpinWait.SpinUntil(() => domains.QueuedTurns == 0, TimeSpan.FromSeconds(5)),
            "lane 1 drained");
        return release;
    }

    private static void PreferredIdleLaneIsReused()
    {
        using var domains = Domains(4, 50);
        var first = RunOne(domains, -1);
        for (int i = 0; i < 200; ++i)
        {
            var next = RunOne(domains, first.Lane);
            Require(next.Lane == first.Lane && next.Thread == first.Thread,
                "an idle preferred lane keeps the actor's turns");
        }
        Require(domains.Steals == 0, "no steals when the home lane is idle");
    }

    private static void TurnBehindBlockedLaneIsStolenByIdleLane()
    {
        using var domains = Domains(2, 200);
        using ManualResetEventSlim release = HoldLaneZeroWithIdleLaneOne(domains);
        try
        {
            int runs = 0;
            int ranOn = -1;
            using var ran = new ManualResetEventSlim();
            Require(domains.TryDispatch(ActorKind.Private, () =>
            {
                Interlocked.Increment(ref runs);
                ranOn = ActorDispatchDomains.CurrentLane;
                ran.Set();
            }, 0, out int placed), "turn admitted");
            Require(placed == 0, "the turn is queued on its busy home lane");
            Require(ran.Wait(TimeSpan.FromSeconds(5)), "turn stranded behind a blocked lane");
            Require(ranOn == 1, "the idle lane takes it after the grace");
            Require(domains.Steals == 1, "one steal counted");
            release.Set();
            Require(SpinWait.SpinUntil(() => domains.QueuedTurns == 0, TimeSpan.FromSeconds(5)),
                "queued accounting returns to zero");
            Thread.Sleep(50);
            Require(Volatile.Read(ref runs) == 1, "the home lane must not run it again");
        }
        finally
        {
            release.Set();
        }
    }

    private static void StealingDisabledLeavesTurnHome()
    {
        using var domains = Domains(2, -1);
        using ManualResetEventSlim release = HoldLaneZeroWithIdleLaneOne(domains);
        try
        {
            int ranOn = -1;
            using var ran = new ManualResetEventSlim();
            Require(domains.TryDispatch(ActorKind.Private, () =>
            {
                ranOn = ActorDispatchDomains.CurrentLane;
                ran.Set();
            }, 0, out _), "turn admitted");
            Require(!ran.Wait(TimeSpan.FromMilliseconds(100)), "ran before its home lane was free");
            release.Set();
            Require(ran.Wait(TimeSpan.FromSeconds(5)) && ranOn == 0, "ran on its home lane");
            Require(domains.Steals == 0, "no steals when disabled");
        }
        finally
        {
            release.Set();
        }
    }

    private static void ProbeWhoseLaneGetsWorkLeavesTurnHome()
    {
        // A long grace makes the race deterministic: lane 1 receives its own
        // work while its probe is still waiting.
        using var domains = Domains(2, 300_000 / 100); // 3 ms
        using ManualResetEventSlim release = HoldLaneZeroWithIdleLaneOne(domains);
        try
        {
            int ranOn = -1;
            using var ran = new ManualResetEventSlim();
            using var ownRan = new ManualResetEventSlim();
            using var holdOwn = new ManualResetEventSlim();
            Require(domains.TryDispatch(ActorKind.Private, () =>
            {
                ranOn = ActorDispatchDomains.CurrentLane;
                ran.Set();
            }, 0, out _), "turn admitted");
            // Lane 1's own turn arrives while the probe spins; it blocks lane 1
            // until the grace has long passed.
            Require(domains.TryDispatch(ActorKind.Private, () =>
            {
                ownRan.Set();
                holdOwn.Wait(TimeSpan.FromSeconds(10));
            }, 1, out int ownLane), "lane 1 work admitted");
            Require(ownLane == 1 && ownRan.Wait(TimeSpan.FromSeconds(5)), "lane 1 runs its own work");
            Thread.Sleep(50);
            Require(!ran.IsSet, "a lane that was given work must not steal");
            holdOwn.Set();
            release.Set();
            Require(ran.Wait(TimeSpan.FromSeconds(5)), "turn eventually runs");
            Require(domains.Steals == 0, "no steal");
        }
        finally
        {
            release.Set();
        }
    }

    private static void EveryTurnRunsExactlyOnce(int seed)
    {
        var random = new Random(seed);
        const int turns = 3000;
        using var domains = Domains(4, 30, capacity: 1024);
        var runs = new int[turns];
        for (int i = 0; i < turns; ++i)
        {
            int id = i;
            long spin = random.Next(10) == 0 ? random.Next(500_000) : random.Next(2_000);
            int preferred = random.Next(6) - 1; // -1 .. 4, including an out-of-range lane
            void Turn()
            {
                Interlocked.Increment(ref runs[id]);
                long until = System.Diagnostics.Stopwatch.GetTimestamp()
                    + spin * System.Diagnostics.Stopwatch.Frequency / 1_000_000_000;
                while (System.Diagnostics.Stopwatch.GetTimestamp() < until) Thread.SpinWait(8);
            }
            while (!domains.TryDispatch(ActorKind.Private, Turn, preferred, out _))
                Thread.Sleep(1); // bounded queue: back off
        }
        Require(SpinWait.SpinUntil(
            () => domains.QueuedTurns == 0 && domains.CompletedWorkItems == turns,
            TimeSpan.FromSeconds(60)), "seed " + seed + ": all turns completed");
        Thread.Sleep(20);
        for (int i = 0; i < turns; ++i)
            Require(Volatile.Read(ref runs[i]) == 1,
                "seed " + seed + ": turn " + i + " ran " + runs[i] + " times");
        Require(domains.CompletedWorkItems == turns, "seed " + seed + ": completion count");
    }

    private static void DisposeDrainsEveryAcceptedTurnOnceWithProbesPending()
    {
        var domains = Domains(3, 5_000);
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        Require(domains.TryDispatch(ActorKind.Private, () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }, -1, out _), "blocker admitted");
        Require(entered.Wait(TimeSpan.FromSeconds(5)), "blocker entered");
        RunOne(domains, -1); // start lane 1 so probes have an idle target
        int ran = 0;
        for (int i = 0; i < 6; ++i)
            Require(domains.TryDispatch(ActorKind.Private,
                () => Interlocked.Increment(ref ran), 0, out _), "queued behind lane 0");
        Task dispose = Task.Factory.StartNew(domains.Dispose, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Thread.Sleep(20);
        release.Set();
        Require(dispose.Wait(TimeSpan.FromSeconds(10)), "Dispose joins every lane");
        Require(Volatile.Read(ref ran) == 6, "every accepted turn ran exactly once: " + ran);
        Require(!domains.TryDispatch(ActorKind.Private, static () => { }), "closed after Dispose");
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor dispatch affinity regression: " + scenario);
    }
}
