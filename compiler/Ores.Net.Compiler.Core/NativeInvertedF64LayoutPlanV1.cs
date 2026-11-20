using Ores.Net.Runtime;

namespace Ores.Net.Compiler;

/// <summary>
/// Static, compiler-owned metadata for a future inverted-class F64 scalar
/// region. There is NO source parser, guest CIL emitter, borrow authority or
/// runtime layout conversion in this contract.
/// </summary>
public sealed record NativeInvertedF64LayoutRequestV1(
    string ClassName, int Rows, IReadOnlyList<string> FieldNames,
    ActorDataLayout Layout, int Lanes = 1);

/// <summary>
/// Frozen checked layout metadata; never exposes mutable field storage. An
/// independent actor-owned runtime shard can be created only through a trusted
/// host with its own admitted ActorArenaTurn.
/// </summary>
public sealed class VerifiedNativeInvertedF64LayoutV1
{
    internal VerifiedNativeInvertedF64LayoutV1(
        string className, int rows, IReadOnlyList<string> names,
        ActorDataLayout layout, int lanes, int physicalRows, int byteLength)
    {
        ClassName = className;
        Rows = rows;
        FieldNames = names;
        Layout = layout;
        Lanes = lanes;
        PhysicalRows = physicalRows;
        ByteLength = byteLength;
        DescriptorIdentity = "inverted-f64-v1|" + className + "|" +
            rows.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "|" + string.Join(",", names) + "|" +
            layout.ToString() + "|" +
            lanes.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public string ClassName { get; }
    public int Rows { get; }
    public IReadOnlyList<string> FieldNames { get; }
    public int Fields => FieldNames.Count;
    public ActorDataLayout Layout { get; }
    public int Lanes { get; }
    public int PhysicalRows { get; }
    public int ByteLength { get; }
    // A descriptor identity is only metadata, NEVER an actor authorization.
    public string DescriptorIdentity { get; }
    public bool SourceGuestEmissionCertified => false;

    /// <summary>Checked physical F64 byte offset for one logical cell.</summary>
    public int ByteOffset(int row, int field)
    {
        if ((uint)row >= (uint)Rows)
            throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)field >= (uint)Fields)
            throw new ArgumentOutOfRangeException(nameof(field));
        long index = Layout switch
        {
            ActorDataLayout.Aos => checked((long)row * Fields + field),
            ActorDataLayout.Soa => checked((long)field * Rows + row),
            ActorDataLayout.Aosoa => checked(
                (((long)(row / Lanes) * Fields + field) * Lanes) +
                (row % Lanes)),
            _ => throw new InvalidOperationException("unverified layout")
        };
        int offset = checked((int)checked(index * sizeof(double)));
        if (offset < 0 || (long)offset + sizeof(double) > ByteLength)
            throw new InvalidOperationException("verified layout exceeded native byte budget");
        return offset;
    }

    /// <summary>
    /// Host-only bridge to the existing actor-owned native slab. The arena
    /// validates the exact turn authority. This is not generated guest CIL.
    /// </summary>
    public ActorShardColumns AllocateTrustedHostShard(
        ActorArena arena, ActorArenaTurn turn)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(turn);
        return new ActorShardColumns(arena, turn, Rows, Fields, Layout, Lanes);
    }
}

public static class NativeInvertedF64LayoutVerifierV1
{
    public const int MaximumFields = 32;

    private static bool Identifier(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
        char first = name[0];
        if (!((first >= 'a' && first <= 'z') ||
              (first >= 'A' && first <= 'Z') || first == '_')) return false;
        foreach (char c in name.AsSpan(1))
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                  (c >= '0' && c <= '9') || c == '_')) return false;
        return true;
    }

    public static VerifiedNativeInvertedF64LayoutV1 Verify(
        NativeInvertedF64LayoutRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Identifier(request.ClassName))
            throw new FormatException("E_INVERTED_CLASS_NAME: bounded ASCII class identifier required");
        var names = request.FieldNames ??
            throw new FormatException("E_INVERTED_FIELDS: field schema required");
        if (names.Count is < 1 or > MaximumFields)
            throw new FormatException("E_INVERTED_FIELDS: field budget 1..32");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (!Identifier(name) || !seen.Add(name))
                throw new FormatException("E_INVERTED_FIELD_NAME: invalid or duplicated field");

        if (request.Rows <= 0)
            throw new FormatException("E_INVERTED_ROWS: positive native row bound required");
        if (request.Layout is not (ActorDataLayout.Aos or ActorDataLayout.Soa
            or ActorDataLayout.Aosoa))
            throw new FormatException("E_INVERTED_LAYOUT: unknown physical layout");
        if (request.Lanes is < 1 or > ActorShardColumns.MaxAosoaLanes ||
            (request.Lanes & (request.Lanes - 1)) != 0 ||
            (request.Layout != ActorDataLayout.Aosoa && request.Lanes != 1))
            throw new FormatException("E_INVERTED_LANES: invalid physical lane width");

        long physicalRows = request.Layout == ActorDataLayout.Aosoa
            ? checked(((long)request.Rows + request.Lanes - 1) /
                request.Lanes * request.Lanes)
            : request.Rows;
        long bytes = checked(physicalRows * names.Count * sizeof(double));
        if (bytes > ActorShardColumns.MaxShardBytes)
            throw new FormatException("E_INVERTED_BUDGET: native shard exceeds 16 MiB");

        return new VerifiedNativeInvertedF64LayoutV1(request.ClassName,
            request.Rows, Array.AsReadOnly(names.ToArray()), request.Layout,
            request.Lanes, checked((int)physicalRows), checked((int)bytes));
    }
}
