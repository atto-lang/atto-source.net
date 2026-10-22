using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Ores.Net.Runtime;

public enum ActorMixedScalarKind { U32, F64, Bool, Ref32 }
public readonly record struct ActorMixedField(string Name, ActorMixedScalarKind Kind);

/// <summary>
/// Heterogeneous actor-owned unmanaged data, not a source-compiled class.
/// A Ref32 is only an encoded unsigned value, NOT a dereference capability.
/// All actor, generation, and turn authorization is enforced by ActorArena.
/// </summary>
public sealed class ActorMixedShard
{
    public const int MaxMixedBytes = 16 * 1024 * 1024;
    private readonly record struct Locator(int Segment, long Offset, long Stride);
    private sealed class Segment
    {
        internal readonly long Bytes;
        internal readonly long Stride;
        internal readonly int Alignment;
        internal long Base;
        internal Segment(long bytes, long stride, int alignment)
        { Bytes = bytes; Stride = stride; Alignment = alignment; }
    }

    private readonly ActorArena _arena;
    private readonly ActorArenaHandle _storage;
    private readonly ActorMixedField[] _fields;
    private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);
    private readonly Locator[] _locators;
    private readonly List<Segment> _segments = new();
    private bool _released;

    public int Rows { get; }
    public ActorDataLayout Layout { get; }
    public int Lanes { get; }
    public int ByteLength { get; }
    public long PayloadBytes { get; }

    private static int Width(ActorMixedScalarKind kind) => kind switch
    {
        ActorMixedScalarKind.U32 or ActorMixedScalarKind.Ref32 => 4,
        ActorMixedScalarKind.F64 => 8,
        ActorMixedScalarKind.Bool => 1,
        _ => throw new ArgumentException("unknown mixed scalar kind")
    };
    private static long Align(long value, int alignment) =>
        checked(((value + alignment - 1) / alignment) * alignment);

    public ActorMixedShard(ActorArena arena, ActorArenaTurn turn,
        IReadOnlyList<ActorMixedField> fields, int rows, ActorDataLayout layout,
        int lanes = 1, int[][]? groups = null, int maxBytes = MaxMixedBytes)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(fields);
        if (rows <= 0 || fields.Count is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(rows));
        if (maxBytes <= 0 || maxBytes > MaxMixedBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (layout is not (ActorDataLayout.Aos or ActorDataLayout.Soa or
            ActorDataLayout.Aosoa or ActorDataLayout.Soaos))
            throw new ArgumentOutOfRangeException(nameof(layout));
        if (layout == ActorDataLayout.Aosoa)
        {
            if (lanes < 1 || lanes > 1024 || (lanes & (lanes - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(lanes));
        }
        else if (lanes != 1)
            throw new ArgumentOutOfRangeException(nameof(lanes));
        if (layout != ActorDataLayout.Soaos && groups is not null)
            throw new ArgumentException("groups valid only for SoAoS", nameof(groups));

        _fields = new ActorMixedField[fields.Count];
        _locators = new Locator[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            _ = Width(field.Kind);
            if (string.IsNullOrWhiteSpace(field.Name) ||
                !IsValidIdentifier(field.Name) || !_names.TryAdd(field.Name, i))
                throw new ArgumentException("invalid or duplicate mixed field", nameof(fields));
            _fields[i] = field;
        }

        Rows = rows; Layout = layout; Lanes = lanes;
        if (layout == ActorDataLayout.Aos)
        {
            var members = Enumerable.Range(0, _fields.Length).ToArray();
            var shape = RecordShape(members);
            _segments.Add(new Segment(checked(shape.stride * rows),
                shape.stride, shape.alignment));
            for (int i = 0; i < members.Length; i++)
                _locators[i] = new Locator(0, shape.offsets[i], shape.stride);
        }
        else if (layout == ActorDataLayout.Soa)
        {
            for (int i = 0; i < _fields.Length; i++)
            {
                int width = Width(_fields[i].Kind);
                _segments.Add(new Segment(checked((long)rows * width), width, width));
                _locators[i] = new Locator(i, 0, width);
            }
        }
        else if (layout == ActorDataLayout.Aosoa)
        {
            long cursor = 0;
            int alignment = 1;
            for (int i = 0; i < _fields.Length; i++)
            {
                int width = Width(_fields[i].Kind);
                cursor = Align(cursor, width);
                _locators[i] = new Locator(0, cursor, width);
                cursor = checked(cursor + (long)width * lanes);
                alignment = Math.Max(alignment, width);
            }
            long stride = Align(cursor, alignment);
            long tiles = checked(((long)rows + lanes - 1) / lanes);
            _segments.Add(new Segment(checked(stride * tiles), stride, alignment));
        }
        else
        {
            if (groups is null || groups.Length == 0 || groups.Length > fields.Count)
                throw new ArgumentException("nonempty SoAoS groups required", nameof(groups));
            var seen = new bool[fields.Count];
            int totalFields = 0;
            foreach (var group in groups)
            {
                if (group is null || group.Length == 0)
                    throw new ArgumentException("empty SoAoS group", nameof(groups));
                foreach (int index in group)
                {
                    if ((uint)index >= (uint)fields.Count || seen[index])
                        throw new ArgumentException("SoAoS must partition fields", nameof(groups));
                    seen[index] = true; totalFields++;
                }
                var shape = RecordShape(group);
                int segment = _segments.Count;
                _segments.Add(new Segment(checked(shape.stride * rows),
                    shape.stride, shape.alignment));
                for (int i = 0; i < group.Length; i++)
                    _locators[group[i]] = new Locator(segment, shape.offsets[i], shape.stride);
            }
            if (totalFields != fields.Count)
                throw new ArgumentException("SoAoS must partition fields", nameof(groups));
        }

        long cursorBase = 0, payloadBytes = 0;
        foreach (var seg in _segments)
        {
            cursorBase = Align(cursorBase, seg.Alignment);
            seg.Base = cursorBase;
            cursorBase = checked(cursorBase + seg.Bytes);
            payloadBytes = checked(payloadBytes + seg.Bytes);
            if (cursorBase > maxBytes)
                throw new ArgumentOutOfRangeException(nameof(maxBytes),
                    "mixed field layout exceeds actor allocation quota");
        }
        if (cursorBase == 0)
            throw new ArgumentOutOfRangeException(nameof(rows));
        PayloadBytes = payloadBytes;
        ByteLength = checked((int)cursorBase);
        _arena = arena;
        _storage = arena.AllocatePersistent(turn, ByteLength, alignment: 8);
    }

    private (long stride, int alignment, long[] offsets) RecordShape(int[] members)
    {
        long cursor = 0;
        int alignment = 1;
        var offsets = new long[members.Length];
        for (int i = 0; i < members.Length; i++)
        {
            int width = Width(_fields[members[i]].Kind);
            cursor = Align(cursor, width);
            offsets[i] = cursor;
            cursor = checked(cursor + width);
            alignment = Math.Max(alignment, width);
        }
        return (Align(cursor, alignment), alignment, offsets);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (!IsFirst(name[0])) return false;
        for (int i = 1; i < name.Length; i++)
            if (!IsFirst(name[i]) && !(name[i] >= '0' && name[i] <= '9'))
                return false;
        return true;
    }
    private static bool IsFirst(char c) =>
        c == '_' || c == '$' || c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z';

    private (int offset, ActorMixedScalarKind kind) Cell(int row, string name)
    {
        if (_released) throw new ObjectDisposedException(nameof(ActorMixedShard));
        if ((uint)row >= (uint)Rows || name is null || !_names.TryGetValue(name, out int index))
            throw new ArgumentOutOfRangeException(nameof(row), "mixed row/field invalid");
        var loc = _locators[index];
        var segment = _segments[loc.Segment];
        long inSegment = Layout == ActorDataLayout.Aosoa
            ? checked((long)(row / Lanes) * segment.Stride + loc.Offset +
                (long)(row % Lanes) * loc.Stride)
            : checked((long)row * loc.Stride + loc.Offset);
        int width = Width(_fields[index].Kind);
        if (inSegment < 0 || inSegment > segment.Bytes ||
            width > segment.Bytes - inSegment)
            throw new InvalidOperationException("mixed cell outside checked segment");
        return (checked((int)(segment.Base + inSegment)), _fields[index].Kind);
    }
    public int GetPhysicalByteOffset(int row, string field) => Cell(row, field).offset;

    public uint ReadU32(ActorArenaTurn turn, int row, string field)
    {
        var (offset, kind) = Cell(row, field);
        if (kind is not (ActorMixedScalarKind.U32 or ActorMixedScalarKind.Ref32))
            throw new InvalidOperationException("u32/ref32 required");
        Span<byte> bytes = stackalloc byte[4];
        _arena.Read(turn, _storage, offset, bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
    public void WriteU32(ActorArenaTurn turn, int row, string field, uint value)
    {
        var (offset, kind) = Cell(row, field);
        if (kind is not (ActorMixedScalarKind.U32 or ActorMixedScalarKind.Ref32))
            throw new InvalidOperationException("u32/ref32 required");
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _arena.Write(turn, _storage, offset, bytes);
    }
    public double ReadF64(ActorArenaTurn turn, int row, string field)
    {
        var (offset, kind) = Cell(row, field);
        if (kind != ActorMixedScalarKind.F64)
            throw new InvalidOperationException("f64 required");
        Span<byte> bytes = stackalloc byte[8];
        _arena.Read(turn, _storage, offset, bytes);
        return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes));
    }
    public void WriteF64(ActorArenaTurn turn, int row, string field, double value)
    {
        var (offset, kind) = Cell(row, field);
        if (kind != ActorMixedScalarKind.F64)
            throw new InvalidOperationException("f64 required");
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        _arena.Write(turn, _storage, offset, bytes);
    }
    public bool ReadBool(ActorArenaTurn turn, int row, string field)
    {
        var (offset, kind) = Cell(row, field);
        if (kind != ActorMixedScalarKind.Bool)
            throw new InvalidOperationException("bool required");
        Span<byte> bytes = stackalloc byte[1];
        _arena.Read(turn, _storage, offset, bytes);
        return bytes[0] != 0;
    }
    public void WriteBool(ActorArenaTurn turn, int row, string field, bool value)
    {
        var (offset, kind) = Cell(row, field);
        if (kind != ActorMixedScalarKind.Bool)
            throw new InvalidOperationException("bool required");
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value ? (byte)1 : (byte)0;
        _arena.Write(turn, _storage, offset, bytes);
    }
    public void Integrate(ActorArenaTurn turn, string position, string velocity, double dt)
    {
        if (position == velocity || !double.IsFinite(dt) ||
            !_names.TryGetValue(position, out int p) ||
            !_names.TryGetValue(velocity, out int v) ||
            _fields[p].Kind != ActorMixedScalarKind.F64 ||
            _fields[v].Kind != ActorMixedScalarKind.F64)
            throw new ArgumentException("integrate requires distinct f64 fields and finite dt");
        for (int i = 0; i < Rows; i++)
            WriteF64(turn, i, position, ReadF64(turn, i, position) +
                ReadF64(turn, i, velocity) * dt);
    }
    /// <summary>
    /// Acquire a whole-shard immutable read loan. It can outlive its current
    /// scheduler turn, but carries no permission to dereference native bytes.
    /// The caller must reenter the owning actor to read. Writes and release
    /// are blocked until every outstanding loan is disposed.
    ///
    /// This is a conservative allocation-wide fence, NOT yet field-sensitive
    /// ECS borrowing or source-level rt borrow lowering.
    /// </summary>
    public ActorArenaReadLoan BorrowRead(ActorArenaTurn turn)
    {
        if (_released)
            throw new ObjectDisposedException(nameof(ActorMixedShard));
        return _arena.BorrowRead(turn, _storage);
    }

    public void Release(ActorArenaTurn turn)
    {
        if (_released) return;
        _arena.ReleasePersistent(turn, _storage);
        _released = true;
    }
}
