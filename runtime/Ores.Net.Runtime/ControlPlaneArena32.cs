using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>
/// Bounded native-memory CONTROL-plane prototype; independent of ActorArena.
/// Its payload and slot metadata are unmanaged. Four-byte ControlRef32 words
/// inhabit payload fields; host-only 64-bit witnesses bind an issuing domain.
/// ActorRuntime may opt in to a native root summary for actor counts and
/// completed messages; managed scheduling queues/contexts and compiled
/// Atto guest object-graph lowering are NOT implemented by this class.
/// </summary>
public sealed unsafe class ControlPlaneArena32 : IDisposable
{
    // The low 20 bits are slot + 1; the high 12 bits are generation.
    // This control-plane ABI is distinct from actor generation16_slot16 and
    // from LLVM's unsigned offset8_plus_one actor reference representation.
    public const string Abi = "control_generation12_slot20_v1";
    public const int RecordBytes = 32;
    // sizeof(Slot) == 12; allocate metadata rounded up for aligned_alloc's 8-byte granularity.
    private const int SlotBytes = 12;
    private const int NativeAlignment = 8;
    public const int SlotBits = 20;
    public const int GenerationBits = 12;
    private const uint SlotMask = (1u << SlotBits) - 1;
    private const uint MaxGeneration = (1u << GenerationBits) - 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        internal uint Generation;
        internal uint Live;
        internal uint FreeNext;
    }

    private static long s_nextIssuer;
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly uint _issuer;
    private byte* _records;
    private Slot* _slots;
    private int _nextSlot;
    private uint _freeHead;
    private bool _closed;

    public static long RequiredNativeBytes(int capacity)
    {
        if (capacity <= 0 || capacity > SlotMask)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        long records = checked((long)capacity * RecordBytes);
        long slots = checked((long)capacity * SlotBytes);
        long paddedSlots = checked((slots + NativeAlignment - 1) & ~(long)(NativeAlignment - 1));
        return checked(records + paddedSlots);
    }

    // All backing is unmanaged; this reports actual reserved bytes, including alignment.
    public long ReservedBytes
    {
        get
        {
            lock (_gate)
                return _closed ? 0 : RequiredNativeBytes(_capacity);
        }
    }

    public ControlPlaneArena32(int capacity)
    {
        if (capacity <= 0 || capacity > SlotMask)
            throw new ArgumentOutOfRangeException(nameof(capacity), "invalid bounded control Ref32 quota");
        long issuer = Interlocked.Increment(ref s_nextIssuer);
        if (issuer <= 0 || issuer > uint.MaxValue)
            throw new InvalidOperationException("control issuer IDs exhausted: no wrap or reuse");
        _issuer = checked((uint)issuer);
        _capacity = capacity;
        if (sizeof(Slot) != SlotBytes)
            throw new InvalidOperationException("control slot ABI layout drift");
        // Native allocations are paired with explicit Dispose, not the CLR GC.
        _records = (byte*)NativeMemory.AlignedAlloc(checked((nuint)((long)capacity * RecordBytes)), NativeAlignment);
        if (_records == null) throw new OutOfMemoryException("control records");
        try
        {
            long metadata = checked((long)capacity * SlotBytes);
            long paddedMetadata = checked((metadata + NativeAlignment - 1) & ~(long)(NativeAlignment - 1));
            _slots = (Slot*)NativeMemory.AlignedAlloc(checked((nuint)paddedMetadata), NativeAlignment);
            if (_slots == null) throw new OutOfMemoryException("control metadata");
            new Span<byte>(_records, checked(capacity * RecordBytes)).Clear();
            new Span<byte>((byte*)_slots, checked(capacity * sizeof(Slot))).Clear();
            for (int i = 0; i < capacity; i++) _slots[i].Generation = 1;
        }
        catch
        {
            if (_slots != null) NativeMemory.AlignedFree(_slots);
            NativeMemory.AlignedFree(_records);
            _slots = null;
            _records = null;
            throw;
        }
    }

    private ulong Witness(uint word) => ((ulong)_issuer << 32) | word;
    private void RequireOpen()
    {
        if (_closed) throw new InvalidOperationException("control heap closed");
    }
    private int Validate(ulong handle)
    {
        RequireOpen();
        if ((uint)(handle >> 32) != _issuer)
            throw new InvalidOperationException("foreign control/actor domain witness");
        uint word = (uint)handle;
        uint low = word & SlotMask;
        uint generation = word >> (int)SlotBits;
        if (low == 0 || generation == 0 || low > (uint)_capacity)
            throw new InvalidOperationException("null/forged control handle");
        int slot = (int)low - 1;
        if (_slots[slot].Live == 0 || _slots[slot].Generation != generation)
            throw new InvalidOperationException("stale control handle");
        return slot;
    }

    public ulong Allocate()
    {
        lock (_gate)
        {
            RequireOpen();
            int slot;
            if (_freeHead != 0)
            {
                slot = (int)_freeHead - 1;
                _freeHead = _slots[slot].FreeNext;
            }
            else
            {
                if (_nextSlot >= _capacity)
                    throw new InvalidOperationException("control heap quota exhausted");
                slot = _nextSlot++;
            }
            if (_slots[slot].Live != 0 || _slots[slot].Generation == 0)
                throw new InvalidOperationException("corrupt or retired control slot");
            _slots[slot].Live = 1;
            _slots[slot].FreeNext = 0;
            uint word = (_slots[slot].Generation << (int)SlotBits) | ((uint)slot + 1);
            return Witness(word);
        }
    }

    public void Drop(ulong handle)
    {
        lock (_gate)
        {
            int slot = Validate(handle);
            new Span<byte>(_records + slot * RecordBytes, RecordBytes).Clear();
            _slots[slot].Live = 0;
            if (_slots[slot].Generation == MaxGeneration)
                _slots[slot].Generation = 0; // retire instead of wrapping to an old identity
            else
            {
                _slots[slot].Generation++;
                _slots[slot].FreeNext = _freeHead;
                _freeHead = (uint)slot + 1;
            }
        }
    }

    public void WriteI64(ulong handle, int field, long value)
    {
        lock (_gate)
        {
            int slot = Validate(handle);
            if (field != 0 && field != 8)
                throw new ArgumentOutOfRangeException(nameof(field), "typed i64 fields are 0 and 8");
            BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(_records + slot * RecordBytes + field, 8), value);
        }
    }

    // Concurrent actor carrier completions can publish out of order.
    // A monotone native i64 summary prevents an older completion from
    // overwriting a newer count; the caller owns the atomic increment.
    public void WriteMaxI64(ulong handle, int field, long value)
    {
        lock (_gate)
        {
            int slot = Validate(handle);
            if (field != 8 || value < 0)
                throw new ArgumentOutOfRangeException(nameof(field),
                    "monotone control completion count requires field 8 and nonnegative value");
            var bytes = new Span<byte>(_records + slot * RecordBytes + field, 8);
            long current = BinaryPrimitives.ReadInt64LittleEndian(bytes);
            if (value > current)
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        }
    }

    public long ReadI64(ulong handle, int field)
    {
        lock (_gate)
        {
            int slot = Validate(handle);
            if (field != 0 && field != 8)
                throw new ArgumentOutOfRangeException(nameof(field), "typed i64 fields are 0 and 8");
            return BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(_records + slot * RecordBytes + field, 8));
        }
    }

    public void WriteRef32(ulong parent, ulong child)
    {
        lock (_gate)
        {
            int slot = Validate(parent);
            if (child != 0) Validate(child);
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(_records + slot * RecordBytes + 16, 4), (uint)child);
        }
    }

    public ulong ReadRef32(ulong parent)
    {
        lock (_gate)
        {
            int slot = Validate(parent);
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_records + slot * RecordBytes + 16, 4));
            if (word == 0) return 0;
            ulong child = Witness(word);
            Validate(child);
            return child;
        }
    }

    public int Capacity => _capacity;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            new Span<byte>(_records, checked(_capacity * RecordBytes)).Clear();
            new Span<byte>((byte*)_slots, checked(_capacity * sizeof(Slot))).Clear();
            NativeMemory.AlignedFree(_records);
            NativeMemory.AlignedFree(_slots);
            _records = null;
            _slots = null;
        }
        // This is deterministic explicit disposal; no finalizer.
        GC.SuppressFinalize(this);
    }
}
