using Ores.Net.Runtime.Net;

internal static class TcpPoolLeaseLedgerSelfTest
{
    private static readonly TcpPoolLeaseLedger.ActorIdentity ActorA = new(101, 1);
    private static readonly TcpPoolLeaseLedger.ActorIdentity ActorB = new(102, 1);
    private const string Partition = "tenant-a|tls|example.test:443";

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new Exception("TCP lease regression: " + name);
    }

    private static TcpPoolLeaseLedger New(int slots = 1) =>
        new(slots, Partition, 7);

    private static TcpPoolLeaseLedger.Lease Acquire(
        TcpPoolLeaseLedger pool, int slot = 0,
        TcpPoolLeaseLedger.ActorIdentity? actor = null, ulong epoch = 7)
    {
        Require(pool.TryAcquire(slot, actor ?? ActorA, Partition, epoch, out var lease),
            "expected exclusive lease");
        return lease!;
    }

    internal static void Run()
    {
        {
            var pool = New();
            var first = Acquire(pool);
            Require(!pool.TryAcquire(0, ActorB, Partition, 7, out _),
                "second actor cannot alias raw TCP socket");
            Require(!pool.TryAcquire(0, ActorA, "tenant-b|tls|example.test:443", 7, out _),
                "partition mismatch");
            Require(!pool.TryBegin(first, ActorB, 7), "other actor cannot begin");
            Require(!pool.TryBegin(first, new(101, 2), 7),
                "actor restart incarnation changes identity");
            Require(!pool.TryRelease(first, ActorB, 7),
                "other actor cannot revoke owner");
            Require(pool.TryBegin(first, ActorA, 7), "owner starts exchange");
            Require(!pool.TryBegin(first, ActorA, 7),
                "same lease cannot start overlapping I/O");
            Require(pool.TryRelease(first, ActorA, 7),
                "release revokes guest while reactor operates");
            Require(!pool.TryAcquire(0, ActorB, Partition, 7, out _),
                "socket not reusable before trusted completion");
            Require(!pool.TryBegin(first, ActorA, 7), "released lease stays revoked");
            Require(pool.TryComplete(first), "reactor drains operation");
            Require(!pool.TryComplete(first), "duplicate completion rejected");
            Require(!pool.TryAcquire(0, ActorB, Partition, 7, out _),
                "quarantined socket cannot be reissued before physical close");
            Require(!pool.TryConfirmPhysicalClose(0, first.Generation),
                "stale generation cannot clear quarantine");
            Require(pool.TryConfirmPhysicalClose(0, first.Generation + 1),
                "trusted physical close fence clears retired slot");
            var next = Acquire(pool, actor: ActorB);
            Require(next.Generation > first.Generation,
                "generation advances on physical retirement");
            Require(!pool.TryRelease(first, ActorA, 7),
                "stale lease cannot revoke new connection owner");
            Require(!pool.TryConfirmPhysicalClose(0, first.Generation + 1),
                "close ack cannot rearm a currently leased slot");
            Require(pool.TryRelease(next, ActorB, 7),
                "new actor can retire idle lease");
        }
        {
            var pool = New(2);
            var a = Acquire(pool, 0);
            var b = Acquire(pool, 1, ActorB);
            Require(!pool.TryAcquire(-1, ActorA, Partition, 7, out _),
                "negative slot index denied");
            Require(!pool.TryAcquire(2, ActorA, Partition, 7, out _),
                "out-of-range slot denied");
            Require(!pool.TryAcquire(0, new(0, 1), Partition, 7, out _),
                "invalid actor ID denied");
            Require(!pool.TryAcquire(0, new(1, 0), Partition, 7, out _),
                "invalid actor incarnation denied");
            Require(!pool.TryAcquire(0, ActorA, Partition, 6, out _),
                "wrong policy epoch denied");
            Require(pool.TryBegin(a, ActorA, 7), "slot one starts");
            Require(pool.TryBegin(b, ActorB, 7), "independent slot starts");
            Require(pool.TryComplete(a), "slot one completes");
            Require(pool.TryRelease(a, ActorA, 7), "slot one retires");
            Require(!pool.TryAcquire(1, ActorA, Partition, 7, out _),
                "slot two remains exclusive");
            Require(pool.TryComplete(b), "slot two completes");
            Require(pool.TryRelease(b, ActorB, 7), "slot two retires");
        }
        {
            var pool = New();
            var active = Acquire(pool);
            Require(pool.TryBegin(active, ActorA, 7), "begin before epoch update");
            Require(!pool.TryRevokeEpoch(7), "epoch rollback/same forbidden");
            Require(!pool.TryRevokeEpoch(6), "epoch rollback forbidden");
            Require(pool.TryRevokeEpoch(8), "epoch advancement");
            Require(!pool.TryAcquire(0, ActorB, Partition, 8, out _),
                "revoked physical operation still in-flight");
            Require(!pool.TryBegin(active, ActorA, 7),
                "stale epoch must not begin");
            Require(pool.TryComplete(active), "trusted completion after revocation");
            Require(!pool.TryAcquire(0, ActorB, Partition, 8, out _),
                "policy epoch change also requires physical close");
            Require(pool.TryConfirmPhysicalClose(0, active.Generation + 1),
                "trusted old-socket retirement");
            var fresh = Acquire(pool, actor: ActorB, epoch: 8);
            Require(!pool.TryComplete(active), "stale completion not valid");
            Require(pool.TryRelease(fresh, ActorB, 8), "new epoch release");
        }
        {
            var pool = New();
            var active = Acquire(pool);
            Require(pool.TryBegin(active, ActorA, 7), "in-flight before stop");
            Require(pool.TryStop(), "terminal shutdown");
            Require(!pool.TryAcquire(0, ActorB, Partition, 7, out _),
                "shutdown refuses new connections");
            Require(!pool.TryBegin(active, ActorA, 7),
                "shutdown revokes guest authority");
            Require(pool.TryComplete(active),
                "shutdown allows trusted completion to free lease");
            Require(!pool.TryAcquire(0, ActorB, Partition, 7, out _),
                "stopped table cannot resurrect physical slot");
            Require(pool.TryStop(), "repeated stop is idempotent");
        }
        {
            var pool = New();
            Require(pool.TryStop(), "shutdown before any lease");
            Require(!pool.TryAcquire(0, ActorA, Partition, 7, out _),
                "stop-before-connect permanent");
        }
        {
            var first = New();
            var second = New();
            var a = Acquire(first);
            var b = Acquire(second);
            Require(!first.TryBegin(b, ActorA, 7), "cross-pool forged token rejected");
            Require(!second.TryRelease(a, ActorA, 7), "cross-pool revoke rejected");
            Require(first.TryRelease(a, ActorA, 7), "first owner retires");
            Require(second.TryRelease(b, ActorA, 7), "second owner retires");
        }
        {
            var pool = New();
            var wins = 0;
            var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            {
                start.Wait();
                if (pool.TryAcquire(0, new((ulong)(i + 1), 1),
                    Partition, 7, out _)) Interlocked.Increment(ref wins);
            })).ToArray();
            start.Set();
            Task.WaitAll(tasks);
            Require(wins == 1, "32-way contention must admit one raw TCP owner");
            Require(pool.TryStop(), "stop after contention");
        }
        {
            // Make the nonparking admission property measurable using a
            // different thread that owns the private table gate. The test
            // synchronizes on a source-visible private field by reflection;
            // production actor code never observes this object.
            var pool = New();
            var field = typeof(TcpPoolLeaseLedger).GetField("gate",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Require(field is not null, "trusted monitor gate exists");
            object gate = field!.GetValue(pool)!;
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var holder = Task.Run(() =>
            {
                lock (gate)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                }
            });
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(2)), "holder acquired gate");
                Require(!pool.TryAcquire(0, ActorA, Partition, 7, out _),
                    "busy transport table fails without parking");
            }
            finally
            {
                release.Set();
                Require(holder.Wait(TimeSpan.FromSeconds(2)), "holder terminates");
            }
        }
        {
            var pool = New();
            Require(pool.TryRevokeEpoch(8), "idle epoch revoke");
            Require(!pool.TryAcquire(0, ActorB, Partition, 8, out _),
                "idle preconnected socket quarantined by epoch rollover");
            Require(!pool.TryConfirmPhysicalClose(0, 0),
                "stale generation cannot rearm");
            Require(pool.TryConfirmPhysicalClose(0, 1),
                "trusted host physical close acknowledgement");
            var ready = Acquire(pool, actor: ActorB, epoch: 8);
            Require(pool.TryRelease(ready, ActorB, 8), "new epoch lease releases");
        }
        {
            var pool = New();
            TcpPoolLeaseLedger.Lease? last = null;
            for (var iteration = 0; iteration < 1024; iteration++)
            {
                var current = Acquire(pool);
                if (last is not null)
                    Require(!pool.TryBegin(last, ActorA, 7),
                        "prior generation never regains authority");
                if ((iteration & 1) == 0)
                {
                    Require(pool.TryBegin(current, ActorA, 7), "recycle operation starts");
                    Require(pool.TryRelease(current, ActorA, 7),
                        "in-flight release revokes authority");
                    Require(!pool.TryConfirmPhysicalClose(0, current.Generation + 1),
                        "active reactor prevents early close acknowledgement");
                    Require(pool.TryComplete(current),
                        "physical completion after cancellation");
                }
                else
                {
                    Require(pool.TryRelease(current, ActorA, 7),
                        "idle physical exchange releases");
                }
                Require(!pool.TryAcquire(0, ActorA, Partition, 7, out _),
                    "quarantined slot cannot be reissued");
                Require(pool.TryConfirmPhysicalClose(0, current.Generation + 1),
                    "trusted close ack after quiescence");
                last = current;
            }
            Require(pool.TryStop(), "table stops after repeated retirements");
        }
        foreach (var wrong in new[] { 0, -1, 1025 })
        {
            try
            {
                _ = New(wrong);
                throw new Exception("invalid ledger capacity accepted");
            }
            catch (ArgumentOutOfRangeException) { }
        }
    }
}
