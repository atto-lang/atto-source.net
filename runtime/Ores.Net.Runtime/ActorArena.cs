using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Ores.Net.Runtime;

/// <summary>
/// Host-only deterministic unmanaged bytes per actor incarnation (including SHARED).
/// NOT source .ores allocation lowering and NOT an inter-actor shared-memory region.
/// A turn authorizes access only on its admitted physical carrier.
/// </summary>
public sealed unsafe class ActorArena : IDisposable
{
    public const int SlotCount = 64;
    public const int SlotBytes = 256;
    public const int ScratchBytes = 16 * 1024;
    private const int TotalBytes = SlotCount * SlotBytes + ScratchBytes;

    public sealed class Turn
    {
        internal Turn() { }
    }
    public sealed class Handle
    {
        internal readonly Guid Domain;
        internal readonly int Slot;
        internal readonly ulong Generation;
        internal Handle(Guid domain, int slot, ulong generation) =>
            (Domain, Slot, Generation) = (domain, slot, generation);
    }
    public sealed class Scratch
    {
        internal readonly Guid Domain;
        internal readonly Turn Turn;
        internal readonly int Offset, Length;
        internal Scratch(Guid domain, Turn turn, int offset, int length) =>
            (Domain, Turn, Offset, Length) = (domain, turn, offset, length);
    }

    /// <summary>
    /// Opaque lifetime-only authority for a registered continuation/FFI/I/O
    /// callback. It grants no arena read/write access and may be released from
    /// any carrier. Logical retirement revokes all arena operations
    /// immediately, while physical native release waits for the last lease.
    /// </summary>
    public sealed class ExternalLease : IDisposable
    {
        private ActorArena? _owner;
        private readonly object _releaseGate = new();
        internal ExternalLease(ActorArena owner) => _owner = owner;

        public void Dispose()
        {
            // Do not consume the final lifetime authority until native
            // reclaim succeeds. A failed close must be retryable by the
            // same callback without underflow or premature unmapping.
            lock (_releaseGate)
            {
                if (_owner is null) return;
                _owner.ReleaseExternalLease();
                _owner = null;
            }
        }
    }

    private struct Slot
    {
        public ulong Generation;
        public int Length;
        public bool Live;
    }

    private readonly ulong _actor, _incarnation;
    private readonly Guid _domain = Guid.NewGuid();
    private readonly object _gate = new();
    private readonly Slot[] _slots = new Slot[SlotCount];
    // Exact issuance metadata: in-bounds offsets are not themselves authority.
    // Fixed-size ushort entries avoid unbounded per-allocation bookkeeping.
    private readonly ushort[] _scratchLengths = new ushort[ScratchBytes];
    private byte* _memory;
    private Turn? _turn;
    private System.Threading.Thread? _carrier;
    private int _scratchTop, _externalLeases;
    private bool _retired;

    public ActorArena(ulong actor, ulong incarnation)
    {
        if (actor == 0 || incarnation == 0) throw new ArgumentOutOfRangeException(nameof(actor));
        _actor = actor;
        _incarnation = incarnation;
        _memory = (byte*)NativeMemory.AllocZeroed((nuint)TotalBytes);
        if (_memory == null) throw new OutOfMemoryException("actor arena allocation failed");
        for (var i = 0; i < _slots.Length; ++i) _slots[i].Generation = 1;
    }

    public Turn BeginTurn(ulong actor, ulong incarnation)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            if (_retired || _turn is not null || actor != _actor || incarnation != _incarnation)
                throw new InvalidOperationException("actor arena admission denied");
            // Managed thread IDs are process-scoped integers and may be reused
            // after a thread lifetime. Hold the actual Thread object while a
            // turn is active so a later carrier cannot inherit old authority.
            var fresh = new Turn();
            _carrier = System.Threading.Thread.CurrentThread;
            _turn = fresh;
            return fresh;
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    private void RequireTurn(Turn turn)
    {
        if (_retired || _turn is null || !ReferenceEquals(_turn, turn) ||
            !ReferenceEquals(_carrier, System.Threading.Thread.CurrentThread))
            throw new InvalidOperationException("wrong or stale actor turn");
    }

    public void EndTurn(Turn turn)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            RequireTurn(turn);
            CryptographicOperations.ZeroMemory(new Span<byte>(_memory + SlotCount * SlotBytes, _scratchTop));
            Array.Clear(_scratchLengths);
            _scratchTop = 0;
            _turn = null;
            _carrier = null;
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    public Handle Allocate(Turn turn, int length)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            RequireTurn(turn);
            if (length <= 0 || length > SlotBytes) throw new ArgumentOutOfRangeException(nameof(length));
            for (var i = 0; i < SlotCount; ++i)
            {
                if (_slots[i].Live || _slots[i].Generation == 0) continue;
                // Allocate the capability first. Handle allocation failure
                // cannot strand an otherwise-live unmanaged slot.
                var handle = new Handle(_domain, i, _slots[i].Generation);
                CryptographicOperations.ZeroMemory(new Span<byte>(_memory + i * SlotBytes, SlotBytes));
                _slots[i].Length = length;
                _slots[i].Live = true;
                return handle;
            }
            throw new InvalidOperationException("actor arena slot quota exhausted");
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    private int ValidSlot(Turn turn, Handle handle)
    {
        RequireTurn(turn);
        if (handle.Domain != _domain || handle.Slot < 0 || handle.Slot >= SlotCount)
            throw new InvalidOperationException("foreign actor arena handle");
        ref var slot = ref _slots[handle.Slot];
        if (!slot.Live || slot.Generation != handle.Generation)
            throw new InvalidOperationException("stale actor arena handle");
        return slot.Length;
    }

