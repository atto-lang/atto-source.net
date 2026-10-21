using System.Buffers.Binary;

namespace Ores.Net.Runtime;

public enum ActorDataLayout
{
    Aos,
    Soa,
    Aosoa,
    Soaos
}

/// <summary>
/// Actor-owned dense homogeneous scalar shard backed by ActorArena unmanaged
/// storage. Exactly one physical layout is authoritative at a time: AoS, SoA,
/// tiled AoSoA, or grouped SoAoS. This is the .NET host substrate for future inverted-class,
/// ECS, and data-oriented lowering; it is not yet source-language lowering.
/// </summary>
public sealed class ActorShardColumns
{
    public const int MaxShardBytes = 16 * 1024 * 1024;
    public const int MaxAosoaLanes = 1024;

    private readonly ActorArena _arena;
    private readonly ActorArenaHandle _storage;
    private readonly long[]? _soaosGroupBaseScalars;
    private readonly int[]? _soaosFieldOffsets;
    private readonly int[]? _soaosFieldGroupWidths;
    private bool _released;

    public ActorShardColumns(ActorArena arena, ActorArenaTurn turn,
        int rows, int fields, ActorDataLayout layout, int lanes = 1,
        int[]? soaosGroupWidths = null)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(turn);
        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows));
        if (fields <= 0)
            throw new ArgumentOutOfRangeException(nameof(fields));

        if (layout == ActorDataLayout.Aosoa)
        {
            if (lanes <= 0 || lanes > MaxAosoaLanes ||
                (lanes & (lanes - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(lanes),
                    "AoSoA lanes must be a power of two from 1 through 1024");
        }
        else if (lanes != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lanes),
                "AoS, SoA, and SoAoS use a lane width of one");
        }

        if (layout == ActorDataLayout.Soaos)
        {
            if (soaosGroupWidths is null || soaosGroupWidths.Length == 0)
                throw new ArgumentException(
                    "SoAoS requires one or more nonempty field groups",
                    nameof(soaosGroupWidths));

            long groupedFields = 0;
            foreach (int width in soaosGroupWidths)
            {
                if (width <= 0)
                    throw new ArgumentOutOfRangeException(nameof(soaosGroupWidths),
                        "SoAoS group widths must be positive");
                groupedFields = checked(groupedFields + width);
            }
            if (groupedFields != fields)
                throw new ArgumentException(
                    "SoAoS group widths must exactly partition every field",
                    nameof(soaosGroupWidths));
        }
        else if (soaosGroupWidths is not null)
        {
            throw new ArgumentException(
                "SoAoS group metadata is only valid for the SoAoS layout",
                nameof(soaosGroupWidths));
        }

        Rows = rows;
        Fields = fields;
        Layout = layout;
        Lanes = lanes;
        PhysicalRows = layout == ActorDataLayout.Aosoa
            ? RoundRows(rows, lanes)
            : rows;

        if (layout == ActorDataLayout.Soaos)
        {
            _soaosGroupBaseScalars = new long[fields];
            _soaosFieldOffsets = new int[fields];
            _soaosFieldGroupWidths = new int[fields];

            int field = 0;
            long groupBaseScalar = 0;
            foreach (int width in soaosGroupWidths!)
            {
                for (int withinGroup = 0; withinGroup < width; withinGroup++)
                {
                    _soaosGroupBaseScalars[field] = groupBaseScalar;
                    _soaosFieldOffsets[field] = withinGroup;
                    _soaosFieldGroupWidths[field] = width;
                    field++;
                }
                groupBaseScalar = checked(groupBaseScalar + checked((long)rows * width));
            }
            GroupCount = soaosGroupWidths!.Length;
        }

        long scalars = checked((long)PhysicalRows * fields);
        long byteLength = checked(scalars * sizeof(double));
        if (byteLength > MaxShardBytes)
            throw new ArgumentOutOfRangeException(nameof(rows),
                "Actor shard exceeds the 16 MiB per-shard limit");

        ByteLength = checked((int)byteLength);
        _arena = arena;
        _storage = arena.AllocatePersistent(turn, ByteLength, alignment: 8);
    }

    public int Rows { get; }
    public int Fields { get; }
    public int PhysicalRows { get; }
    public int Lanes { get; }
    public int ByteLength { get; }
    public ActorDataLayout Layout { get; }
    public int GroupCount { get; }

    public double Read(ActorArenaTurn turn, int row, int field)
    {
        EnsureLive();
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        _arena.Read(turn, _storage, ByteOffset(row, field), bytes);
        long bits = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        return BitConverter.Int64BitsToDouble(bits);
    }

    public void Write(ActorArenaTurn turn, int row, int field, double value)
    {
        EnsureLive();
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes, BitConverter.DoubleToInt64Bits(value));
        _arena.Write(turn, _storage, ByteOffset(row, field), bytes);
    }

    /// <summary>
    /// Dense in-actor kernel with no per-row actor/Future allocation. This is a
    /// reference host kernel, not a claim of SIMD/vectorized code generation.
    /// </summary>
    public void Integrate(ActorArenaTurn turn, int positionField,
        int velocityField, double dt)
    {
        EnsureLive();
        if (positionField < 0 || positionField >= Fields ||
            velocityField < 0 || velocityField >= Fields ||
            positionField == velocityField)
            throw new ArgumentOutOfRangeException(nameof(positionField),
                "Integrate requires two distinct valid fields");
        if (!double.IsFinite(dt))
            throw new ArgumentOutOfRangeException(nameof(dt));

        for (int row = 0; row < Rows; row++)
        {
            double position = Read(turn, row, positionField);
            double velocity = Read(turn, row, velocityField);
            Write(turn, row, positionField, position + velocity * dt);
        }
    }

    /// <summary>
    /// Allocation-free copy into caller-owned storage. Detached snapshots with
    /// untracked lifetime are intentionally not provided by this API.
    /// </summary>
    public void CopyColumnInto(ActorArenaTurn turn, int field,
        Span<double> destination)
    {
        EnsureLive();
        if (field < 0 || field >= Fields)
            throw new ArgumentOutOfRangeException(nameof(field));
        if (destination.Length < Rows)
            throw new ArgumentException("Destination is smaller than the shard column",
                nameof(destination));

        for (int row = 0; row < Rows; row++)
            destination[row] = Read(turn, row, field);
    }

    /// <summary>
    /// Return a checked byte offset relative to this shard allocation. This is
    /// a host/compiler geometry hook, not a native pointer or guest capability.
    /// </summary>
    public int GetPhysicalByteOffset(int row, int field)
    {
        EnsureLive();
        return ByteOffset(row, field);
    }

    /// <summary>
    /// Deterministically release this shard while its owning actor turn is
    /// admitted. The underlying bytes are zeroed before the slot is reusable.
    /// </summary>
    public void Release(ActorArenaTurn turn)
    {
        if (_released)
            return;
        _arena.ReleasePersistent(turn, _storage);
        _released = true;
    }

    private int ByteOffset(int row, int field)
    {
        if ((uint)row >= (uint)Rows)
            throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)field >= (uint)Fields)
            throw new ArgumentOutOfRangeException(nameof(field));

        long scalar = Layout switch
        {
            ActorDataLayout.Aos => checked((long)row * Fields + field),
            ActorDataLayout.Soa => checked((long)field * Rows + row),
            ActorDataLayout.Aosoa => checked(
                (((long)(row / Lanes) * Fields + field) * Lanes) +
                (row % Lanes)),
            ActorDataLayout.Soaos => checked(
                _soaosGroupBaseScalars![field] +
                checked((long)row * _soaosFieldGroupWidths![field]) +
                _soaosFieldOffsets![field]),
            _ => throw new InvalidOperationException("Unknown actor shard layout")
        };

        return checked((int)(scalar * sizeof(double)));
    }

    private void EnsureLive()
    {
        if (_released)
            throw new ObjectDisposedException(nameof(ActorShardColumns));
    }

    private static int RoundRows(int rows, int lanes)
    {
        int remainder = rows & (lanes - 1);
        return remainder == 0 ? rows : checked(rows + lanes - remainder);
    }
}
