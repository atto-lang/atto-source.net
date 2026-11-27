using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>
/// Bounded native-memory CONTROL-plane prototype; independent of ActorArena.
/// Its payload and slot metadata are unmanaged. Four-byte ControlRef32 words
/// inhabit payload fields; host-only 64-bit witnesses bind an issuing domain.
/// No language object-graph compiler or scheduler integration is claimed.
/// Numeric witnesses are trusted-host values, not unforgeable guest capabilities.
/// </summary>
public sealed unsafe class ControlPlaneArena32 : IDisposable
{
    // The low 20 bits are slot + 1; the high 12 bits are generation.
    // This control-plane ABI is distinct from actor generation16_slot16 and
    // from LLVM's unsigned offset8_plus_one actor reference representation.
    public const string Abi = "control_generation12_slot20_v1";
    public const int RecordBytes = 32;
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
    private readonly int _metadataBytes;
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly uint _issuer;
    private byte* _records;
    private Slot* _slots;
    private uint _freeHead, _freeTail;
    private bool _closed;

    public ControlPlaneArena32(int capacity)
    {
        if (capacity <= 0 || capacity > SlotMask)
            throw new ArgumentOutOfRangeException(nameof(capacity), "invalid bounded control Ref32 quota");
        _issuer = IssueDomain();
        _capacity = capacity;
        _metadataBytes = checked((capacity * sizeof(Slot) + 7) & ~7);
        // Native allocations are paired with explicit Dispose, not the CLR GC.
        _records = (byte*)NativeMemory.AlignedAlloc(checked((nuint)(capacity * RecordBytes)), 8);
        if (_records == null) throw new OutOfMemoryException("control records");
        try
        {
            _slots = (Slot*)NativeMemory.AlignedAlloc(checked((nuint)_metadataBytes), 8);
            if (_slots == null) throw new OutOfMemoryException("control metadata");
            new Span<byte>(_records, checked(capacity * RecordBytes)).Clear();
            new Span<byte>((byte*)_slots, _metadataBytes).Clear();
            for (int i = 0; i < capacity; i++)
            {
                _slots[i].Generation = 1;
                _slots[i].FreeNext = i + 1 < capacity ? (uint)i + 2 : 0;
            }
            _freeHead = 1;
            _freeTail = (uint)capacity;
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

    private static uint IssueDomain()
    {
        while (true)
        {
            long before = Volatile.Read(ref s_nextIssuer);
            if (before >= uint.MaxValue) throw new InvalidOperationException("control issuer IDs exhausted");
            if (Interlocked.CompareExchange(ref s_nextIssuer, before + 1, before) == before)
                return checked((uint)(before + 1));
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
        if (TryAllocate(out ulong handle)) return handle;
        throw new InvalidOperationException("control heap closed or quota exhausted");
    }

    /// <summary>Nonthrowing capacity/closed denial; no fallback or host allocation.</summary>
    public bool TryAllocate(out ulong handle)
    {
        lock (_gate)
        {
            handle = 0;
            if (_closed) return false;
            if (_freeHead == 0) return false;
            int slot = (int)_freeHead - 1;
            _freeHead = _slots[slot].FreeNext;
            if (_freeHead == 0) _freeTail = 0;
            if (_slots[slot].Live != 0 || _slots[slot].Generation == 0)
                throw new InvalidOperationException("corrupt or retired control slot");
            _slots[slot].Live = 1;
            _slots[slot].FreeNext = 0;
            uint word = (_slots[slot].Generation << (int)SlotBits) | ((uint)slot + 1);
            handle = Witness(word);
            return true;
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
                _slots[slot].FreeNext = 0;
                if (_freeTail == 0) _freeHead = (uint)slot + 1;
                else _slots[(int)_freeTail - 1].FreeNext = (uint)slot + 1;
                _freeTail = (uint)slot + 1;
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
            new Span<byte>((byte*)_slots, _metadataBytes).Clear();
            NativeMemory.AlignedFree(_records);
            NativeMemory.AlignedFree(_slots);
            _records = null;
            _slots = null;
        }
        // This is deterministic explicit disposal; no finalizer.
        GC.SuppressFinalize(this);
    }
}
