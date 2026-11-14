using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>
/// Actor-owned unmanaged byte arena with primitive generational handles.
///
/// Guest payload bytes live outside the CLR managed heap. The arena object and
/// fixed metadata arrays are managed once at actor construction; successful
/// steady-state allocate/read/write/drop operations do not allocate managed
/// payload or handle objects. Dispose deterministically zeroes and frees the
/// unmanaged region.
///
/// The compiler/borrow checker should lower lexical owners through
/// <see cref="CleanupScope"/> so every non-transferred allocation is dropped on
/// scope exit. Actor retirement must occur only after async, pin and FFI
/// quiescence.
/// </summary>
public sealed unsafe class ActorNativeArena : IDisposable
{
    public const ulong InvalidHandle = 0;
    public const int MaximumCapacityBytes = 64 * 1024 * 1024;
    private const int SlotBits = 16;
    private const ulong SlotMask = (1UL << SlotBits) - 1;
    private const long MaxSequence = (1L << (64 - SlotBits)) - 1;
    private const int MaxSlots = ushort.MaxValue;

    private static long s_nextSequence = 1;

    // Serializes byte access, metadata updates and native reclamation.
    private readonly object _gate = new();
    private readonly int[] _offsets;
    private readonly int[] _lengths;
    private readonly ulong[] _tokens;
    private readonly bool[] _live;
    private readonly int[] _freeSlots;
    private byte* _memory;
    private readonly int _capacity;
    private int _backingBytes;
    private readonly ActorArenaBudget _budget;
    private int _cursor;
    private int _nextSlot;
    private int _freeCount;
    private int _liveCount;
    private bool _disposed;

    public ActorNativeArena(int bytes, int slots, ActorArenaBudget? budget = null)
    {
        if (bytes <= 0 || bytes > MaximumCapacityBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes),
                "Actor-native arena capacity must be between 1 and 64 MiB");
        if (slots <= 0 || slots > MaxSlots) throw new ArgumentOutOfRangeException(nameof(slots));

