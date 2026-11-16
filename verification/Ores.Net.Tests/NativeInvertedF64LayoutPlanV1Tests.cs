using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class NativeInvertedF64LayoutPlanV1Tests
{
    private static int checks;

    private static void Require(bool ok, string why)
    {
        ++checks;
        if (!ok) throw new Exception("Inverted F64 static layout: " + why);
    }

    private static void Deny(NativeInvertedF64LayoutRequestV1 candidate,
        string diagnostic)
    {
        ++checks;
        try { _ = NativeInvertedF64LayoutVerifierV1.Verify(candidate); }
        catch (FormatException ex)
            when (ex.Message.Contains(diagnostic, StringComparison.Ordinal)) { return; }
        throw new Exception("Unsupported inverted layout admitted: " + diagnostic);
    }

    private static void RejectOffset(Action action, string reason)
    {
        ++checks;
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Invalid physical index admitted: " + reason);
    }

    internal static void Run()
    {
        var nominal = new List<string> { "position", "velocity", "checksum" };
        foreach (var layout in new[] {
            ActorDataLayout.Aos, ActorDataLayout.Soa, ActorDataLayout.Aosoa
        })
        {
            var plan = NativeInvertedF64LayoutVerifierV1.Verify(
                new("Particle", 17, nominal, layout,
                    layout == ActorDataLayout.Aosoa ? 8 : 1));
            Require(plan.ClassName == "Particle" && plan.Rows == 17 &&
                plan.Fields == 3 && plan.FieldNames[0] == "position" &&
                !plan.SourceGuestEmissionCertified, "frozen source-free F64 plan");
            Require(plan.ByteLength == (layout == ActorDataLayout.Aosoa ? 576 : 408) &&
                plan.PhysicalRows == (layout == ActorDataLayout.Aosoa ? 24 : 17),
                "precise single authoritative native backing");
            var used = new HashSet<int>();
            for (int row = 0; row < plan.Rows; ++row)
                for (int field = 0; field < plan.Fields; ++field)
                {
                    int offset = plan.ByteOffset(row, field);
                    Require(offset >= 0 && offset % sizeof(double) == 0 &&
                        offset + sizeof(double) <= plan.ByteLength &&
                        used.Add(offset), "all F64 addresses are non-overlapping");
                }
            Require(plan.ByteOffset(3, 2) == (layout switch {
                ActorDataLayout.Aos => 88,
                ActorDataLayout.Soa => 296,
                ActorDataLayout.Aosoa => 152,
                _ => throw new Exception("unsupported layout")
            }), "explicit byte geometry matches scalar shard ABI");
            RejectOffset(() => plan.ByteOffset(-1, 0), "negative row");
            RejectOffset(() => plan.ByteOffset(17, 0), "row beyond logical tail");
            RejectOffset(() => plan.ByteOffset(0, -1), "negative field");
            RejectOffset(() => plan.ByteOffset(0, 3), "field beyond schema");

            const ulong actorId = 19661;
            using var arena = new ActorArena(actorId, incarnation: 1,
                persistentBytes: 1024 * 1024, scratchBytes: 4096,
                maxAllocations: 8, maxReadLoans: 4, maxLifetimePins: 4);
            using var turn = arena.EnterTurn(actorId, 1);
            var shard = plan.AllocateTrustedHostShard(arena, turn);
            Require(shard.Rows == plan.Rows && shard.Fields == plan.Fields &&
                shard.ByteLength == plan.ByteLength &&
                shard.PhysicalRows == plan.PhysicalRows &&
                shard.Layout == plan.Layout && shard.Lanes == plan.Lanes,
                "compiled metadata and unmanaged runtime shape match");
            for (int row = 0; row < plan.Rows; ++row)
            {
                shard.Write(turn, row, 0, row + 0.5);
                shard.Write(turn, row, 1, 3.0);
                shard.Write(turn, row, 2, -42.0);
            }
            shard.Integrate(turn, 0, 1, 0.25);
            for (int row = 0; row < plan.Rows; ++row)
                Require(shard.Read(turn, row, 0) == row + 1.25 &&
                    shard.Read(turn, row, 1) == 3.0 &&
                    shard.Read(turn, row, 2) == -42.0,
                    "all three layouts preserve identical native field values");
            shard.Release(turn);
            Require(arena.PersistentLiveBytes == 0 && arena.LiveAllocations == 0,
                "real arena allocation is refunded after explicit release");
        }
        nominal[0] = "mutated_name";
        nominal.Add("extra");
        var frozen = NativeInvertedF64LayoutVerifierV1.Verify(
            new("Particle", 3, new[] { "a", "b" }, ActorDataLayout.Soa));
        Require(frozen.DescriptorIdentity.Contains("a,b", StringComparison.Ordinal) &&
            frozen.Fields == 2, "nominal descriptor independent of other caller schemas");
        var names = new List<string> { "a", "b" };
        var independent = NativeInvertedF64LayoutVerifierV1.Verify(
            new("Particle", 3, names, ActorDataLayout.Aosoa, 2));
        string identity = independent.DescriptorIdentity;
        names[0] = "reused";
        names.Add("new_field");
        Require(independent.FieldNames[0] == "a" && independent.Fields == 2 &&
            independent.DescriptorIdentity == identity,
            "verified schema is immutable against caller mutation");
        Require(identity != frozen.DescriptorIdentity,
            "different physical layouts produce different static descriptors");

        Deny(new("Particle", 3, Array.Empty<string>(), ActorDataLayout.Aos),
            "E_INVERTED_FIELDS");
        Deny(new("Particle", 3, Enumerable.Repeat("field", 33).ToArray(),
            ActorDataLayout.Aos), "E_INVERTED_FIELDS");
        Deny(new("Particle", 3, new[] { "a", "a" }, ActorDataLayout.Aos),
            "E_INVERTED_FIELD_NAME");
        Deny(new("Particle", 3, new[] { "good", "bad name" }, ActorDataLayout.Aos),
            "E_INVERTED_FIELD_NAME");
        Deny(new("Particle", 3, new[] { "a,b" }, ActorDataLayout.Aos),
            "E_INVERTED_FIELD_NAME");
        Deny(new("Particle", 3, new[] { "x" }, (ActorDataLayout)127),
            "E_INVERTED_LAYOUT");
        Deny(new("Particle", 3, new[] { "x" }, ActorDataLayout.Soa, 8),
            "E_INVERTED_LANES");
        Deny(new("Particle", 3, new[] { "x" }, ActorDataLayout.Aosoa, 3),
            "E_INVERTED_LANES");
        Deny(new("Particle", 3, new[] { "x" }, ActorDataLayout.Aosoa, 2048),
            "E_INVERTED_LANES");
        Deny(new("Particle", 0, new[] { "x" }, ActorDataLayout.Aos),
            "E_INVERTED_ROWS");
        Deny(new("Particle", -1, new[] { "x" }, ActorDataLayout.Aos),
            "E_INVERTED_ROWS");
        Deny(new("Particle", int.MaxValue, new[] { "x" }, ActorDataLayout.Aos),
            "E_INVERTED_BUDGET");
        Deny(new("Particle", 699050, new[] { "x", "y", "z" },
            ActorDataLayout.Aosoa, 8), "E_INVERTED_BUDGET");
        Require(NativeInvertedF64LayoutVerifierV1.Verify(
            new("Particle", 699050, new[] { "x", "y", "z" },
            ActorDataLayout.Aos)).ByteLength == 16777200,
            "valid logical layout within 16MiB cap is still admitted");
        Deny(new("Bad class", 1, new[] { "x" }, ActorDataLayout.Aos),
            "E_INVERTED_CLASS_NAME");

        Console.WriteLine($"PASS {checks} checked .NET NativeAOT inverted F64 layout metadata checks (host only)");
    }
}
