using Ores.Net.Runtime;
using System.Threading;

internal static class ActorShardLayoutAdvisorV1SelfTest
{
    private static int checks;

    private static void Require(bool yes, string reason)
    {
        ++checks;
        if (!yes) throw new Exception("NativeAOT layout advisor: " + reason);
    }

    private static void Deny<T>(Action action, string reason) where T : Exception
    {
        ++checks;
        try { action(); }
        catch (T) { return; }
        throw new Exception("Unfenced layout advisor admission: " + reason);
    }

    private static ActorArena NewArena(ulong actor) => new(actor, incarnation: 1,
        persistentBytes: 1024 * 1024, scratchBytes: 4096,
        maxAllocations: 8, maxLifetimePins: 4, maxReadLoans: 4);

    internal static void Run()
    {
        const ulong actor = 7070;
        using var coldArena = NewArena(actor);
        using var coldTurn = coldArena.EnterTurn(actor, 1);
        var cold = new ActorShardLayoutAdvisorV1(coldArena, coldTurn, 3, sampleLimit: 64);
        Require(cold.Recommend(coldTurn, 32).Layout == ActorDataLayout.Aos,
            "cold profile starts with conservative AoS");
        Require(cold.Observe(coldTurn, ActorShardAccessKindV1.RowScan, 63) &&
            cold.Recommend(coldTurn, 32).Layout == ActorDataLayout.Aos,
            "insufficient profile never speculates tiled layout");
        Require(cold.Observe(coldTurn, ActorShardAccessKindV1.RowScan, 2) &&
            cold.Samples == 64, "sampling saturates rather than overflowing");
        Require(!cold.Observe(coldTurn, ActorShardAccessKindV1.RowScan) &&
            cold.Samples == 64, "full profile rejects further samples");
        var tiled = cold.Recommend(coldTurn, 32, preferredLanes: 8);
        Require(tiled.Layout == ActorDataLayout.Aosoa && tiled.Lanes == 8 &&
            tiled.Samples == 64, "row-local traces select precompiled AoSoA");
        Require(cold.Recommend(coldTurn, 7).Layout == ActorDataLayout.Aos,
            "short shard cannot pretend to be a tiled hot kernel");
        Require(cold.Recommend(coldTurn, 32, preferredLanes: 1).Layout ==
            ActorDataLayout.Aos, "lane-one has no false tiling optimization");

        using var columnsArena = NewArena(actor + 1);
        using var columnsTurn = columnsArena.EnterTurn(actor + 1, 1);
        var columns = new ActorShardLayoutAdvisorV1(columnsArena, columnsTurn, 3);
        Require(columns.Observe(columnsTurn, ActorShardAccessKindV1.ColumnScan, 128),
            "column samples admitted");
        Require(columns.Recommend(columnsTurn, 40).Layout == ActorDataLayout.Soa,
            "column-local tracing selects field-major SoA");

        using var randomArena = NewArena(actor + 2);
        using var randomTurn = randomArena.EnterTurn(actor + 2, 1);
        var random = new ActorShardLayoutAdvisorV1(randomArena, randomTurn, 3);
        random.Observe(randomTurn, ActorShardAccessKindV1.RowScan, 100);
        random.Observe(randomTurn, ActorShardAccessKindV1.RandomLookup, 200);
        Require(random.Recommend(randomTurn, 40).Layout == ActorDataLayout.Aos,
            "random access disqualifies unsafe row-major specialization");

        using var quotaArena = NewArena(actor + 3);
        using var quotaTurn = quotaArena.EnterTurn(actor + 3, 1);
        var quota = new ActorShardLayoutAdvisorV1(quotaArena, quotaTurn,
            fields: 3, sampleLimit: 4096);
        // Warm the verifier and recorder before asserting that the steady
        // already-admitted turn probe path has no per-observation allocation.
        for (int i = 0; i < 128; ++i)
            quota.Observe(quotaTurn, ActorShardAccessKindV1.RowScan);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2048; ++i)
            quota.Observe(quotaTurn, ActorShardAccessKindV1.RowScan);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated == 0, "live-turn hot sampling allocates no managed bytes");

        // Numeric actor IDs and incarnations are not sufficient authority.
        Deny<InvalidOperationException>(() => columns.Observe(randomTurn,
            ActorShardAccessKindV1.ColumnScan),
            "foreign actor cannot observe another actor's profiler");
        Deny<InvalidOperationException>(() => columns.Recommend(coldTurn, 40),
            "foreign actor cannot read a layout policy");
        Deny<InvalidOperationException>(() =>
            new ActorShardLayoutAdvisorV1(columnsArena, coldTurn, 3),
            "advisor construction requires an arena-issued active turn");

        // Even if a second arena has the SAME numeric owner/incarnation,
        // its opaque turn authority is distinct and must not be accepted.
        using (var cloneArena = NewArena(actor + 1))
        using (var cloneTurn = cloneArena.EnterTurn(actor + 1, 1))
        {
            Deny<InvalidOperationException>(() => columns.Observe(cloneTurn,
                ActorShardAccessKindV1.ColumnScan),
                "equal numeric owner cannot forge a foreign arena turn");
            Deny<InvalidOperationException>(() => columns.Retire(cloneTurn),
                "foreign exact owner cannot revoke a live profile");
        }

        bool carrierDenied = false;
        var carrier = new Thread(() =>
        {
            try { columns.Observe(columnsTurn, ActorShardAccessKindV1.ColumnScan); }
            catch (InvalidOperationException) { carrierDenied = true; }
        });
        carrier.Start();
        carrier.Join();
        Require(carrierDenied && columns.Samples == 128,
            "off-carrier access cannot mutate an active actor's profile");

        Deny<ArgumentOutOfRangeException>(() => columns.Observe(columnsTurn,
            (ActorShardAccessKindV1)999), "undefined access kind");
        Deny<ArgumentOutOfRangeException>(() => columns.Observe(columnsTurn,
            ActorShardAccessKindV1.RowScan, count: -1), "negative sample count");
        Deny<ArgumentOutOfRangeException>(() => columns.Recommend(columnsTurn,
            40, preferredLanes: 3), "non-power-of-two lane width");
        Deny<ArgumentOutOfRangeException>(() => columns.Recommend(columnsTurn,
            int.MaxValue), "overbudget allocation rejected before native memory");
        Deny<ArgumentNullException>(() =>
            new ActorShardLayoutAdvisorV1(null!, columnsTurn, 3),
            "advisor must have a real actor arena");
        Deny<ArgumentOutOfRangeException>(() =>
            new ActorShardLayoutAdvisorV1(columnsArena, columnsTurn, 0),
            "invalid fields");
        Deny<ArgumentOutOfRangeException>(() =>
            new ActorShardLayoutAdvisorV1(columnsArena, columnsTurn, 3, sampleLimit: 1),
            "bounded sample policy required");

        // Old turn objects cannot be replayed after suspend/reentry. New turns
        // for the SAME arena are accepted without granting ownership globally.
        using (var epochArena = NewArena(actor + 11))
        {
            var former = epochArena.EnterTurn(actor + 11, 1);
            var epochAdvisor = new ActorShardLayoutAdvisorV1(epochArena, former, 3);
            epochAdvisor.Observe(former, ActorShardAccessKindV1.RowScan, 64);
            former.Dispose();
            using (var current = epochArena.EnterTurn(actor + 11, 1))
            {
                Deny<InvalidOperationException>(() => epochAdvisor.Observe(former,
                    ActorShardAccessKindV1.RowScan), "disposed turn is never reusable");
                Deny<InvalidOperationException>(() => epochAdvisor.Recommend(former, 40),
                    "stale turn cannot select new physical layout");
                Require(epochAdvisor.Recommend(current, 40).Layout ==
                    ActorDataLayout.Aosoa, "live successor turn retains owned profile");
            }
            Deny<InvalidOperationException>(() => epochAdvisor.Observe(former,
                ActorShardAccessKindV1.RowScan), "old turn stays invalid after successor closes");
        }

        // Choices are real inputs to one actor-owned physical layout, not a
        // simulated detached array or a covert runtime JIT rewrite.
        ExerciseNativeShard(actor + 4, ActorShardAccessKindV1.RowScan,
            ActorDataLayout.Aosoa, 8);
        ExerciseNativeShard(actor + 5, ActorShardAccessKindV1.ColumnScan,
            ActorDataLayout.Soa, 1);
        ExerciseNativeShard(actor + 6, ActorShardAccessKindV1.RandomLookup,
            ActorDataLayout.Aos, 1);

        columns.Retire(columnsTurn);
        Require(columns.Samples == 0, "retirement scrubs profile counters");
        Deny<InvalidOperationException>(() => columns.Observe(columnsTurn,
            ActorShardAccessKindV1.ColumnScan),
            "retired advisor cannot record even during old physical turn");
        Deny<InvalidOperationException>(() => columns.Recommend(columnsTurn, 40),
            "retired advisor cannot select physical layout");
        Deny<InvalidOperationException>(() => columns.Retire(columnsTurn),
            "retirement cannot be replayed");

        Console.WriteLine($"PASS {checks} NativeAOT actor-turn-authenticated layout advisory checks (host only)");
    }

    private static void ExerciseNativeShard(ulong actor,
        ActorShardAccessKindV1 access, ActorDataLayout expected, int expectedLanes)
    {
        using var arena = NewArena(actor);
        using var turn = arena.EnterTurn(actor, 1);
        var advisor = new ActorShardLayoutAdvisorV1(arena, turn, 3);
        advisor.Observe(turn, access, 192);
        var selected = advisor.Recommend(turn, rows: 40);
        Require(selected.Layout == expected && selected.Lanes == expectedLanes,
            "native-backed choice matches bounded trace");
        var shard = new ActorShardColumns(arena, turn, 40, 3,
            selected.Layout, selected.Lanes);
        for (int row = 0; row < 40; ++row)
        {
            shard.Write(turn, row, 0, row);
            shard.Write(turn, row, 1, 2.0);
            shard.Write(turn, row, 2, 42.0);
        }
        shard.Integrate(turn, 0, 1, dt: 0.5);
        for (int row = 0; row < 40; ++row)
            Require(shard.Read(turn, row, 0) == row + 1.0 &&
                shard.Read(turn, row, 2) == 42.0,
                "all layouts execute identical native data kernel");
        shard.Release(turn);
        Require(arena.PersistentLiveBytes == 0 && arena.LiveAllocations == 0,
            "selected shard returns native quota on explicit release");
        advisor.Retire(turn);
    }
}