    public byte[] Read(Turn turn, Handle handle)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            var length = ValidSlot(turn, handle);
            return new ReadOnlySpan<byte>(_memory + handle.Slot * SlotBytes, length).ToArray();
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    public void Write(Turn turn, Handle handle, ReadOnlySpan<byte> value)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            var length = ValidSlot(turn, handle);
            if (value.Length != length) throw new ArgumentException("actor arena write length");
            value.CopyTo(new Span<byte>(_memory + handle.Slot * SlotBytes, length));
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    public void Drop(Turn turn, Handle handle)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            ValidSlot(turn, handle);
            ref var slot = ref _slots[handle.Slot];
            CryptographicOperations.ZeroMemory(new Span<byte>(_memory + handle.Slot * SlotBytes, SlotBytes));
            slot.Live = false;
            slot.Length = 0;
            slot.Generation = slot.Generation == ulong.MaxValue ? 0 : slot.Generation + 1;
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    /// <summary>
    /// Register a lifetime pin for an external continuation, FFI operation or
    /// I/O callback before the actor turn exits. The lease carries lifetime
    /// authority only; it cannot dereference actor memory.
    /// </summary>
    public ExternalLease AcquireExternalLease(Turn turn)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            RequireTurn(turn);
            if (_externalLeases == int.MaxValue)
                throw new InvalidOperationException("actor arena external lease quota exhausted");
            var lease = new ExternalLease(this);
            ++_externalLeases;
            return lease;
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    /// <summary>
    /// Acquire the lifetime pin synchronously while the actor turn is valid,
    /// then keep it until the registered asynchronous operation settles.
    /// The returned task never grants arena memory authority.
    /// </summary>
    public System.Threading.Tasks.Task TrackExternalCompletion(
        Turn turn, System.Threading.Tasks.Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var lease = AcquireExternalLease(turn);
        return AwaitExternalCompletion(task, lease);

        static async System.Threading.Tasks.Task AwaitExternalCompletion(
            System.Threading.Tasks.Task pending, ExternalLease lifetime)
        {
            using (lifetime)
                await pending.ConfigureAwait(false);
        }
    }

    public System.Threading.Tasks.Task<T> TrackExternalCompletion<T>(
        Turn turn, System.Threading.Tasks.Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var lease = AcquireExternalLease(turn);
        return AwaitExternalCompletion(task, lease);

        static async System.Threading.Tasks.Task<T> AwaitExternalCompletion(
            System.Threading.Tasks.Task<T> pending, ExternalLease lifetime)
        {
            using (lifetime)
                return await pending.ConfigureAwait(false);
        }
    }

    private void ReleaseExternalLease()
    {
        lock (_gate)
        {
            if (_externalLeases <= 0)
                throw new InvalidOperationException("actor arena external lease accounting underflow");
            // Complete the physical release before consuming the final
            // lease. A failed scrub/release must leave this callback's
            // lifetime authority retryable, not leak retired capacity.
            if (_retired && _externalLeases == 1)
                FreeNativeMemory();
            --_externalLeases;
        }
    }

    public int OutstandingExternalLeases
    {
        get { lock (_gate) return _externalLeases; }
    }

    public bool Retired
    {
        get { lock (_gate) return _retired; }
    }

    public bool PhysicallyDisposed
    {
        get { lock (_gate) return _memory == null; }
    }

    public Scratch AllocateScratch(Turn turn, int length)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            RequireTurn(turn);
            if (length <= 0 || length > ScratchBytes - _scratchTop)
                throw new ArgumentOutOfRangeException(nameof(length), "scratch quota");
            var handle = new Scratch(_domain, turn, _scratchTop, length);
            _scratchLengths[_scratchTop] = checked((ushort)length);
            _scratchTop += length;
            return handle;
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    private void RequireScratch(Turn turn, Scratch handle)
    {
        RequireTurn(turn);
        if (handle.Domain != _domain || !ReferenceEquals(handle.Turn, turn) ||
            handle.Offset < 0 || handle.Offset >= ScratchBytes ||
            handle.Offset >= _scratchTop || handle.Length <= 0 ||
            handle.Length > _scratchTop - handle.Offset ||
            _scratchLengths[handle.Offset] != handle.Length)
            throw new InvalidOperationException("foreign or stale scratch handle");
    }

    public byte[] ReadScratch(Turn turn, Scratch handle)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            RequireScratch(turn, handle);
            return new ReadOnlySpan<byte>(
                _memory + SlotCount * SlotBytes + handle.Offset, handle.Length).ToArray();
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    public void WriteScratch(Turn turn, Scratch handle, ReadOnlySpan<byte> value)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            RequireScratch(turn, handle);
            if (value.Length != handle.Length) throw new ArgumentException("scratch write length");
            value.CopyTo(new Span<byte>(
                _memory + SlotCount * SlotBytes + handle.Offset, handle.Length));
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    private void FreeNativeMemory()
    {
        if (_memory == null) return;
        CryptographicOperations.ZeroMemory(new Span<byte>(_memory, TotalBytes));
        NativeMemory.Free(_memory);
        _memory = null;
    }

    public void Dispose()
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            if (_retired) return;
            if (_turn is not null) throw new InvalidOperationException("active turn blocks retirement");

            // Retirement is two-phase: revoke new actor operations now, then
            // reclaim only after all continuation/FFI/I/O lifetime pins quiesce.
            _retired = true;
            _carrier = null;
            if (_externalLeases == 0)
                FreeNativeMemory();
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }
}
