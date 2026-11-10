namespace Ores.Net.Runtime;

/// <summary>
/// Native actor-owned aggregate value kinds. Distinct tags prevent accidental
/// Entry/Pair/Tuple/Record aliasing even when layouts happen to be identical.
/// This is the i64 payload milestone, not a generic guest type system.
/// </summary>
public enum NativeAggregateKind : byte
{
    Tuple = 1,
    Record = 2,
    Entry = 3,
    Pair = 4,
    Array = 5
}

/// <summary>
/// Owned fixed-shape i64 aggregates backed exclusively by ActorNativeArena.
/// Per-object headers and payload live in unmanaged actor-private bytes, not
/// CLR ValueTuple, List, Dictionary, or object[]. Tokens are unforgeable
/// generational arena handles; foreign/stale handles fail closed.
///
/// A compiler may lower typed values to this ABI only after proving a scalar
/// i64-only payload, exact kind and record schema, and cleanup on all paths.
/// This class alone does NOT establish source compiler/CIL/NativeAOT parity.
/// </summary>
public sealed class ActorNativeAggregateStore : IDisposable
{
    private const long Magic = 0x4154544F41474731; // "ATTOAGG1"
    private const int HeaderBytes = 24;
    public const int MaximumElements = 4096;

    private readonly ActorNativeArena _arena;

    public ActorNativeAggregateStore(int capacityBytes, int slots)
        => _arena = new ActorNativeArena(capacityBytes, slots);

    public int LiveAggregates => _arena.LiveAllocations;
    public int UsedBytes => _arena.UsedBytes;

    public ulong CreateTuple(ReadOnlySpan<long> values)
        => Create(NativeAggregateKind.Tuple, 0, values);

    public ulong CreateRecord(long checkedShapeId, ReadOnlySpan<long> values)
    {
        if (checkedShapeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(checkedShapeId),
                "Record requires a positive compiler-verified nominal shape ID");
        return Create(NativeAggregateKind.Record, checkedShapeId, values);
    }

    public ulong CreateEntry(long key, long value)
    {
        Span<long> values = stackalloc long[2] { key, value };
        return Create(NativeAggregateKind.Entry, 0, values);
    }

    public ulong CreatePair(long first, long second)
    {
        Span<long> values = stackalloc long[2] { first, second };
        return Create(NativeAggregateKind.Pair, 0, values);
    }

    /// <summary>Fixed-size writable Array of scalar i64 elements only.</summary>
    public ulong CreateArray(ReadOnlySpan<long> values)
        => Create(NativeAggregateKind.Array, 0, values);

    /// <summary>
    /// Bounded allocation is transactional. On failed admission the handle is
    /// zero and no partially initialized live allocation remains.
    /// </summary>
    private ulong Create(NativeAggregateKind kind, long schema, ReadOnlySpan<long> values)
    {
        if (values.Length > MaximumElements)
            throw new ArgumentOutOfRangeException(nameof(values),
                "Native aggregate element budget exceeded");
        int size = checked(HeaderBytes + values.Length * sizeof(long));
        ulong handle = _arena.Allocate(size, alignment: 8);
        if (handle == ActorNativeArena.InvalidHandle)
            return ActorNativeArena.InvalidHandle;
        try
        {
            long header = ((long)values.Length << 8) | (byte)kind;
            if (!_arena.WriteInt64(handle, 0, Magic)
                || !_arena.WriteInt64(handle, 8, header)
                || !_arena.WriteInt64(handle, 16, schema))
                throw new InvalidOperationException("Failed to initialize native aggregate header");
            for (int i = 0; i < values.Length; i++)
            {
                if (!_arena.WriteInt64(handle, HeaderBytes + i * sizeof(long), values[i]))
                    throw new InvalidOperationException("Failed to initialize native aggregate payload");
            }
            return handle;
        }
        catch
        {
            _arena.Drop(handle); // scrubs all bytes, revokes the generation
            throw;
        }
    }

    public (NativeAggregateKind Kind, int Length, long ShapeId) Describe(ulong handle)
    {
        // Underlying ReadInt64 independently validates handle authority,
        // generation, liveness, and the native allocation bounds.
        if (_arena.ReadInt64(handle, 0) != Magic)
            throw new InvalidOperationException("Invalid native aggregate magic");
        long header = _arena.ReadInt64(handle, 8);
        int length = (int)(header >> 8);
        var kind = (NativeAggregateKind)(byte)header;
        long shape = _arena.ReadInt64(handle, 16);
        if (length < 0 || length > MaximumElements ||
            kind is not (NativeAggregateKind.Tuple or NativeAggregateKind.Record
                or NativeAggregateKind.Entry or NativeAggregateKind.Pair
                or NativeAggregateKind.Array) ||
            (kind is NativeAggregateKind.Entry or NativeAggregateKind.Pair && length != 2) ||
            (kind == NativeAggregateKind.Record ? shape <= 0 : shape != 0))
            throw new InvalidOperationException("Invalid native aggregate shape");
        return (kind, length, shape);
    }

    public long Read(ulong handle, NativeAggregateKind expectedKind,
        int index, long expectedShapeId = 0)
    {
        var (kind, count, shape) = Describe(handle);
        if (kind != expectedKind || shape != expectedShapeId)
            throw new InvalidOperationException("Aggregate kind or nominal shape mismatch");
        if ((uint)index >= (uint)count)
            throw new IndexOutOfRangeException("Native aggregate index outside checked extent");
        return _arena.ReadInt64(handle, HeaderBytes + index * sizeof(long));
    }

    /// <summary>Only the i64 Array kind is mutable; all product kinds are frozen.</summary>
    public bool TryWriteArray(ulong handle, int index, long value)
    {
        // This Try API must reject all stale/foreign/unowned handles without
        // throwing, including actor retirement racing with a mutation.
        (NativeAggregateKind Kind, int Length, long ShapeId) descriptor;
        try { descriptor = Describe(handle); }
        catch (InvalidOperationException) { return false; }
        if (descriptor.Kind != NativeAggregateKind.Array || descriptor.ShapeId != 0 ||
            (uint)index >= (uint)descriptor.Length)
            return false;
        return _arena.WriteInt64(handle, HeaderBytes + index * sizeof(long), value);
    }

    public bool Drop(ulong handle)
    {
        // Do not permit an unowned foreign/stale handle or arbitrary native
        // allocation handle to release a different allocation via this API.
        try { _ = Describe(handle); }
        catch (InvalidOperationException) { return false; }
        return _arena.Drop(handle);
    }

    public void Dispose() => _arena.Dispose();
}