        // Allocate managed control metadata first. If one of these allocations
        // fails, no unmanaged block exists yet and therefore nothing can leak.
        _offsets = new int[slots];
        _lengths = new int[slots];
        _tokens = new ulong[slots];
        _live = new bool[slots];
        _freeSlots = new int[slots];
        // A maximum is a quota, not an eager native allocation. Actor storage
        // is materialized only by the first successful object allocation.
        _capacity = bytes;
        _budget = budget ?? ActorArenaBudget.Process;
    }

    public int CapacityBytes => _capacity;
    // Physically allocated native backing; unlike the logical quota, this starts at zero.
    public int BackingBytes { get { lock (_gate) return _backingBytes; } }
    public int UsedBytes { get { lock (_gate) return _cursor; } }
    public int LiveAllocations { get { lock (_gate) return _liveCount; } }
    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    public ulong Allocate(int bytes, int alignment = 8)
    {
        lock (_gate)
        {
        if (_disposed || bytes <= 0 || alignment <= 0 || (alignment & (alignment - 1)) != 0
            || alignment > 64 || (_freeCount == 0 && _nextSlot >= _live.Length))
            return InvalidHandle;

        // Every backing uses 64-byte alignment, so logical byte offsets do
        // not depend on a process-width native base address. A relocated
        // backing retains all slot offsets and 32-bit actor-local locations.
        int mask = alignment - 1;
        if (_cursor > int.MaxValue - mask) return InvalidHandle;
        int aligned = (_cursor + mask) & ~mask;
        if (aligned > _capacity || bytes > _capacity - aligned) return InvalidHandle;

        ulong sequence = NextSequence();
        if (sequence == 0) return InvalidHandle;
        EnsureBacking(aligned + bytes);

        int slot = _freeCount == 0 ? _nextSlot++ : _freeSlots[--_freeCount];
        ulong token = (sequence << SlotBits) | (uint)(slot + 1);
        _offsets[slot] = aligned;
        _lengths[slot] = bytes;
        _tokens[slot] = token;
        _live[slot] = true;
        _cursor = aligned + bytes;
        _liveCount++;
        return token;
    
        }
    }

    public bool Drop(ulong handle)
    {
        lock (_gate)
        {
        int slot = ValidSlot(handle);
        if (slot < 0) return false;

        // Rewind a just-dropped bump tail even when an earlier actor root
        // remains live. Bytes are scrubbed before reducing the cursor.
        int droppedOffset = _offsets[slot];
        int droppedLength = _lengths[slot];
        bool wasTail = droppedOffset + droppedLength == _cursor;
        Zero(droppedOffset, droppedLength);
        _live[slot] = false;
        _tokens[slot] = 0;
        _offsets[slot] = 0;
        _lengths[slot] = 0;
        _freeSlots[_freeCount++] = slot;
        _liveCount--;

        if (wasTail && _liveCount != 0)
        {
            // Scrubbed earlier-freed holes at the end also become reusable
            // once the newest live allocation disappears. Bounded O(slots)
            // scan, no host heap allocation and no relocation of live bytes.
            int highestLiveEnd = 0;
            for (int i = 0; i < _nextSlot; i++)
            {
                if (_live[i])
                    highestLiveEnd = Math.Max(highestLiveEnd,
                        checked(_offsets[i] + _lengths[i]));
            }
            _cursor = highestLiveEnd;
        }

        if (_liveCount == 0)
        {
            Zero(0, _cursor);
            _cursor = 0;
        }
        return true;
    
        }
    }

    public bool WriteInt64(ulong handle, int relativeOffset, long value)
    {
        lock (_gate)
        {
        int slot = ValidSlot(handle);
        if (slot < 0 || relativeOffset < 0 || relativeOffset > _lengths[slot] - sizeof(long))
            return false;
        Unsafe.WriteUnaligned<long>(_memory + _offsets[slot] + relativeOffset, value);
        return true;
    
        }
    }

    public long ReadInt64(ulong handle, int relativeOffset = 0)
    {
        lock (_gate)
        {
        int slot = ValidSlot(handle);
        if (slot < 0 || relativeOffset < 0 || relativeOffset > _lengths[slot] - sizeof(long))
            throw new InvalidOperationException("invalid, stale or out-of-bounds actor arena handle");
        return Unsafe.ReadUnaligned<long>(_memory + _offsets[slot] + relativeOffset);
    
        }
    }

    public bool WriteByte(ulong handle, int relativeOffset, byte value)
    {
        lock (_gate)
        {
        int slot = ValidSlot(handle);
        if (slot < 0 || relativeOffset < 0 || relativeOffset >= _lengths[slot])
            return false;
        _memory[_offsets[slot] + relativeOffset] = value;
        return true;
    
        }
    }

    public byte ReadByte(ulong handle, int relativeOffset)
    {
        lock (_gate)
        {
        int slot = ValidSlot(handle);
        if (slot < 0 || relativeOffset < 0 || relativeOffset >= _lengths[slot])
            throw new InvalidOperationException("invalid, stale or out-of-bounds actor arena handle");
        return _memory[_offsets[slot] + relativeOffset];
    
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
        if (_disposed) return;
        if (_memory != null)
        {
            Zero(0, _backingBytes);
            NativeMemory.AlignedFree(_memory);
            _memory = null;
            // Refund only after physical scrubbing and release, not logical drop.
            if (!ReferenceEquals(_budget, ActorArenaBudget.Process))
                ActorArenaBudget.Process.Refund(_backingBytes);
            _budget.Refund(_backingBytes);
            _backingBytes = 0;
        }
        Array.Clear(_live);
        Array.Clear(_tokens);
        Array.Clear(_offsets);
        Array.Clear(_lengths);
        _cursor = 0;
        _nextSlot = 0;
        _freeCount = 0;
        _liveCount = 0;
        _disposed = true;
    
        }
    }

    // Called only while _gate is held. No raw pointers or native-backed spans
    // escape the arena, so a larger aligned block may replace the old one.
    // The quota is still 64 MiB maximum; only consumed capacity is allocated.
    private void EnsureBacking(int required)
    {
        if (required <= _backingBytes) return;
        int next = _backingBytes == 0 ? Math.Min(_capacity, 4096) : _backingBytes;
        while (next < required)
            next = next > _capacity / 2 ? _capacity : next * 2;

        // C aligned_alloc requires a multiple of the requested alignment.
        // Physical tail padding cannot increase the actor's logical quota.
        int physicalBytes = checked((int)((next + 63L) & ~63L));
        int additionalBytes = physicalBytes - _backingBytes;
        // Admission is charged for physical backing before any native call.
        // A custom tenant limit cannot bypass the global process limit.
        _budget.Reserve(additionalBytes);
        bool processReserved = false;
        byte* replacement = null;
        try
        {
            if (!ReferenceEquals(_budget, ActorArenaBudget.Process))
            {
                ActorArenaBudget.Process.Reserve(additionalBytes);
                processReserved = true;
            }
            replacement = (byte*)NativeMemory.AlignedAlloc((nuint)physicalBytes, (nuint)64);
            if (replacement == null)
                throw new OutOfMemoryException("actor native arena growth failed");

            // An uninitialized/reused object must never observe stale host data.
            new Span<byte>(replacement, physicalBytes).Clear();
            if (_memory != null)
            {
                Buffer.MemoryCopy(_memory, replacement, physicalBytes, _cursor);
                Zero(0, _backingBytes);
                NativeMemory.AlignedFree(_memory);
            }
            _memory = replacement;
            _backingBytes = physicalBytes;
        }
        catch
        {
            if (replacement != null)
            {
                CryptographicOperations.ZeroMemory(new Span<byte>(replacement, physicalBytes));
                NativeMemory.AlignedFree(replacement);
            }
            if (processReserved)
                ActorArenaBudget.Process.Refund(additionalBytes);
            _budget.Refund(additionalBytes);
            throw;
        }
    }

    private int ValidSlot(ulong handle)
    {
        if (_disposed || handle == InvalidHandle) return -1;
        int encoded = (int)(handle & SlotMask);
        if (encoded <= 0) return -1;
        int slot = encoded - 1;
        return slot < _nextSlot && _live[slot] && _tokens[slot] == handle ? slot : -1;
    }

    private void Zero(int start, int length)
    {
        if (_memory == null || length <= 0) return;
        // Do not allow reclamation scrubbing to be optimized away before
        // NativeMemory.Free or reused object storage is returned to an actor.
        CryptographicOperations.ZeroMemory(new Span<byte>(_memory + start, length));
    }

    private static ulong NextSequence()
    {
        while (true)
        {
            long current = Volatile.Read(ref s_nextSequence);
            if (current <= 0 || current > MaxSequence) return 0;
            if (Interlocked.CompareExchange(ref s_nextSequence, current + 1, current) == current)
                return (ulong)current;
        }
    }

    /// <summary>
    /// Stack-only cleanup scope. The compiler supplies stackalloc storage, so
    /// tracking lexical owners does not allocate a managed collection.
    /// </summary>
    public ref struct CleanupScope
    {
        private ActorNativeArena _arena;
        private Span<ulong> _handles;
        private int _count;
        private bool _disposed;
        private bool _cleanupOk;

        public CleanupScope(ActorNativeArena arena, Span<ulong> trackingStorage)
        {
            ArgumentNullException.ThrowIfNull(arena);
            if (trackingStorage.IsEmpty)
                throw new ArgumentException("positive cleanup tracking capacity required",
                    nameof(trackingStorage));
            _arena = arena;
            _handles = trackingStorage;
            _handles.Clear();
            _count = 0;
            _disposed = false;
            _cleanupOk = true;
        }

        public readonly int Tracked => _count;
        public readonly bool CleanupOk => _cleanupOk;

        public ulong Allocate(int bytes, int alignment = 8)
        {
            if (_disposed || _count == _handles.Length) return InvalidHandle;
            ulong handle = _arena.Allocate(bytes, alignment);
            if (handle == InvalidHandle) return InvalidHandle;
            _handles[_count++] = handle;
            return handle;
        }

        public bool Release(ulong handle)
        {
            if (_disposed) return false;
            for (int i = 0; i < _count; i++)
            {
                if (_handles[i] != handle) continue;
                _handles.Slice(i + 1, _count - i - 1).CopyTo(_handles.Slice(i));
                _handles[--_count] = InvalidHandle;
                return true;
            }
            return false;
        }

        public bool Drop(ulong handle)
        {
            if (_disposed) return false;
            for (int i = 0; i < _count; i++)
            {
                if (_handles[i] != handle) continue;
                if (!_arena.Drop(handle)) return false;
                _handles.Slice(i + 1, _count - i - 1).CopyTo(_handles.Slice(i));
                _handles[--_count] = InvalidHandle;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            while (_count > 0)
            {
                ulong handle = _handles[--_count];
                _handles[_count] = InvalidHandle;
                if (!_arena.Drop(handle)) _cleanupOk = false;
            }
        }
    }
}
