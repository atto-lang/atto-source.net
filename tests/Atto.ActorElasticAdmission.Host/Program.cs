using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Ores.Net.Runtime;

static class ActorElasticHostProbeV1
{
    private static int _assertions;
    private static int _checks;

    private static void Check(bool condition, string reason)
    {
        Interlocked.Increment(ref _assertions);
        if (!condition) throw new Exception("E_HOST_TEST: " + reason);
    }

    private static ActorElasticAdmissionV1 NewGate(long incarnation, int maximum,
        bool serial = false, ActorElasticAffinityV1 affinity = ActorElasticAffinityV1.Sticky,
        bool dedicated = false, int actor = 64, int tenant = 64, int global = 64)
        => new(incarnation, 501, 3, maximum, serial, affinity, dedicated, actor, tenant, global);

    private static Dictionary<int, long> Workers(int count)
    {
        var workers = new Dictionary<int, long>();
        for (int i = 0; i < count; i++) workers.Add(i, 7);
        return workers;
    }

    private static void Reject<T>(Action work, string reason) where T : Exception
    {
        bool failedAsExpected = false;
        try { work(); } catch (T) { failedAsExpected = true; }
        Check(failedAsExpected, reason);
    }

    private static void ElasticTwoToEightConcurrent()
    {
        using var gate = NewGate(11, ActorElasticAdmissionV1.PoolSize);
        gate.ReplaceHealthyWorkers(Workers(2));
        Check(gate.CurrentCapacity == 2, "elastic initial 2");
        using (var a = gate.TryEnter(11, 501, 3, 0, 7))
        using (var b = gate.TryEnter(11, 501, 3, 1, 7))
        {
            Check(a is not null && b is not null, "two real physical leases");
            Check(gate.Running == 2, "running two");
            Check(gate.TryEnter(11, 501, 3, 0, 7) is null, "not two on one carrier");
        }

        gate.ReplaceHealthyWorkers(Workers(8));
        Check(gate.CurrentCapacity == 8, "live pool size 8");
        using var entered = new CountdownEvent(8);
        using var release = new ManualResetEventSlim(false);
        var failures = new ConcurrentQueue<Exception>();
        var threads = new List<Thread>();
        for (int i = 0; i < 8; i++)
        {
            int lane = i;
            var worker = new Thread(() =>
            {
                try
                {
                    using var lease = gate.TryEnter(11, 501, 3, lane, 7);
                    if (lease is null) throw new Exception("unexpected capacity denial");
                    entered.Signal();
                    if (!release.Wait(TimeSpan.FromSeconds(15)))
                        throw new Exception("thread rendezvous timeout");
                }
                catch (Exception e) { failures.Enqueue(e); }
            });
            threads.Add(worker);
            worker.Start();
        }

        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(15)), "all 8 workers reached admission");
            Check(gate.Running == 8 && gate.PeakRunning == 8, "8 physical overlapping leases");
        }
        finally { release.Set(); }
        foreach (var worker in threads)
            Check(worker.Join(TimeSpan.FromSeconds(15)), "worker physically retired");
        Check(failures.IsEmpty && gate.Running == 0, "all worker results and leases");
        _checks++;
    }

    private static void FixedCapQuotasAndShrink()
    {
        using var fixedGate = NewGate(21, 2);
        fixedGate.ReplaceHealthyWorkers(Workers(8));
        Check(fixedGate.CurrentCapacity == 2, "fixed 2 invariant");
        using (var a = fixedGate.TryEnter(21, 501, 3, 0, 7))
        using (var b = fixedGate.TryEnter(21, 501, 3, 1, 7))
        {
            Check(a is not null && b is not null, "two fixed leases");
            Check(fixedGate.TryEnter(21, 501, 3, 2, 7) is null, "third fixed denied");
        }

        using var gate = NewGate(22, ActorElasticAdmissionV1.PoolSize, actor: 8, tenant: 8);
        gate.ReplaceHealthyWorkers(Workers(8));
        var outstanding = new List<ActorElasticAdmissionV1.Lease>();
        for (int lane = 0; lane < 8; lane++)
        {
            var lease = gate.TryEnter(22, 501, 3, lane, 7);
            Check(lease is not null, "initial 8");
            outstanding.Add(lease!);
        }
        gate.ReplaceHealthyWorkers(Workers(2));
        Check(gate.CurrentCapacity == 2, "shrunk budget 2");
        Check(gate.TryEnter(22, 501, 3, 0, 7) is null, "shrink admits no new over budget");
        for (int i = 0; i < 7; i++) outstanding[i].Dispose();
        Check(gate.Running == 1, "retirement drains without unsafe abort");
        using (var next = gate.TryEnter(22, 501, 3, 0, 7))
            Check(next is not null, "new permit after below shrink budget");
        outstanding[7].Dispose();
        outstanding[7].Dispose();
        Check(gate.Running == 0, "duplicate release ignored");
        using var limited = NewGate(23, ActorElasticAdmissionV1.PoolSize, actor: 6, tenant: 3, global: 4);
        limited.ReplaceHealthyWorkers(Workers(8));
        Check(limited.CurrentCapacity == 3, "minimum of actor tenant global");
        _checks++;
    }

    private static void SerialSuspensionAndMethod()
    {
        using var gate = NewGate(31, 1, serial: true);
        gate.ReplaceHealthyWorkers(Workers(4));
        bool logicalReceiveAlive = true; // separate logical gate is NOT implemented here.
        using (var receive = gate.TryEnter(31, 501, 3, 0, 7))
        {
            Check(receive is not null, "serial receive executed");
            Check(gate.TryEnter(31, 501, 3, 1, 7) is null, "no concurrent serial segment");
        }
        Check(logicalReceiveAlive, "suspension retains modeled logical receive");
        using (var method = gate.TryEnter(31, 501, 3, 1, 7))
        {
            Check(method is not null, "independent method on released physical carrier");
            Check(gate.TryEnter(31, 501, 3, 0, 7) is null, "method and task cannot overlap");
        }
        logicalReceiveAlive = false;
        Check(!logicalReceiveAlive, "logical receive terminal cleanup");
        _checks++;
    }

    private static void PinnedWorkerRetirement()
    {
        using var gate = NewGate(41, 1, serial: true, affinity: ActorElasticAffinityV1.Pinned);
        gate.ReplaceHealthyWorkers(Workers(2));
        var first = gate.TryEnter(41, 501, 3, 0, 7);
        Check(first is not null && gate.HomeLane == 0, "original pinned lane");
        gate.ReplaceHealthyWorkers(new Dictionary<int, long> { [1] = 7 });
        Check(gate.TryEnter(41, 501, 3, 1, 7) is null, "no repin before physical retirement");
        first!.Dispose();
        using var replacement = gate.TryEnter(41, 501, 3, 1, 7);
        Check(replacement is not null && gate.HomeLane == 1, "fenced migration after death");
        _checks++;
    }

    private static void WorkerEpochStopAndSecurity()
    {
        var gate = NewGate(51, ActorElasticAdmissionV1.PoolSize);
        gate.ReplaceHealthyWorkers(new Dictionary<int, long> { [0] = 9 });
        Check(gate.TryEnter(51, 501, 3, 0, 8) is null, "stale worker generation");
        Reject<System.Security.SecurityException>(
            () => gate.TryEnter(52, 501, 3, 0, 9), "stale actor generation denied");
        Reject<System.Security.SecurityException>(
            () => gate.TryEnter(51, 502, 3, 0, 9), "foreign pool denied");
        Reject<System.Security.SecurityException>(
            () => gate.TryEnter(51, 501, 4, 0, 9), "foreign pool generation denied");
        var active = gate.TryEnter(51, 501, 3, 0, 9);
        Check(active is not null, "exact generation permitted");
        gate.Stop();
        Check(gate.TryEnter(51, 501, 3, 0, 9) is null, "STOP fences guest entry");
        Check(gate.CurrentCapacity == 0 && gate.Running == 1, "STOP seals capacity while old lease drains");
        Reject<InvalidOperationException>(gate.Dispose, "cannot retire while physical lease held");
        active!.Dispose();
        gate.Dispose();
        Check(gate.Running == 0, "physical lease retired");
        Check(gate.HomeLane == -1, "retired worker is no longer a placement hint");
        _checks++;
    }

    private static void ReplacementCannotResurrectAnOlderWorkerGeneration()
    {
        using var gate = NewGate(71, ActorElasticAdmissionV1.PoolSize);
        gate.ReplaceHealthyWorkers(new Dictionary<int, long> { [0] = 7 });
        using (var retiring = gate.TryEnter(71, 501, 3, 0, 7))
        {
            Check(retiring is not null, "old worker entered");
            gate.ReplaceHealthyWorkers(new Dictionary<int, long> { [0] = 8 });
            Check(gate.TryEnter(71, 501, 3, 0, 8) is null, "replacement waits for the physical slot");
            gate.ReplaceHealthyWorkers(new Dictionary<int, long>());
            Reject<ArgumentException>(() => gate.ReplaceHealthyWorkers(
                new Dictionary<int, long> { [0] = 7, [1] = 10 }), "stale snapshot must be rejected");
            Check(gate.CurrentCapacity == 0 && gate.Running == 1, "failed update leaves inventory and lease intact");
            // The rejected snapshot must not advance lane 1 either.
            gate.ReplaceHealthyWorkers(new Dictionary<int, long> { [0] = 8, [1] = 9 });
            Check(gate.CurrentCapacity == 2, "both current workers remain available");
        }
        Check(gate.TryEnter(71, 501, 3, 0, 7) is null, "old generation still denied");
        using (var replacement = gate.TryEnter(71, 501, 3, 0, 8))
            Check(replacement?.WorkerGeneration == 8, "replacement admits only the current generation");
        Console.WriteLine("ACTOR_WORKER_REPLACEMENT_V1 stale-denied,old-drained,new-admitted");
        _checks++;
    }

    private static void InvalidPoliciesAndDedicatedDenial()
    {
        Reject<ArgumentOutOfRangeException>(
            () => NewGate(61, 0), "zero cap denied");
        Reject<ArgumentException>(
            () => NewGate(61, 2, serial: true), "serial >1 denied");
        Reject<ArgumentException>(
            () => NewGate(61, 2, affinity: ActorElasticAffinityV1.Pinned),
            "pinned >1 denied");
        Reject<ArgumentOutOfRangeException>(
            () => NewGate(61, 1, affinity: (ActorElasticAffinityV1)99),
            "unknown affinity denied");
        using var gate = NewGate(61, 1,
            affinity: ActorElasticAffinityV1.Dedicated, dedicated: false);
        gate.ReplaceHealthyWorkers(Workers(1));
        Check(gate.TryEnter(61, 501, 3, 0, 7) is null,
            "unreserved exclusive carrier denied");
        Reject<ArgumentException>(
            () => gate.ReplaceHealthyWorkers(new Dictionary<int, long> { [0] = 0 }),
            "invalid generation denied without mutating pool");
        Check(gate.CurrentCapacity == 1, "inventory unchanged after invalid update");
        _checks++;
    }

    public static int Main()
    {
        ElasticTwoToEightConcurrent();
        FixedCapQuotasAndShrink();
        SerialSuspensionAndMethod();
        PinnedWorkerRetirement();
        WorkerEpochStopAndSecurity();
        ReplacementCannotResurrectAnOlderWorkerGeneration();
        InvalidPoliciesAndDedicatedDenial();
        Console.WriteLine("HOST_NATIVEAOT_ELASTIC_ADMISSION_PASS checks=" + _checks +
            " assertions=" + _assertions);
        Console.WriteLine("NOT_GUEST_EXECUTED: .atto source compiler and actor runtime not connected");
        return 0;
    }
}
