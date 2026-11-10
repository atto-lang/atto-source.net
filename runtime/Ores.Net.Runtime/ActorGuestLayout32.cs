namespace Ores.Net.Runtime;

/// <summary>
/// A static, bounded actor-native guest object layout. Type IDs are local to
/// the compiler-produced module/type registry, NOT network or host capabilities.
/// The .NET source compiler does not yet produce these descriptors; callers
/// using the host API remain trusted, and compiled guest evidence is pending.
/// </summary>
public enum ActorGuestFieldKind32 : byte
{
    I64 = 1,
    Ref32 = 2
}

/// <param name="Offset">Byte offset of a field in a native guest object.</param>
/// <param name="Kind">Primitive i64 data or a compressed local object reference.</param>
/// <param name="TargetTypeId">For Ref32, a required nonzero nominal target type.</param>
public readonly record struct ActorGuestField32(
    int Offset, ActorGuestFieldKind32 Kind, uint TargetTypeId = 0);

/// <summary>
/// Immutable, validated field layout for one nominal guest object type.
/// Only the scalar and reference widths needed for the first compiler
/// object-graph milestone are supported. No class inheritance, dynamic
/// dispatch, nullable encoding beyond null=0, or variable-length arrays.
/// </summary>
public sealed class ActorGuestLayout32
{
    public const int MaxFields = 64;
    private readonly ActorGuestField32[] _fields;

    public uint TypeId { get; }
    public int SizeBytes { get; }

    public ActorGuestLayout32(uint typeId, int sizeBytes,
        IReadOnlyList<ActorGuestField32> fields)
    {
        if (typeId == 0)
            throw new ArgumentOutOfRangeException(nameof(typeId));
        if (sizeBytes <= 0 || sizeBytes > ActorArena.MaxArenaBytes ||
            (sizeBytes & 7) != 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes),
                "Guest object size must be a positive 8-byte multiple within arena limits");
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0 || fields.Count > MaxFields)
            throw new ArgumentOutOfRangeException(nameof(fields),
                "Guest object field count must be between 1 and 64");

        var copy = new ActorGuestField32[fields.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = fields[i];
        Array.Sort(copy, static (left, right) =>
            left.Offset.CompareTo(right.Offset));

        int end = 0;
        foreach (var field in copy)
        {
            int width = field.Kind switch
            {
                ActorGuestFieldKind32.I64 => 8,
                ActorGuestFieldKind32.Ref32 => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(fields),
                    "Unsupported native guest field kind")
            };
            if (field.Offset < end || field.Offset < 0 ||
                (field.Offset & (width - 1)) != 0 ||
                field.Offset > sizeBytes - width)
                throw new ArgumentException(
                    "Guest fields overlap, are unaligned, or exceed object bounds",
                    nameof(fields));
            if (field.Kind == ActorGuestFieldKind32.Ref32
                ? field.TargetTypeId == 0 : field.TargetTypeId != 0)
                throw new ArgumentException(
                    "Nominal target type is required only for Ref32 fields",
                    nameof(fields));
            end = checked(field.Offset + width);
        }
        TypeId = typeId;
        SizeBytes = sizeBytes;
        _fields = copy;
    }

    /// <summary>
    /// One nominal type identifier must have one immutable shape for the
    /// actor lifetime, including after every instance has been dropped.
    /// </summary>
    internal bool SameShape(ActorGuestLayout32 other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (TypeId != other.TypeId || SizeBytes != other.SizeBytes ||
            _fields.Length != other._fields.Length)
            return false;
        for (int i = 0; i < _fields.Length; i++)
            if (_fields[i] != other._fields[i])
                return false;
        return true;
    }

    /// <summary>
    /// Typed scalar access is atomic at the ABI boundary: only the exact,
    /// aligned eight-byte I64 field may be read or written. Partial reads or
    /// writes could otherwise tear a compiler-owned scalar into ill-typed
    /// object state. Untyped native buffers retain their byte-granular API.
    /// Reference fields can only use checked Ref32 methods.
    /// </summary>
    internal bool AllowsScalarAccess(int offset, int length)
    {
        if (length != sizeof(long) || offset < 0 ||
            offset > SizeBytes - sizeof(long))
            return false;
        foreach (var field in _fields)
            if (field.Kind == ActorGuestFieldKind32.I64 &&
                offset == field.Offset)
                return true;
        return false;
    }

    internal uint RequiredReferenceTarget(int offset)
    {
        foreach (var field in _fields)
            if (field.Kind == ActorGuestFieldKind32.Ref32 &&
                field.Offset == offset)
                return field.TargetTypeId;
        throw new InvalidOperationException(
            "Guest reference must target an exact typed Ref32 field");
    }
}
