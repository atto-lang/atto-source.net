namespace Ores.Net.Compiler;

/// <summary>
/// Bridges a REAL compiler-verified immutable native record layout into a
/// bounded readonly field projection. Unlike hand-authored Source descriptors,
/// the field names, types, ordering, and shape identity are taken from the
/// existing NativeAggregateLayoutVerifier-issued immutable plan.
/// This remains non-emitting: no CIL structural call or NativeAOT guest ABI.
/// </summary>
public static class ReadonlyVerifiedRecordBridgeV1
{
    public static ReadonlyShapeProjectionV1.Plan ProveVerifiedRecord(
        VerifiedNativeAggregateLayout source,
        long verifiedLayoutEpoch,
        long verifiedBorrowRegion,
        IReadOnlyList<ReadonlyShapeProjectionV1.RequiredField> expected)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expected);
        if (source.Kind != NativeAggregateValueKind.Record ||
            source.Mutable || source.ShapeId is <= 0 or > int.MaxValue)
            throw new InvalidOperationException(
                "only positive-source-id immutable record layouts may project");
        if (source.FieldNames.Count != source.ElementTypes.Count)
            throw new InvalidOperationException("record layout field schema mismatch");
        var fields = new List<ReadonlyShapeProjectionV1.Field>();
        for (int slot = 0; slot < source.FieldNames.Count; slot++)
        {
            // Only the existing checked i64 record layout can supply fields.
            if (source.ElementTypes[slot] != IrValueType.I64)
                throw new InvalidOperationException(
                    "non-i64 source field cannot be projected as readonly int");
            fields.Add(new ReadonlyShapeProjectionV1.Field(
                source.FieldNames[slot], ReadonlyShapeProjectionV1.Scalar.I64,
                slot, true, true, true, false));
        }
        return ReadonlyShapeProjectionV1.Prove(new ReadonlyShapeProjectionV1.Source(
            ReadonlyShapeProjectionV1.Origin.Record,
            checked((int)source.ShapeId), verifiedLayoutEpoch,
            verifiedBorrowRegion, false, fields.AsReadOnly()), expected);
    }
}
