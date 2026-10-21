using Ores.Net.Runtime;

internal static class ActorShardColumnsSelfTest
{
    public static void Run()
    {
        foreach (ActorDataLayout layout in Enum.GetValues<ActorDataLayout>())
            ExerciseLayout(layout);

        Reject(() =>
        {
            using var arena = NewArena(6100);
            using var turn = arena.EnterTurn(6100, 1);
            _ = new ActorShardColumns(arena, turn, 8, 3,
                ActorDataLayout.Aosoa, lanes: 3);
        }, "non-power-of-two AoSoA lane width");

        Reject(() =>
        {
            using var arena = NewArena(6101);
            using var turn = arena.EnterTurn(6101, 1);
            _ = new ActorShardColumns(arena, turn, 8, 3,
                ActorDataLayout.Soa, lanes: 4);
        }, "SoA cannot pretend to carry AoSoA lane metadata");

        Reject(() =>
        {
            using var arena = NewArena(6102);
            using var turn = arena.EnterTurn(6102, 1);
            _ = new ActorShardColumns(arena, turn, 8, 3,
                ActorDataLayout.Soaos);
        }, "SoAoS requires explicit field groups");

        Reject(() =>
        {
            using var arena = NewArena(6103);
            using var turn = arena.EnterTurn(6103, 1);
            _ = new ActorShardColumns(arena, turn, 8, 3,
                ActorDataLayout.Soaos, soaosGroupWidths: new[] { 2, 0, 1 });
        }, "SoAoS group width cannot be zero");

        Reject(() =>
        {
            using var arena = NewArena(6104);
            using var turn = arena.EnterTurn(6104, 1);
            _ = new ActorShardColumns(arena, turn, 8, 3,
                ActorDataLayout.Soaos, soaosGroupWidths: new[] { 2 });
        }, "SoAoS groups must cover every field");

        Reject(() =>
        {
            using var arena = NewArena(6105);
            using var turn = arena.EnterTurn(6105, 1);
            _ = new ActorShardColumns(arena, turn, 8, 3,
                ActorDataLayout.Soa, soaosGroupWidths: new[] { 2, 1 });
        }, "non-SoAoS layout cannot carry SoAoS metadata");

        ExerciseDistinctSoaosGeometry();

        Console.WriteLine("PASS .NET actor-owned AoS/SoA/AoSoA/SoAoS shard layouts and dense kernel");
    }

    private static void ExerciseLayout(ActorDataLayout layout)
    {
        ulong actorId = 6000UL + (ulong)layout;
        using var arena = NewArena(actorId);
        using var turn = arena.EnterTurn(actorId, 1);
        int lanes = layout == ActorDataLayout.Aosoa ? 4 : 1;
        int[]? groups = layout == ActorDataLayout.Soaos ? new[] { 2, 1 } : null;
        var shard = new ActorShardColumns(arena, turn,
            rows: 7, fields: 3, layout, lanes, soaosGroupWidths: groups);

        Require(shard.Rows == 7 && shard.Fields == 3,
            layout + " logical dimensions");
        Require(shard.ByteLength <= ActorShardColumns.MaxShardBytes,
            layout + " bounded physical bytes");
        if (layout == ActorDataLayout.Aosoa)
            Require(shard.PhysicalRows == 8,
                "AoSoA pads to the next tile without changing logical rows");
        if (layout == ActorDataLayout.Soaos)
            Require(shard.GroupCount == 2 && shard.PhysicalRows == 7,
                "SoAoS preserves logical rows and explicit field groups");

        for (int row = 0; row < shard.Rows; row++)
        {
            shard.Write(turn, row, 0, row * 10.0);
            shard.Write(turn, row, 1, row + 0.5);
            shard.Write(turn, row, 2, 1000.0 + row);
        }

        for (int row = 0; row < shard.Rows; row++)
        {
            Require(shard.Read(turn, row, 0) == row * 10.0,
                layout + " field 0 mapping");
            Require(shard.Read(turn, row, 1) == row + 0.5,
                layout + " field 1 mapping");
            Require(shard.Read(turn, row, 2) == 1000.0 + row,
                layout + " field 2 mapping");
        }

        shard.Integrate(turn, positionField: 0, velocityField: 1, dt: 0.25);
        var column = new double[shard.Rows];
        shard.CopyColumnInto(turn, 0, column);
        for (int row = 0; row < column.Length; row++)
        {
            double expected = row * 10.0 + (row + 0.5) * 0.25;
            Require(column[row] == expected,
                layout + " dense integrate/copy parity row " + row);
        }

        Reject(() => shard.Read(turn, -1, 0),
            layout + " negative row bounds");
        Reject(() => shard.Read(turn, 0, 3),
            layout + " field bounds");

        shard.Release(turn);
        Require(arena.PersistentLiveBytes == 0,
            layout + " release refunds actor arena bytes");
        Reject(() => shard.Read(turn, 0, 0),
            layout + " released shard cannot be reused");
    }

    private static void ExerciseDistinctSoaosGeometry()
    {
        using var arena = NewArena(6200);
        using var turn = arena.EnterTurn(6200, 1);
        var soa = new ActorShardColumns(arena, turn,
            rows: 4, fields: 4, ActorDataLayout.Soa);
        var soaos = new ActorShardColumns(arena, turn,
            rows: 4, fields: 4, ActorDataLayout.Soaos,
            soaosGroupWidths: new[] { 2, 2 });

        Require(soa.GetPhysicalByteOffset(1, 1) == 40,
            "SoA field-one row-one byte geometry");
        Require(soaos.GetPhysicalByteOffset(1, 1) == 24,
            "SoAoS first subrecord group byte geometry");
        Require(soa.GetPhysicalByteOffset(1, 2) == 72,
            "SoA field-two row-one byte geometry");
        Require(soaos.GetPhysicalByteOffset(1, 2) == 80,
            "SoAoS second subrecord group byte geometry");
        Require(soa.GetPhysicalByteOffset(1, 1) !=
                soaos.GetPhysicalByteOffset(1, 1),
            "SoAoS must not alias SoA physical geometry");

        soa.Release(turn);
        soaos.Release(turn);
        Require(arena.PersistentLiveBytes == 0,
            "geometry fixtures release both unmanaged allocations");
    }

    private static ActorArena NewArena(ulong actorId) => new(
        actorId, incarnation: 1,
        persistentBytes: 2 * 1024 * 1024,
        scratchBytes: 64 * 1024,
        maxAllocations: 32,
        maxLifetimePins: 8,
        maxReadLoans: 8);

    private static void Reject(Action action, string scenario)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Exception("Actor shard unexpectedly admitted: " + scenario);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor shard regression: " + scenario);
    }
}
