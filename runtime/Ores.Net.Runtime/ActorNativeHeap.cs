using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>A local, non-transferable location in exactly one actor heap incarnation.</summary>
/// <remarks>The handle is not an owning pointer, a wire address, or authorization by itself.</remarks>
[JsonConverter(typeof(ActorHeapHandleNoWireConverter))]
public readonly struct ActorHeapHandle
{
    internal Guid Heap { get; }
    internal int Slot { get; }
    internal ulong Generation { get; }

    internal ActorHeapHandle(Guid heap, int slot, ulong generation)
    {
        Heap = heap;
        Slot = slot;
        Generation = generation;
    }

    public override string ToString() => "ActorHeapHandle(local, non-transferable)";
}

/// <summary>Do not accidentally publish actor-local memory addresses or generations on the wire.</summary>
public sealed class ActorHeapHandleNoWireConverter : JsonConverter<ActorHeapHandle>
{
    public override ActorHeapHandle Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException(
        "Actor heap handles cannot be deserialized from JSON");

    public override void Write(Utf8JsonWriter writer, ActorHeapHandle value,
        JsonSerializerOptions options) => throw new NotSupportedException(
        "Actor heap handles cannot be serialized as JSON");
}

/// <summary>
/// Bounded actor-owned unmanaged storage with deterministic byte reclamation.
/// Fixed managed metadata is allocated once at construction, not per object.
/// Not a CLR GC heap, OS isolation boundary, or compiler borrow checker.
///
/// The actor scheduler must hold a lifetime lease for every suspended task,
/// native callback, continuation, and FFI operation that can retain a heap
/// location. Retirement seals admissions first and frees only after leases
/// drain. In-place reads and writes are synchronized with retirement; no
/// raw pointer, Memory, or Span into actor memory escapes this API.
/// </summary>
public sealed unsafe class ActorNativeHeap : IDisposable
{
    public const int MaximumCapacity = 64 * 1024 * 1024;
    public const int MaximumSlots = 65_536;

    private struct Slot
    {
        internal int Offset;
        internal int Length;
        internal ulong Generation;
        internal bool Live;
    }

    private struct Hole
    {
        internal int Offset;
        internal int Length;

        internal Hole(int offset, int length)
        {
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>
    /// A reference-type, idempotent lease intentionally costs one managed
    /// allocation per suspended operation, NOT per allocation/read/write.
    /// It cannot be accidentally double-released by copying a value struct.
    /// </summary>
    public sealed class LifetimeLease : IDisposable
    {
        private ActorNativeHeap? _heap;

        internal LifetimeLease(ActorNativeHeap heap) => _heap = heap;

        public void Dispose()
        {
            Interlocked.Exchange(ref _heap, null)?.ReleaseLease(this);
        }
    }

    private readonly object _gate = new();
    private readonly ActorRef _owner;
    private readonly Guid _identity = Guid.NewGuid();
    private readonly Slot[] _slots;
    private readonly int[] _freeSlots;
    private readonly Hole[] _holes;
    // Only the exact lease objects minted by this heap may release native life.
    private readonly HashSet<LifetimeLease> _issuedLeases =
        new(ReferenceEqualityComparer.Instance);
    private readonly int _capacity;
    private byte* _memory;
    private int _nextSlot;
    private int _freeCount;
    private int _holeCount;
    private int _used;
    private int _live;
    private int _leases;
    private bool _closing;

    public ActorNativeHeap(ActorRef owner, int capacity, int maxSlots)
    {
        if (owner.Id == 0 || owner.Generation == 0 || owner.Nonce == Guid.Empty)
            throw new ArgumentException("A live actor capability is required", nameof(owner));
        if (capacity <= 0 || capacity > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxSlots <= 0 || maxSlots > MaximumSlots)
            throw new ArgumentOutOfRangeException(nameof(maxSlots));
        _owner = owner;
        _capacity = capacity;
        _slots = new Slot[maxSlots];
        _freeSlots = new int[maxSlots];
        _holes = new Hole[maxSlots + 1];
        for (int i = 0; i < maxSlots; ++i) _slots[i].Generation = 1;

        _memory = (byte*)NativeMemory.AllocZeroed((nuint)capacity);
        if (_memory == null) throw new OutOfMemoryException("actor-native heap allocation failed");
    }

    public int Capacity => _capacity;

    public int LiveAllocations
    {
        get { lock (_gate) return _live; }
    }

    public bool Retired
    {
        get { lock (_gate) return _memory == null; }
    }

    private void RequireOwner(ActorRef actor)
    {
        if (actor != _owner)
            throw new InvalidOperationException("foreign or stale actor heap capability");
    }

    private static int Padding(nuint address, int alignment)
    {
        nuint mask = (nuint)(alignment - 1);
        return (int)(((nuint)alignment - (address & mask)) & mask);
    }

    private bool Valid(ActorHeapHandle handle)
    {
        return _memory != null && handle.Heap == _identity
            && (uint)handle.Slot < (uint)_nextSlot
            && _slots[handle.Slot].Live
            && _slots[handle.Slot].Generation == handle.Generation;
    }

    /// <summary>Admission for a possible cross-turn/async/FFI reference.</summary>
    public LifetimeLease? AcquireLifetimeLease(ActorRef actor)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (_closing) return null;
            // Allocate the managed lease before changing admission accounting.
            if (_leases == MaximumSlots)
                return null;
            var lease = new LifetimeLease(this);
            if (!_issuedLeases.Add(lease))
                throw new InvalidOperationException("duplicate heap lease issuance");
            checked { ++_leases; }
            return lease;
        }
    }

