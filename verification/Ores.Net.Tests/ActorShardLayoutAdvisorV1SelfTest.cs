using Ores.Net.Runtime;

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

    internal static void Run()
    {
        const ulong actor = 7070;
        var cold = new ActorShardLayoutAdvisorV1(actor, 1, 3, sampleLimit: 64);
        Require(cold.Recommend(actor, 1, 32).Layout == ActorDataLayout.Aos,
            "cold profile starts with conservative AoS");
        Require(cold.Observe(actor, 1, ActorShardAccessKindV1.RowScan, 63) &&
            cold.Recommend(actor, 1, 32).Layout == ActorDataLayout.Aos,
            "insufficient profile never speculates tiled layout");
        Require(cold.Observe(actor, 1, ActorShardAccessKindV1.RowScan, 2) &&
            cold.Samples == 64, "sampling saturates rather than overflowing");
        Require(!cold.Observe(actor, 1, ActorShardAccessKindV1.RowScan) &&
            cold.Samples == 64, "full profile rejects further samples");
        var tiled = cold.Recommend(actor, 1, 32, preferredLanes: 8);
        Require(tiled.Layout == ActorDataLayout.Aosoa && tiled.Lanes == 8 &&
            tiled.Samples == 64, "row-local traces select precompiled AoSoA");
        Require(cold.Recommend(actor, 1, 7).Layout == ActorDataLayout.Aos,
            "short shard cannot pretend to be a tiled hot kernel");
        Require(cold.Recommend(actor, 1, 32, preferredLanes: 1).Layout ==
            ActorDataLayout.Aos, "lane-one has no false tiling optimization");

        var columns = new ActorShardLayoutAdvisorV1(actor + 1, 1, 3);
        Require(columns.Observe(actor + 1, 1,
            ActorShardAccessKindV1.ColumnScan, 128), "column samples admitted");
        Require(columns.Recommend(actor + 1, 1, 40).Layout == ActorDataLayout.Soa,
            "column-local tracing selects field-major SoA");

        var random = new ActorShardLayoutAdvisorV1(actor + 2, 1, 3);
        random.Observe(actor + 2, 1, ActorShardAccessKindV1.RowScan, 100);
        random.Observe(actor + 2, 1, ActorShardAccessKindV1.RandomLookup, 200);
        Require(random.Recommend(actor + 2, 1, 40).Layout == ActorDataLayout.Aos,
            "random access disqualifies unsafe row-major specialization");

        var quota = new ActorShardLayoutAdvisorV1(actor + 3, 1,
            fields: 3, sampleLimit: 4096);
        // Fully warm the observation method before asserting that the hot
        // trace path has no per-sample CLR allocations.
        for (int i = 0; i < 128; ++i)
            quota.Observe(actor + 3, 1, ActorShardAccessKindV1.RowScan);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2048; ++i)
            quota.Observe(actor + 3, 1, ActorShardAccessKindV1.RowScan);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated == 0, "hot sampling path allocates no managed bytes");

        Deny<InvalidOperationException>(() => columns.Observe(actor + 2, 1,
            ActorShardAccessKindV1.ColumnScan), "foreign actor generation denied");
        Deny<InvalidOperationException>(() => columns.Recommend(actor + 1, 2, 40),
            "stale actor incarnation cannot use prior profile");
        Deny<ArgumentOutOfRangeException>(() => columns.Observe(actor + 1, 1,
            (ActorShardAccessKindV1)999), "undefined access kind");
        Deny<ArgumentOutOfRangeException>(() => columns.Observe(actor + 1, 1,
            ActorShardAccessKindV1.RowScan, count: -1), "negative sample count");
        Deny<ArgumentOutOfRangeException>(() => columns.Recommend(actor + 1, 1,
            40, preferredLanes: 3), "non-power-of-two lane width");
        Deny<ArgumentOutOfRangeException>(() => columns.Recommend(actor + 1, 1,
            int.MaxValue), "unbounded allocation rejected before native memory");
        Deny<ArgumentOutOfRangeException>(() =>
            new ActorShardLayoutAdvisorV1(0, 1, 3), "zero actor identity");
        Deny<ArgumentOutOfRangeException>(() =>
            new ActorShardLayoutAdvisorV1(actor, 0, 3), "zero incarnation");
        Deny<ArgumentOutOfRangeException>(() =>
            new ActorShardLayoutAdvisorV1(actor, 1, 0), "invalid fields");
        Deny<ArgumentOutOfRangeException>(() =>
            new ActorShardLayoutAdvisorV1(actor, 1, 3, sampleLimit: 1),
            "unbounded or unsupported sampling policy");

        // Recommendations are real inputs to the existing single-layout
        // actor-owned native shard, not a simulated alternate storage array.
        ExerciseNativeShard(actor + 4, ActorShardAccessKindV1.RowScan,
            ActorDataLayout.Aosoa, 8);
        ExerciseNativeShard(actor + 5, ActorShardAccessKindV1.ColumnScan,
            ActorDataLayout.Soa, 1);
        ExerciseNativeShard(actor + 6, ActorShardAccessKindV1.RandomLookup,
            ActorDataLayout.Aos, 1);

        columns.Retire(actor + 1, 1);
        Require(columns.Samples == 0, "retirement scrubs trace counters");
        Deny<InvalidOperationException>(() => columns.Observe(actor + 1, 1,
            ActorShardAccessKindV1.ColumnScan), "retired advisor cannot record");
        Deny<InvalidOperationException>(() => columns.Recommend(actor + 1, 1, 40),
            "retired advisor cannot choose live memory layout");
        Deny<InvalidOperationException>(() => columns.Retire(actor + 1, 1),
            "retirement cannot be replayed");

        Console.WriteLine($"PASS {checks} NativeAOT profile-guided layout advisor checks (host only)");
    }

    private static void ExerciseNativeShard(ulong actor,
        ActorShardAccessKindV1 access, ActorDataLayout expected, int expectedLanes)
    {
        var advisor = new ActorShardLayoutAdvisorV1(actor, 1, 3);
        advisor.Observe(actor, 1, access, 192);
        var selected = advisor.Recommend(actor, 1, rows: 40);
        Require(selected.Layout == expected && selected.Lanes == expectedLanes,
            "native-backed choice matches bounded trace");
        using var arena = new ActorArena(actor, incarnation: 1,
            persistentBytes: 1024 * 1024, scratchBytes: 4096,
            maxAllocations: 8, maxLifetimePins: 4, maxReadLoans: 4);
        using var turn = arena.EnterTurn(actor, 1);
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
    }
}
