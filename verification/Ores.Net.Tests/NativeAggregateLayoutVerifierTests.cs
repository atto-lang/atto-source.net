using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class NativeAggregateLayoutVerifierTests
{
    private static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception("Native compiler aggregate layout: " + message);
    }

    private static void Reject(NativeAggregateLayoutRequest request, string diagnostic)
    {
        try
        {
            _ = NativeAggregateLayoutVerifier.Verify(request);
        }
        catch (FormatException ex) when (
            ex.Message.Contains(diagnostic, StringComparison.Ordinal)) { return; }
        throw new Exception("Unverified aggregate layout admitted: " + diagnostic);
    }

    public static void Run()
    {
        var ownedTypes = new List<IrValueType> { IrValueType.I64, IrValueType.I64 };
        var tuple = NativeAggregateLayoutVerifier.Verify(
            new(NativeAggregateValueKind.Tuple, ownedTypes));
        Require(tuple.Kind == NativeAggregateValueKind.Tuple &&
            tuple.ElementTypes.Count == 2 && tuple.ByteLength == 40 && !tuple.Mutable &&
            !tuple.SourceGuestEmissionCertified,
            "checked 2-slot native tuple layout with no guest emission claim");
        ownedTypes[0] = IrValueType.Bool;
        ownedTypes.Add(IrValueType.I64);
        Require(tuple.ElementTypes.Count == 2 && tuple.ElementTypes[0] == IrValueType.I64,
            "verified layout is immutable against caller mutation");

        var fields = new List<string> { "low", "high" };
        var record = NativeAggregateLayoutVerifier.Verify(
            new(NativeAggregateValueKind.Record,
                new[] { IrValueType.I64, IrValueType.I64 }, fields, ShapeId: 17));
        fields[0] = "evil";
        Require(record.FieldNames[0] == "low" && record.ShapeId == 17 &&
                !record.Mutable, "readonly record layout snapshot and static fields");
        var entry = NativeAggregateLayoutVerifier.Verify(
            new(NativeAggregateValueKind.Entry,
                new[] { IrValueType.I64, IrValueType.I64 }));
        var pair = NativeAggregateLayoutVerifier.Verify(
            new(NativeAggregateValueKind.Pair,
                new[] { IrValueType.I64, IrValueType.I64 }));
        Require(entry.Kind != pair.Kind, "Entry and Pair must remain nominally distinct");
        var array = NativeAggregateLayoutVerifier.Verify(
            new(NativeAggregateValueKind.Array, new[] { IrValueType.I64 }));
        Require(array.Mutable && array.ByteLength == 32,
            "bounded native Array has checked writable single-element layout");
        var emptyArray = NativeAggregateLayoutVerifier.Verify(
            new(NativeAggregateValueKind.Array, Array.Empty<IrValueType>(),
                HasExplicitElementWitness: true));
        Require(emptyArray.ByteLength == 24, "typed empty array has a bounded header");

        // Compiler and unmanaged actor runtime must agree on ABI tag values.
        foreach (var (compilerKind, nativeKind) in new[] {
            (NativeAggregateValueKind.Tuple, NativeAggregateKind.Tuple),
            (NativeAggregateValueKind.Record, NativeAggregateKind.Record),
            (NativeAggregateValueKind.Entry, NativeAggregateKind.Entry),
            (NativeAggregateValueKind.Pair, NativeAggregateKind.Pair),
            (NativeAggregateValueKind.Array, NativeAggregateKind.Array)
        })
            Require((byte)compilerKind == (byte)nativeKind,
                "compiler/runtime native aggregate kind tags agree");

        Reject(new(NativeAggregateValueKind.Tuple, new[] { IrValueType.Bool }),
            "E_AGGREGATE_PAYLOAD");
        Reject(new(NativeAggregateValueKind.Pair, new[] { IrValueType.I64 }),
            "E_AGGREGATE_ARITY");
        Reject(new(NativeAggregateValueKind.Entry, Array.Empty<IrValueType>()),
            "E_AGGREGATE_ARITY");
        Reject(new(NativeAggregateValueKind.Array, Array.Empty<IrValueType>()),
            "E_ARRAY_TYPE_WITNESS");
        Reject(new(NativeAggregateValueKind.Record,
            new[] { IrValueType.I64 }, new[] { "field" }), "E_RECORD_SHAPE");
        Reject(new(NativeAggregateValueKind.Record,
            new[] { IrValueType.I64 }, new[] { "field", "extra" }, ShapeId: 7),
            "E_RECORD_ARITY");
        Reject(new(NativeAggregateValueKind.Record,
            new[] { IrValueType.I64, IrValueType.I64 },
            new[] { "dup", "dup" }, ShapeId: 7), "E_RECORD_DUPLICATE");
        Reject(new(NativeAggregateValueKind.Record,
            new[] { IrValueType.I64 }, new[] { "bad field" }, ShapeId: 7),
            "E_RECORD_STATIC_FIELD");
        Reject(new(NativeAggregateValueKind.Tuple,
            new[] { IrValueType.I64 }, ShapeId: 17), "E_AGGREGATE_SHAPE");
        Reject(new(NativeAggregateValueKind.Tuple,
            new IrValueType[NativeAggregateLayoutVerifier.MaximumProductElements + 1]),
            "E_AGGREGATE_BUDGET");
        Reject(new(NativeAggregateValueKind.Array,
            new IrValueType[NativeAggregateLayoutVerifier.MaximumArrayElements + 1]),
            "E_AGGREGATE_BUDGET");
        foreach (var unsupported in new[] {
            NativeAggregateValueKind.Map, NativeAggregateValueKind.Set,
            NativeAggregateValueKind.List, NativeAggregateValueKind.Vector
        })
            Reject(new(unsupported, Array.Empty<IrValueType>()),
                "E_COLLECTION_FACTORY_UNSUPPORTED");

        Console.WriteLine("PASS .NET native aggregate HIR layout verification (NOT guest emission)");
    }
}