    private void ReleaseLease(LifetimeLease lease)
    {
        lock (_gate)
        {
            if (_leases <= 0 || !_issuedLeases.Remove(lease))
                throw new InvalidOperationException("unissued or stale actor heap lease");
            --_leases;
            if (_closing && _leases == 0) ReleaseNative();
        }
    }

    /// <summary>
    /// Return null if byte/slot quota is exhausted. Freed ranges are reused
    /// without moving live objects. Alignment applies to the physical address.
    /// </summary>
    public ActorHeapHandle? Allocate(ActorRef actor, int length, int alignment = 8)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (_closing || length <= 0 || length > _capacity ||
                alignment <= 0 || alignment > 64 || (alignment & (alignment - 1)) != 0 ||
                (_freeCount == 0 && _nextSlot == _slots.Length))
                return null;

            int holeIndex = -1;
            int offset = -1;
            for (int i = 0; i < _holeCount; ++i)
            {
                Hole hole = _holes[i];
                int padding = Padding((nuint)(_memory + hole.Offset), alignment);
                if (padding <= hole.Length && length <= hole.Length - padding)
                {
                    holeIndex = i;
                    offset = hole.Offset + padding;
                    break;
                }
            }
            if (holeIndex >= 0)
            {
                Hole hole = _holes[holeIndex];
                int prefix = offset - hole.Offset;
                int suffix = hole.Length - prefix - length;
                if (prefix > 0 && suffix > 0)
                {
                    Array.Copy(_holes, holeIndex + 1, _holes, holeIndex + 2,
                        _holeCount - holeIndex - 1);
                    _holes[holeIndex] = new Hole(hole.Offset, prefix);
                    _holes[holeIndex + 1] = new Hole(offset + length, suffix);
                    ++_holeCount;
                }
                else if (prefix > 0) _holes[holeIndex].Length = prefix;
                else if (suffix > 0) _holes[holeIndex] = new Hole(offset + length, suffix);
                else
                {
                    Array.Copy(_holes, holeIndex + 1, _holes, holeIndex,
                        _holeCount - holeIndex - 1);
                    --_holeCount;
                }
            }
            else
            {
                int padding = Padding((nuint)(_memory + _used), alignment);
                if (padding > _capacity - _used ||
                    length > _capacity - _used - padding) return null;
                offset = _used + padding;
                _used = offset + length;
            }

