using System.Text.Json;
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

    private static void ExerciseReadLoanLifetime()
    {
        using var arena = NewArena(8420);
        var turn = arena.EnterTurn(8420, 1);
        var shard = new ActorMixedShard(arena, turn, Fields, 3,
            ActorDataLayout.Soaos, groups: new[] { new[] { 0, 2 }, new[] { 1, 3 } });
        shard.WriteU32(turn, 0, "id", 42);
        var loan = shard.BorrowRead(turn);
        Require(loan.Length == shard.ByteLength,
            "read loan covers the exact persistent shard allocation");
        Require(shard.ReadU32(turn, 0, "id") == 42,
            "immutable loan must still allow owner-turn reads");
        Reject(() => shard.WriteU32(turn, 0, "id", 99),
            "read loan prohibits any writes in the same shard");
        Reject(() => shard.WriteBool(turn, 0, "flag", true),
            "read loan covers all shards' fields until field-sensitive lowering exists");
        Reject(() => shard.Release(turn),
            "read loan blocks whole-shard reclamation");
        // Suspend/end this physical turn; a read-only loan is a lifetime fence,
        // not a second thread authority or a way to access the memory without
        // entering the owning actor.
        turn.Dispose();
        using (var resumed = arena.EnterTurn(8420, 1))
        {
            Require(shard.ReadU32(resumed, 0, "id") == 42,
                "immutable loan survives suspension and owner-turn migration");
            Reject(() => shard.WriteU32(resumed, 0, "id", 99),
                "read loan remains active across turns");
            loan.Dispose();
            loan.Dispose();
            shard.WriteU32(resumed, 0, "id", 99);
            Require(shard.ReadU32(resumed, 0, "id") == 99,
                "writes resume after the last loan closes");
            shard.Release(resumed);
            Require(arena.PersistentLiveBytes == 0, "loan-retired bytes refunded");
            Reject(() => shard.BorrowRead(resumed),
                "released mixed shard cannot mint a new loan");
        }
    }

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

    private static void ExercisePortableGoldenVectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "mixed-layout-golden-v1.json")));
        var contract = document.RootElement;
        Require(contract.GetProperty("version").GetInt32() == 1, "portable vector version");
        Require(contract.GetProperty("scope").GetString() == "runtime-host-mixed-layout-only",
            "portable vectors cannot attest source compilation");
        var fields = contract.GetProperty("fields").EnumerateArray().Select(field =>
            new ActorMixedField(field.GetProperty("name").GetString()!,
                field.GetProperty("type").GetString() switch
                {
                    "u32" => ActorMixedScalarKind.U32,
                    "f64" => ActorMixedScalarKind.F64,
                    "bool" => ActorMixedScalarKind.Bool,
                    "ref32" => ActorMixedScalarKind.Ref32,
                    _ => throw new Exception("unknown portable scalar")
                })).ToArray();
        var names = fields.Select((field, index) => (field.Name, Index: index))
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.Ordinal);
        ulong actorId = 21000;
        foreach (var vector in contract.GetProperty("layouts").EnumerateArray())
        {
            var kind = vector.GetProperty("layout").GetString() switch
            {
                "aos" => ActorDataLayout.Aos,
                "soa" => ActorDataLayout.Soa,
                "aosoa" => ActorDataLayout.Aosoa,
                "soaos" => ActorDataLayout.Soaos,
                _ => throw new Exception("unknown portable layout")
            };
            int rows = vector.GetProperty("rows").GetInt32();
            int lanes = vector.TryGetProperty("tileWidth", out var tile) ? tile.GetInt32() : 1;
            int[][]? groups = vector.TryGetProperty("groups", out var groupData)
                ? groupData.EnumerateArray().Select(group => group.EnumerateArray()
                    .Select(name => names[name.GetString()!]).ToArray()).ToArray()
                : null;
            using var arena = NewArena(actorId++);
            using var turn = arena.EnterTurn(arena.ActorId, 1);
            foreach (var invalidRows in contract.GetProperty("rejectRows").EnumerateArray())
                Reject(() => new ActorMixedShard(arena, turn, fields,
                    invalidRows.GetInt32(), kind, lanes: lanes, groups: groups),
                    "portable nonpositive rows");
            Require(arena.PersistentLiveBytes == 0, "rejected rows commit no payload");

            var shard = new ActorMixedShard(arena, turn, fields, rows, kind,
                lanes: lanes, groups: groups);
            Require(shard.ByteLength == vector.GetProperty("byteLength").GetInt32(),
                "portable physical byte length");
            Require(shard.PayloadBytes == vector.GetProperty("payloadBytes").GetInt64(),
                "portable payload length");
            foreach (var entry in vector.GetProperty("offsets").EnumerateArray())
                Require(shard.GetPhysicalByteOffset(entry[0].GetInt32(), entry[1].GetString()!)
                    == entry[2].GetInt32(), "portable golden byte offset");
            for (int row = 0; row < rows; row++)
            {
                Require(shard.ReadU32(turn, row, "id") == 0, "zero initialized u32");
                Require(!shard.ReadBool(turn, row, "flag"), "zero initialized bool");
            }
            // Verify every physical cell, not only selected golden offsets.
            // Padding and masked AoSoA tail lanes must never alias live rows.
            var occupiedBytes = new HashSet<int>();
            for (int row = 0; row < rows; row++)
            foreach (var field in fields)
            {
                int width = FieldWidth(field.Kind);
                int offset = shard.GetPhysicalByteOffset(row, field.Name);
                Require(offset >= 0 && offset <= shard.ByteLength - width,
                    "portable logical cell inside physical allocation");
                Require(offset % width == 0, "portable scalar alignment");
                for (int i = 0; i < width; i++)
                    Require(occupiedBytes.Add(checked(offset + i)),
                        "portable live scalar cells cannot overlap");
            }
            for (int row = 0; row < rows; row++)
            {
                Require(shard.ReadU32(turn, row, "next") == 0, "portable ref32 zero");
                Require(BitConverter.DoubleToInt64Bits(shard.ReadF64(turn, row, "x")) == 0,
                    "portable f64 +0 initialized");
                shard.WriteU32(turn, row, "id", uint.MaxValue - (uint)row);
                shard.WriteU32(turn, row, "next", 0x80000000U + (uint)row);
                shard.WriteBool(turn, row, "flag", row % 2 == 0);
                shard.WriteF64(turn, row, "x", row + 0.375);
            }
            void VerifyIndependentCells()
            {
                for (int row = 0; row < rows; row++)
                {
                    Require(shard.ReadU32(turn, row, "id") == uint.MaxValue - (uint)row,
                        "portable id row isolation");
                    Require(shard.ReadU32(turn, row, "next") == 0x80000000U + (uint)row,
                        "portable ref32 row isolation");
                    Require(shard.ReadBool(turn, row, "flag") == (row % 2 == 0),
                        "portable bool row isolation");
                    Require(shard.ReadF64(turn, row, "x") == row + 0.375,
                        "portable f64 row isolation");
                }
            }
            VerifyIndependentCells();
            Reject(() => shard.WriteBool(turn, 0, "id", true),
                "portable mismatched bool writer");
            Reject(() => shard.WriteF64(turn, 0, "next", 1.0),
                "portable mismatched f64 writer");
            Reject(() => shard.WriteBool(turn, -1, "flag", true),
                "portable invalid lower row");
            Reject(() => shard.ReadF64(turn, rows, "x"),
                "portable invalid upper row");
            VerifyIndependentCells(); // All rejected operations preserve every cell.

            shard.WriteU32(turn, 0, "id", uint.MaxValue);
            shard.WriteU32(turn, 0, "next", uint.MaxValue);
            shard.WriteBool(turn, 0, "flag", true);
            shard.WriteF64(turn, 0, "x", -0.0);
            shard.WriteF64(turn, 1, "x", double.NaN);
            shard.WriteF64(turn, 2, "x", double.PositiveInfinity);
            Require(shard.ReadU32(turn, 0, "id") == uint.MaxValue, "portable u32 max");
            Require(shard.ReadU32(turn, 0, "next") == uint.MaxValue, "portable ref32 max");
            Require(shard.ReadBool(turn, 0, "flag"), "portable bool");
            Require(BitConverter.DoubleToInt64Bits(shard.ReadF64(turn, 0, "x")) ==
                BitConverter.DoubleToInt64Bits(-0.0), "portable signed zero");
            Require(double.IsNaN(shard.ReadF64(turn, 1, "x")), "portable NaN");
            Require(double.IsPositiveInfinity(shard.ReadF64(turn, 2, "x")), "portable infinity");
            shard.WriteF64(turn, 2, "x", double.NegativeInfinity);
            Require(double.IsNegativeInfinity(shard.ReadF64(turn, 2, "x")), "portable negative infinity");
            Reject(() => shard.WriteU32(turn, rows, "id", 42), "portable upper row fence");
            Reject(() => shard.ReadU32(turn, -1, "id"), "portable lower row fence");
            Reject(() => shard.WriteU32(turn, 0, "not_declared", 42), "portable field fence");
            Require(shard.ReadU32(turn, 0, "id") == uint.MaxValue,
                "rejected writes preserve payload");
            shard.Release(turn);
            Require(arena.PersistentLiveBytes == 0, "portable release reclaims payload");
            Reject(() => shard.ReadU32(turn, 0, "id"), "portable stale read");
            Reject(() => shard.WriteU32(turn, 0, "id", 1), "portable stale write");
            shard.Release(turn);
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
        ExercisePortableGoldenVectors();
        ExerciseReadLoanLifetime();
        Console.WriteLine("PASS .NET unmanaged heterogeneous actor shards in four layouts");
    }
}
