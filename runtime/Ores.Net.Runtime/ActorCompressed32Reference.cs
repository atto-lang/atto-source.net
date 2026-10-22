namespace Ores.Net.Runtime;

/// <summary>
/// Unsigned 32-bit actor-relative managed-reference representation.
///
/// This is not a native pointer, ActorRef, row handle, FFI handle, or capability.
/// Ownership, incarnation, object-start issuance, generation/liveness, type/layout,
/// and borrow authorization remain mandatory checks in the owning arena/runtime.
/// </summary>
public static class ActorCompressed32Reference
{
    public const long ObjectAlignmentBytes = 8;
    public const long MaximumEncodedStartingOffsetBytes = 34_359_738_352L;
    public const long MaximumLogicalSpanBytes = 34_359_738_360L;

    public static uint Encode(long canonicalActorOffsetBytes)
    {
        if (canonicalActorOffsetBytes < 0 ||
            canonicalActorOffsetBytes > MaximumEncodedStartingOffsetBytes ||
            (canonicalActorOffsetBytes & (ObjectAlignmentBytes - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(canonicalActorOffsetBytes),
                "actor-relative offset cannot be represented by compressed32");

        return checked((uint)(canonicalActorOffsetBytes / ObjectAlignmentBytes + 1));
    }

    public static long? Decode(uint word)
    {
        if (word == 0)
            return null;
        return checked(((long)word - 1L) * ObjectAlignmentBytes);
    }

    /// <summary>
    /// Decode a reference stored in a wider native scalar/tagged cell. Upper
    /// bits must be zero; accidental signed extension is rejected.
    /// </summary>
    public static long? DecodeStored(ulong storedWord)
    {
        if (storedWord > uint.MaxValue)
            throw new InvalidOperationException(
                "compressed32 reference has nonzero bits above bit 31");
        return Decode((uint)storedWord);
    }

    public static long DereferenceChecked(ulong storedWord,
        long logicalSpanBytes, long accessBytes)
    {
        long offset = DecodeStored(storedWord) ??
            throw new InvalidOperationException("null compressed32 reference");

        // A compressed actor-relative reference can address at most its
        // canonical 32-bit logical span, even if the host supplies a wider
        // integer. Never let an oversized span authorize a crossing read.
        if (logicalSpanBytes < 0 ||
            logicalSpanBytes > MaximumLogicalSpanBytes ||
            accessBytes <= 0 || offset > logicalSpanBytes ||
            accessBytes > logicalSpanBytes - offset)
            throw new InvalidOperationException(
                "compressed32 reference is outside the logical actor span");

        return offset;
    }
}