            int slotIndex = _freeCount > 0 ? _freeSlots[--_freeCount] : _nextSlot++;
            ref Slot slot = ref _slots[slotIndex];
            slot.Offset = offset;
            slot.Length = length;
            slot.Live = true;
            ++_live;
            return new ActorHeapHandle(_identity, slotIndex, slot.Generation);
        }
    }

    public bool Write(ActorRef actor, ActorHeapHandle handle, ReadOnlySpan<byte> bytes,
        int offset = 0)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (_closing || !Valid(handle)) return false;
            Slot slot = _slots[handle.Slot];
            if (offset < 0 || offset > slot.Length ||
                bytes.Length > slot.Length - offset) return false;
            bytes.CopyTo(new Span<byte>(_memory + slot.Offset + offset, bytes.Length));
            return true;
        }
    }

    public bool Read(ActorRef actor, ActorHeapHandle handle, Span<byte> destination,
        int offset = 0)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (_closing || !Valid(handle)) return false;
            Slot slot = _slots[handle.Slot];
            if (offset < 0 || offset > slot.Length ||
                destination.Length > slot.Length - offset) return false;
            new ReadOnlySpan<byte>(_memory + slot.Offset + offset, destination.Length)
                .CopyTo(destination);
            return true;
        }
    }

    public bool Remove(ActorRef actor, ActorHeapHandle handle)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (_closing || !Valid(handle)) return false;
            ref Slot slot = ref _slots[handle.Slot];
            // Exhausted generations are not recycled, preventing handle ABA.
            if (slot.Generation == ulong.MaxValue) return false;
            CryptographicOperations.ZeroMemory(new Span<byte>(_memory + slot.Offset, slot.Length));
            int offset = slot.Offset;
            int length = slot.Length;
            slot.Live = false;
            ++slot.Generation;
            _freeSlots[_freeCount++] = handle.Slot;
            --_live;
            if (_live == 0)
            {
                _used = 0;
                _holeCount = 0;
            }
            else InsertHole(offset, length);
            return true;
        }
    }

    // Sorted, coalesced free ranges; bounded by maxSlots+1 at all times.
    private void InsertHole(int offset, int length)
    {
        int i = 0;
        while (i < _holeCount && _holes[i].Offset < offset) ++i;
        bool before = i > 0 && _holes[i - 1].Offset + _holes[i - 1].Length == offset;
        bool after = i < _holeCount && offset + length == _holes[i].Offset;
        if (before && after)
        {
            _holes[i - 1].Length += length + _holes[i].Length;
            Array.Copy(_holes, i + 1, _holes, i, _holeCount - i - 1);
            --_holeCount;
        }
        else if (before) _holes[i - 1].Length += length;
        else if (after)
        {
            _holes[i].Offset = offset;
            _holes[i].Length += length;
        }
        else
        {
            Array.Copy(_holes, i, _holes, i + 1, _holeCount - i);
            _holes[i] = new Hole(offset, length);
            ++_holeCount;
        }

        // Rewind a vacant bump tail without compacting any live allocation.
        while (_holeCount > 0)
        {
            Hole tail = _holes[_holeCount - 1];
            if (tail.Offset + tail.Length != _used) break;
            _used = tail.Offset;
            --_holeCount;
        }
    }

    /// <summary>
    /// Seal new admissions and retire once all scheduler/async/FFI leases drain.
    /// False means release is deferred, not that it is safe to reuse the heap.
    /// The trusted supervisor must separately prove no unleased references.
    /// </summary>
    public bool Retire(ActorRef actor)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            _closing = true;
            if (_leases != 0) return false;
            ReleaseNative();
            return true;
        }
    }

    private void ReleaseNative()
    {
        if (_memory == null) return;
        CryptographicOperations.ZeroMemory(new Span<byte>(_memory, _capacity));
        NativeMemory.Free(_memory);
        _memory = null;
        _live = 0;
        _used = 0;
        _holeCount = 0;
        _freeCount = 0;
        _nextSlot = 0;
        _issuedLeases.Clear();
        // Native bytes are scrubbed before free; also eliminate stale metadata.
        Array.Clear(_slots);
        Array.Clear(_freeSlots);
        Array.Clear(_holes);
    }

    /// <summary>Trusted owner/supervisor cleanup; pending leases defer physical free.</summary>
    public void Dispose() => Retire(_owner);
}
