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
    public const int MaxScratchHandles = 4096;
    private const int TotalBytes = SlotCount * SlotBytes + ScratchBytes;

    public sealed class Turn
    {
        private Turn() { }
    }
    public sealed class Handle
    {
        private readonly long Domain;
        private readonly int Slot;
        private readonly ulong Generation;
        private Handle(long domain, int slot, ulong generation) =>
            (Domain, Slot, Generation) = (domain, slot, generation);
    }
    public sealed class Scratch
    {
        private readonly long Domain;
        private readonly Turn Turn;
        private readonly int Offset, Length, Issuance;
        private Scratch(long domain, Turn turn, int offset, int length, int issuance) =>
            (Domain, Turn, Offset, Length, Issuance) =
            (domain, turn, offset, length, issuance);
    }

    // Arena domains are capability provenance, not random correlation IDs.
    // Long.MaxValue is a permanent exhaustion sentinel and is never issued.
    private sealed class DomainCounter
    {
        private long Next;
        private DomainCounter(long next = 1) => Next = next;
    }

    private static readonly DomainCounter s_domainCounter = new();

    private static long ClaimDomain(DomainCounter counter, ulong actor, ulong incarnation)
    {
        ArgumentNullException.ThrowIfNull(counter);
        // Invalid owner authority must never consume a non-recyclable domain.
        if (actor == 0 || incarnation == 0)
            throw new ArgumentOutOfRangeException(nameof(actor), "invalid actor arena identity");
        var candidate = Volatile.Read(ref counter.Next);
        while (candidate > 0 && candidate < long.MaxValue)
        {
            if (Interlocked.CompareExchange(ref counter.Next, candidate + 1, candidate) == candidate)
                return candidate;
            candidate = Volatile.Read(ref counter.Next);
        }
        throw new InvalidOperationException("actor arena domain exhausted");
    }
    private struct Slot
    {
        public ulong Generation;
        public int Length;
        public bool Live;
    }

    private readonly ulong _actor, _incarnation;
    private readonly long _domain;
    private readonly object _gate = new();
    private readonly Slot[] _slots = new Slot[SlotCount];
    // Capability object identity is part of authority. Preallocated tables keep
    // this bounded and avoid per-turn maps or unbounded GC bookkeeping.
    private readonly Handle?[] _issuedHandles = new Handle?[SlotCount];
    private readonly Scratch?[] _scratchIssued = new Scratch?[MaxScratchHandles];
    // Exact issuance metadata: in-bounds offsets are not themselves authority.
    private readonly ushort[] _scratchLengths = new ushort[ScratchBytes];
    private byte* _memory;
    private Turn? _turn;
    private int _carrier, _scratchTop, _scratchCount;
    private bool _retired;

    public ActorArena(ulong actor, ulong incarnation)
    {
        if (actor == 0 || incarnation == 0) throw new ArgumentOutOfRangeException(nameof(actor));
        _actor = actor;
        _incarnation = incarnation;
        _domain = ClaimDomain(s_domainCounter, actor, incarnation);
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
            _carrier = Environment.CurrentManagedThreadId;
            _turn = new Turn();
            return _turn;
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    private void RequireTurn(Turn turn)
    {
        if (_retired || _turn is null || !ReferenceEquals(_turn, turn) ||
            _carrier != Environment.CurrentManagedThreadId)
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
            Array.Clear(_scratchIssued, 0, _scratchCount);
            _scratchTop = 0;
            _scratchCount = 0;
            _turn = null;
            _carrier = 0;
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
                _slots[i].Live = true;
                _slots[i].Length = length;
                CryptographicOperations.ZeroMemory(new Span<byte>(_memory + i * SlotBytes, SlotBytes));
                var handle = new Handle(_domain, i, _slots[i].Generation);
                _issuedHandles[i] = handle;
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
        if (!ReferenceEquals(_issuedHandles[handle.Slot], handle) ||
            !slot.Live || slot.Generation != handle.Generation)
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
            _issuedHandles[handle.Slot] = null;
            slot.Live = false;
            slot.Length = 0;
            slot.Generation = slot.Generation == ulong.MaxValue ? 0 : slot.Generation + 1;
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
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
            if (_scratchCount >= MaxScratchHandles)
                throw new InvalidOperationException("scratch handle quota exhausted");
            var handle = new Scratch(_domain, turn, _scratchTop, length, _scratchCount);
            _scratchIssued[_scratchCount] = handle;
            _scratchLengths[_scratchTop] = checked((ushort)length);
            _scratchTop += length;
            _scratchCount++;
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
            handle.Issuance < 0 || handle.Issuance >= _scratchCount ||
            !ReferenceEquals(_scratchIssued[handle.Issuance], handle) ||
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

    public void Dispose()
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
            throw new InvalidOperationException("actor arena busy");
        try
        {
            if (_retired) return;
            if (_turn is not null) throw new InvalidOperationException("active turn blocks retirement");
            CryptographicOperations.ZeroMemory(new Span<byte>(_memory, TotalBytes));
            Array.Clear(_issuedHandles);
            Array.Clear(_scratchIssued);
            Array.Clear(_scratchLengths);
            NativeMemory.Free(_memory);
            _memory = null;
            _scratchTop = 0;
            _scratchCount = 0;
            _retired = true;
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }
}
