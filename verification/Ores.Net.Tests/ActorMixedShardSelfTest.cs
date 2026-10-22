using Ores.Net.Runtime;

internal static class ActorMixedShardSelfTest
{
    private static readonly ActorMixedField[] Fields =
    {
        new("id", ActorMixedScalarKind.U32),
        new("x", ActorMixedScalarKind.F64),
        new("flag", ActorMixedScalarKind.Bool),
        new("next", ActorMixedScalarKind.Ref32)
    };
    private static void Require(bool ok, string label)
    {
        if (!ok) throw new Exception("Mixed actor shard regression: " + label);
    }
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (OverflowException) { return; }
        throw new Exception("Expected mixed actor shard rejection: " + label);
    }
    private static ActorArena NewArena(ulong id) => new(id, incarnation: 1,
        persistentBytes: 2 * 1024 * 1024, scratchBytes: 64 * 1024,
        maxAllocations: 32, maxLifetimePins: 8, maxReadLoans: 8);

    private static int FieldWidth(ActorMixedScalarKind type) => type switch
    {
        ActorMixedScalarKind.Bool => 1,
        ActorMixedScalarKind.F64 => 8,
        ActorMixedScalarKind.U32 or ActorMixedScalarKind.Ref32 => 4,
        _ => throw new Exception("unknown test scalar")
    };

    private static void ExercisePhysicalLayoutSweep()
    {
        int id = 8300;
        foreach (var kind in new[] {
            ActorDataLayout.Aos, ActorDataLayout.Soa,
            ActorDataLayout.Aosoa, ActorDataLayout.Soaos
        })
        foreach (int rows in new[] { 1, 2, 3, 5, 8, 17 })
        foreach (int lanes in (kind == ActorDataLayout.Aosoa
            ? new[] { 1, 2, 4, 8 } : new[] { 1 }))
        {
            ulong actorId = (ulong)id++;
            using var arena = NewArena(actorId);
            using var turn = arena.EnterTurn(actorId, 1);
            var shard = new ActorMixedShard(arena, turn, Fields, rows, kind,
                lanes: lanes, groups: kind == ActorDataLayout.Soaos
                    ? new[] { new[] { 0, 2 }, new[] { 1, 3 } } : null);
            var occupied = new HashSet<int>();
            for (int row = 0; row < rows; row++)
            for (int field = 0; field < Fields.Length; field++)
            {
                int offset = shard.GetPhysicalByteOffset(row, Fields[field].Name);
                int width = FieldWidth(Fields[field].Kind);
                Require(offset >= 0 && offset <= shard.ByteLength - width,
                    "mixed cell must be completely inside allocated arena");
                Require(offset % width == 0, "mixed scalar must be naturally aligned");
                for (int i = 0; i < width; i++)
                    Require(occupied.Add(checked(offset + i)),
                        "two distinct logical cells overlap a physical byte");
            }
            Reject(() => shard.GetPhysicalByteOffset(rows, "id"),
                "tail padding must not introduce live ECS rows");
            Reject(() => shard.GetPhysicalByteOffset(0, "toString"),
                "inherited names are not field declarations");
            shard.Release(turn);
            Require(arena.PersistentLiveBytes == 0,
                "swept shard releases all native bytes");
        }
    }

    public static void Run()
    {
        var kinds = new[]
        {
            ActorDataLayout.Aos, ActorDataLayout.Soa,
            ActorDataLayout.Aosoa, ActorDataLayout.Soaos
        };
        int id = 7900;
        foreach (var kind in kinds)
        {
            using var arena = NewArena((ulong)id++);
            using var turn = arena.EnterTurn(arena.ActorId, 1);
            int rows = kind == ActorDataLayout.Aosoa ? 5 : 3;
            int[][]? groups = kind == ActorDataLayout.Soaos
                ? new[] { new[] { 0, 2 }, new[] { 1, 3 } }
                : null;
            var shard = new ActorMixedShard(arena, turn, Fields, rows, kind,
                lanes: kind == ActorDataLayout.Aosoa ? 4 : 1, groups: groups);
            int expectedBytes = kind == ActorDataLayout.Soa ? 56 :
                kind == ActorDataLayout.Aosoa ? 144 : 72;
            int payload = kind == ActorDataLayout.Soa ? 51 : expectedBytes;
            Require(shard.ByteLength == expectedBytes, kind + " byte budget");
            Require(shard.PayloadBytes == payload, kind + " field payload");
            for (int row = 0; row < rows; row++)
            {
                shard.WriteU32(turn, row, "id", uint.MaxValue - (uint)row);
                shard.WriteF64(turn, row, "x",
                    row == 0 ? -0.0 : row == 1 ? double.NaN : row + 0.25);
                shard.WriteBool(turn, row, "flag", row % 2 == 0);
                shard.WriteU32(turn, row, "next", (uint)row + 1);
            }
            Require(shard.ReadU32(turn, 0, "id") == uint.MaxValue, "unsigned 32-bit");
            Require(BitConverter.DoubleToInt64Bits(shard.ReadF64(turn, 0, "x")) ==
                BitConverter.DoubleToInt64Bits(-0.0), "negative zero preserved");
            Require(double.IsNaN(shard.ReadF64(turn, 1, "x")), "NaN preserved");
            Require(shard.ReadBool(turn, 2, "flag"), "boolean preserved");
            Require(shard.ReadU32(turn, rows - 1, "next") == rows, "reference word");
            Reject(() => shard.WriteBool(turn, 0, "id", true), "typed field");
            Reject(() => shard.WriteF64(turn, 0, "next", 2), "typed f64");
            Reject(() => shard.ReadU32(turn, rows, "id"), "row bounds");
            Reject(() => shard.ReadU32(turn, 0, "constructor"), "unknown field");
            if (kind == ActorDataLayout.Aosoa)
                Require(shard.GetPhysicalByteOffset(4, "id") == 72,
                    "AoSoA fourth-lane tail/next tile");
            if (kind == ActorDataLayout.Soaos)
                Require(shard.GetPhysicalByteOffset(2, "next") == 64,
                    "SoAoS second grouped subrecord");
            if (kind == ActorDataLayout.Soa)
                Require(shard.GetPhysicalByteOffset(2, "x") == 32,
                    "SoA independent segment base alignment");
            shard.Release(turn);
            Require(arena.PersistentLiveBytes == 0, "native bytes fully released");
            Reject(() => shard.ReadU32(turn, 0, "id"), "released shard");
        }

        using (var a = NewArena(8000))
        using (var b = NewArena(8001))
        using (var at = a.EnterTurn(8000, 1))
        using (var bt = b.EnterTurn(8001, 1))
        {
            var one = new ActorMixedShard(a, at, Fields, 2, ActorDataLayout.Soa);
            var two = new ActorMixedShard(b, bt, Fields, 2, ActorDataLayout.Soa);
            one.WriteU32(at, 0, "id", 11);
            two.WriteU32(bt, 0, "id", 99);
            Require(one.ReadU32(at, 0, "id") == 11, "actor A isolated");
            Require(two.ReadU32(bt, 0, "id") == 99, "actor B isolated");
            Reject(() => one.ReadU32(bt, 0, "id"), "foreign actor B turn");
            Reject(() => two.WriteU32(at, 0, "id", 12), "foreign actor A turn");
            one.Release(at); two.Release(bt);
        }

        using (var a = NewArena(8002))
        using (var turn = a.EnterTurn(8002, 1))
        {
            Reject(() => new ActorMixedShard(a, turn, Fields, 3,
                ActorDataLayout.Soa, maxBytes: 55), "whole buffer including padding quota");
            Reject(() => new ActorMixedShard(a, turn, Fields, 5,
                ActorDataLayout.Aosoa, lanes: 3), "non power-of-two tile");
            Reject(() => new ActorMixedShard(a, turn, Fields, 3,
                ActorDataLayout.Soaos, groups: new[] { new[] { 0 }, new[] { 1 } }),
                "incomplete SoAoS partition");
            Reject(() => new ActorMixedShard(a, turn, Fields, 3,
                ActorDataLayout.Soaos, groups: new[] { new[] { 0, 1 }, new[] { 1, 2, 3 } }),
                "duplicate SoAoS column");
            Reject(() => new ActorMixedShard(a, turn, Fields, int.MaxValue,
                ActorDataLayout.Aos), "oversized geometry rejected before native allocation");
            Require(a.PersistentLiveBytes == 0, "failed admission never commits bytes");
            var valid = new ActorMixedShard(a, turn, Fields, 3, ActorDataLayout.Aos);
            valid.WriteU32(turn, 0, "id", 42);
            Require(valid.ReadU32(turn, 0, "id") == 42, "subsequent valid admission");
            valid.Release(turn);
        }
        ExercisePhysicalLayoutSweep();
        Console.WriteLine("PASS .NET unmanaged heterogeneous actor shards in four layouts");
    }
}
