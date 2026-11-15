namespace Ores.Net.Compiler;

/// <summary>
/// Compiler-side aggregate layout request, deliberately independent of CLR
/// collection representations. These are compiler facts, NOT an executable
/// OresIR instruction or authorization to publish NativeAOT guest claims.
/// </summary>
public enum NativeAggregateValueKind : byte
{
    Tuple = 1,
    Record = 2,
    Entry = 3,
    Pair = 4,
    Array = 5,
    List = 6,
    Vector = 7,
    Map = 8,
    Set = 9
}

public sealed record NativeAggregateLayoutRequest(
    NativeAggregateValueKind Kind,
    IReadOnlyList<IrValueType> ElementTypes,
    IReadOnlyList<string>? FieldNames = null,
    long ShapeId = 0,
    bool HasExplicitElementWitness = false);

/// <summary>
/// Immutable checked scalar aggregate layout, usable as the next typed-HIR
/// milestone. Its numeric tags and 24-byte header match ActorNativeAggregateStore,
/// but actual source compiler -> CIL conversion remains deliberately blocked.
/// </summary>
public sealed class VerifiedNativeAggregateLayout
{
    internal VerifiedNativeAggregateLayout(NativeAggregateValueKind kind,
        IReadOnlyList<IrValueType> elements, IReadOnlyList<string> names, long shapeId)
    {
        Kind = kind;
        ElementTypes = elements;
        FieldNames = names;
        ShapeId = shapeId;
    }

    public NativeAggregateValueKind Kind { get; }
    public IReadOnlyList<IrValueType> ElementTypes { get; }
    public IReadOnlyList<string> FieldNames { get; }
    public long ShapeId { get; }
    public int ByteLength => checked(24 + ElementTypes.Count * sizeof(long));
    public bool Mutable => Kind == NativeAggregateValueKind.Array;
    // Do not mint authority for a not-yet-implemented type/borrow/CIL pipeline.
    public bool SourceGuestEmissionCertified => false;
}

public static class NativeAggregateLayoutVerifier
{
    public const int MaximumProductElements = 32;
    public const int MaximumArrayElements = 4096;

    public static VerifiedNativeAggregateLayout Verify(NativeAggregateLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = request.ElementTypes ??
            throw new FormatException("Native aggregate requires checked element types");

        int limit = request.Kind == NativeAggregateValueKind.Array
            ? MaximumArrayElements : MaximumProductElements;
        if (input.Count > limit)
            throw new FormatException("E_AGGREGATE_BUDGET: too many native aggregate elements");

        switch (request.Kind)
        {
            case NativeAggregateValueKind.Tuple:
            case NativeAggregateValueKind.Record:
            case NativeAggregateValueKind.Entry:
            case NativeAggregateValueKind.Pair:
            case NativeAggregateValueKind.Array:
                break;
            default:
                throw new FormatException(
                    "E_COLLECTION_FACTORY_UNSUPPORTED: owning List/Vector/Map/Set requires compiler-verified native implementation");
        }

        // The initial runtime payload is tightly packed native signed i64.
        // Bool, nested graphs, pointers and unknown reference types must NOT
        // be quietly represented as integers or host CLR references.
        foreach (var type in input)
            if (type != IrValueType.I64)
                throw new FormatException(
                    "E_AGGREGATE_PAYLOAD: only compiler-owned i64 scalar fields have native layout");

        if (request.Kind is NativeAggregateValueKind.Entry or NativeAggregateValueKind.Pair &&
            input.Count != 2)
            throw new FormatException("E_AGGREGATE_ARITY: Entry/Pair require exactly two fields");

        if (request.Kind == NativeAggregateValueKind.Array &&
            input.Count == 0 && !request.HasExplicitElementWitness)
            throw new FormatException(
                "E_ARRAY_TYPE_WITNESS: empty native Array needs explicit element type");

        bool record = request.Kind == NativeAggregateValueKind.Record;
        var fields = request.FieldNames ?? Array.Empty<string>();
        if (record)
        {
            if (request.ShapeId <= 0)
                throw new FormatException(
                    "E_RECORD_SHAPE: readonly native record needs verified positive schema");
            if (fields.Count != input.Count)
                throw new FormatException(
                    "E_RECORD_ARITY: record fields must correspond exactly to values");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string field in fields)
            {
                if (string.IsNullOrEmpty(field) || field.Length > 128
                    || !(field[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')
                    || field.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                        or >= '0' and <= '9' or '_')))
                    throw new FormatException(
                        "E_RECORD_STATIC_FIELD: record fields need bounded ASCII identifiers");
                if (!seen.Add(field))
                    throw new FormatException(
                        "E_RECORD_DUPLICATE: duplicate nominal record field");
            }
        }
        else if (request.ShapeId != 0 || fields.Count != 0)
        {
            throw new FormatException(
                "E_AGGREGATE_SHAPE: only record products may carry schema/field metadata");
        }

        // Defensive snapshot: later caller mutation must not change verified
        // layout, element count, property order, or guest physical ABI bytes.
        return new VerifiedNativeAggregateLayout(request.Kind,
            Array.AsReadOnly(input.ToArray()),
            Array.AsReadOnly(fields.ToArray()),
            request.ShapeId);
    }
}
