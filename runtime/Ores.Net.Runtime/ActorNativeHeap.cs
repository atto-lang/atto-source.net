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
        internal int ReadLoans;
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
            Interlocked.Exchange(ref _heap, null)?.ReleaseLease();
        }
    }

    /// <summary>
    /// Borrowed immutable slot access. One reference-type guard costs one
    /// CLR allocation per outstanding loan, not per payload read/write.
    /// Neither pointers nor memory views escape; reads copy under the heap
    /// lock. The loan is revoked on Dispose and cannot cross actor authority.
    /// </summary>
    public sealed class ReadOnlyLoan : IDisposable
    {
        private ActorNativeHeap? _heap;
        private readonly ActorHeapHandle _handle;

        internal ReadOnlyLoan(ActorNativeHeap heap, ActorHeapHandle handle)
        {
            _heap = heap;
            _handle = handle;
        }

        public bool Read(ActorRef actor, Span<byte> destination, int offset = 0)
        {
            var heap = Volatile.Read(ref _heap);
            if (heap == null) throw new ObjectDisposedException(nameof(ReadOnlyLoan));
            return heap.ReadBorrowed(actor, this, _handle, destination, offset);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _heap, null)?.ReleaseReadLoan(_handle);
        }

        internal bool BelongsTo(ActorNativeHeap heap)
            => ReferenceEquals(Volatile.Read(ref _heap), heap);
    }

    /// <summary>
    /// Stack-only deterministic ownership of one temporary native allocation.
    /// A ref struct is not a GC-allocated wrapper and cannot be boxed or
    /// captured by an ordinary heap-resident continuation. Oreslang source
    /// lowering must still prove move-only ownership and suspend legality;
    /// C# permits copying ref structs, so a copied scope fails closed on its
    /// second attempted drop rather than freeing another generation.
    /// </summary>
    public ref struct OwnedAllocation
    {
        private ActorNativeHeap? _heap;
        private readonly ActorRef _owner;
        private readonly ActorHeapHandle _handle;

        internal OwnedAllocation(ActorNativeHeap heap, ActorRef owner,
            ActorHeapHandle handle)
        {
            _heap = heap;
            _owner = owner;
            _handle = handle;
        }

        public bool IsValid => _heap is not null;

        public bool Write(ReadOnlySpan<byte> bytes, int offset = 0)
        {
            var heap = _heap ?? throw new InvalidOperationException(
                "actor scoped allocation has been dropped or was not admitted");
            return heap.Write(_owner, _handle, bytes, offset);
        }

        public bool Read(Span<byte> destination, int offset = 0)
        {
            var heap = _heap ?? throw new InvalidOperationException(
                "actor scoped allocation has been dropped or was not admitted");
            return heap.Read(_owner, _handle, destination, offset);
        }

        /// <summary>Scrub/drop on exit. Never silently abandon a live loan.</summary>
        public void Dispose()
        {
            var heap = _heap;
            if (heap is null) return;
            if (!heap.Remove(_owner, _handle))
                throw new InvalidOperationException(
                    "scoped native object cannot drop while borrowed or retiring");
            _heap = null;
        }
    }

    /// <summary>
    /// Admit one temporary value whose native memory is released by a
    /// compiler-generated using/finally scope. A default scope means
    /// bounded slot/byte admission failed.
    /// </summary>
    public OwnedAllocation AllocateScoped(ActorRef actor, int length,
        int alignment = 8)
    {
        var handle = Allocate(actor, length, alignment);
        return handle.HasValue
            ? new OwnedAllocation(this, actor, handle.Value)
            : default;
    }

    private readonly object _gate = new();
    private readonly ActorRef _owner;
    private readonly Guid _identity = Guid.NewGuid();
    private readonly Slot[] _slots;
    private readonly int[] _freeSlots;
    private readonly Hole[] _holes;
    private readonly int _capacity;
    private readonly ActorNativeMemoryBudget? _budget;
    private byte* _memory;
    private int _nextSlot;
    private int _freeCount;
    private int _holeCount;
    private int _used;
    private int _live;
    private int _leases;
    private bool _closing;

    public ActorNativeHeap(ActorRef owner, int capacity, int maxSlots,
        ActorNativeMemoryBudget? sharedBudget = null)
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

        // Prevent hundreds of actor-local quotas from exhausting the process.
        // Accounting must be reserved before issuing an unmanaged pointer.
        if (sharedBudget != null && !sharedBudget.TryReserve(capacity))
            throw new InvalidOperationException("aggregate native actor memory budget exhausted");
        try
        {
            _memory = (byte*)NativeMemory.AllocZeroed((nuint)capacity);
            if (_memory == null)
                throw new OutOfMemoryException("actor-native heap allocation failed");
            _budget = sharedBudget;
        }
        catch
        {
            sharedBudget?.Release(capacity);
            throw;
        }
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
            var lease = new LifetimeLease(this);
            checked { ++_leases; }
            return lease;
        }
    }

    /// <summary>
    /// Borrow a single live allocation immutably across a suspension.
    /// All ordinary writes/deletes to that allocation are rejected until
    /// the loan is released. New loan admissions close at retirement.
    /// </summary>
    public ReadOnlyLoan? AcquireReadOnlyLoan(ActorRef actor, ActorHeapHandle handle)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (_closing || !Valid(handle)) return null;
            ref Slot slot = ref _slots[handle.Slot];
            if (_leases == int.MaxValue || slot.ReadLoans == int.MaxValue)
                throw new OverflowException("actor heap read loan count exhausted");
            // Construct before recording the pin to avoid a phantom loan on OOM.
            var loan = new ReadOnlyLoan(this, handle);
            ++slot.ReadLoans;
            ++_leases;
            return loan;
        }
    }

    private bool ReadBorrowed(ActorRef actor, ReadOnlyLoan loan,
        ActorHeapHandle handle, Span<byte> destination, int offset)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            // Already admitted loans may finish during stop/drain, while
            // new actor turns and acquisitions are sealed.
            if (!loan.BelongsTo(this) || !Valid(handle) ||
                _slots[handle.Slot].ReadLoans <= 0) return false;
            Slot slot = _slots[handle.Slot];
            if (offset < 0 || offset > slot.Length ||
                destination.Length > slot.Length - offset) return false;
            new ReadOnlySpan<byte>(_memory + slot.Offset + offset, destination.Length)
                .CopyTo(destination);
            return true;
        }
    }

    private void ReleaseReadLoan(ActorHeapHandle handle)
    {
        lock (_gate)
        {
            if (!Valid(handle)) throw new InvalidOperationException(
                "read-only loan refers to a stale or retired native allocation");
            ref Slot slot = ref _slots[handle.Slot];
            if (slot.ReadLoans <= 0 || _leases <= 0)
                throw new InvalidOperationException("actor heap read loan underflow");
            --slot.ReadLoans;
            --_leases;
            if (_closing && _leases == 0) ReleaseNative();
        }
    }

    private void ReleaseLease()
    {
        lock (_gate)
        {
            if (_leases <= 0) throw new InvalidOperationException("actor heap lease underflow");
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
            if (slot.ReadLoans != 0) return false;
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
            if (slot.ReadLoans != 0) return false;
            // Exhausted generations are not recycled, preventing handle ABA.
            if (slot.Generation == ulong.MaxValue) return false;
            CryptographicOperations.ZeroMemory(new Span<byte>(_memory + slot.Offset, slot.Length));
            int offset = slot.Offset;
            int length = slot.Length;
            slot.Live = false;
            slot.ReadLoans = 0;
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
        // No native bytes are left pinned once the underlying allocation is
        // released. Idempotence is guaranteed by the null-pointer sentinel.
        _budget?.Release(_capacity);
        _live = 0;
        _used = 0;
        _holeCount = 0;
        _freeCount = 0;
        _nextSlot = 0;
    }

    /// <summary>Trusted owner/supervisor cleanup; pending leases defer physical free.</summary>
    public void Dispose() => Retire(_owner);
}
