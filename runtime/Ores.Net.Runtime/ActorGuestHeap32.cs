using System.Buffers.Binary;

namespace Ores.Net.Runtime;

/// <summary>
/// Actor-local 32-bit guest object references backed by the existing
/// ActorArena persistent native slab. This is an indirection table, NOT a
/// second allocator and NOT a separate CLR heap.
/// 
/// Layout: high 16 bits = nonzero generation; low 16 bits = slot + 1.
/// Zero is null. Slot metadata resolves to the arena's offset-backed,
/// generation-fenced persistent allocation. Retire a slot instead of wrapping
/// its 16-bit generation, so a stale reference can never become live again.
/// 
/// The current actor turn supplies the implicit owner authority. These words
/// are *not* globally authenticated capabilities: never serialize guest refs,
/// pass them between actors, expose them to untrusted FFI, or accept raw
/// mailbox words as references. Compiler source lowering must enforce that
/// boundary and typed reference writes before guest allocation is certified.
/// </summary>
internal sealed class ActorGuestHeap32
{
    internal const uint Null = 0;
    internal const int MaxSlots = ushort.MaxValue;
    private const uint SlotMask = ushort.MaxValue;
    private const int SlotBits = 16;

    private readonly object _gate = new();
    private readonly ActorArena _arena;
    private readonly ActorArenaNativeAllocation[] _handles;
    private readonly ActorGuestLayout32?[] _layouts;
    private readonly ushort[] _generations;
    private readonly int[] _freeSlots;
    private int _nextSlot;
    private int _freeCount;
    private int _liveCount;

    internal ActorGuestHeap32(ActorArena arena, int slots)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (slots <= 0 || slots > MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(slots));
        _arena = arena;
        _handles = new ActorArenaNativeAllocation[slots];
        _layouts = new ActorGuestLayout32?[slots];
        _generations = new ushort[slots];
        _freeSlots = new int[slots];
    }

    internal int LiveCount
    {
        get { lock (_gate) return _liveCount; }
    }

    internal uint Allocate(ActorArenaTurn turn, int bytes, int alignment = 8)
    {
        lock (_gate)
        {
            // Turn checks precede even metadata-only operations.
            _arena.ValidateActiveTurn(turn);
            if (_freeCount == 0 && _nextSlot == _handles.Length)
                throw new InvalidOperationException("Guest reference slots exhausted");

            int slot = _freeCount != 0
                ? _freeSlots[_freeCount - 1] : _nextSlot;
            ushort generation = _generations[slot] == 0
                ? (ushort)1 : _generations[slot];

            // Allocation is failure-neutral for both the existing arena and
            // the guest reference table: commit the slot only after success.
            ActorArenaNativeAllocation handle = _arena.AllocateNativeGuest(turn, bytes, alignment);
            if (_freeCount != 0)
                _freeCount--;
            else
                _nextSlot++;
            _handles[slot] = handle;
            _layouts[slot] = null;
            _generations[slot] = generation;
            _liveCount++;
            return ((uint)generation << SlotBits) | (uint)(slot + 1);
        }
    }

    /// <summary>Allocate a statically described guest object with nominal type identity.</summary>
    internal uint AllocateTyped(ActorArenaTurn turn, ActorGuestLayout32 layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        lock (_gate)
        {
            // The native allocation is committed before publishing the type;
            // layout already validated all field bounds and alignment.
            uint reference = Allocate(turn, layout.SizeBytes, 8);
            _layouts[checked((int)(reference & SlotMask) - 1)] = layout;
            return reference;
        }
    }

    internal void Drop(ActorArenaTurn turn, uint reference)
    {
        lock (_gate)
        {
            _arena.ValidatePhysicalTurn(turn);
            int slot = ValidateSlot(reference);
            // This can reject on an active read loan or expired turn; leave
            // reference metadata intact until the native drop really commits.
            _arena.DropNativeGuest(turn, _handles[slot]);
            _handles[slot] = default;
            _layouts[slot] = null;
            _liveCount--;
            if (_generations[slot] == ushort.MaxValue)
            {
                // Slot is permanently exhausted; never wrap back to 1.
                _generations[slot] = 0;
            }
            else
            {
                _generations[slot]++;
                _freeSlots[_freeCount++] = slot;
            }
        }
    }

    internal void Read(ActorArenaTurn turn, uint reference, int offset,
        Span<byte> destination)
    {
        lock (_gate)
        {
            _arena.ValidatePhysicalTurn(turn);
            int slot = ValidateSlot(reference);
            if (_layouts[slot] is { } typed &&
                !typed.AllowsScalarAccess(offset, destination.Length))
                throw new InvalidOperationException(
                    "Typed guest raw reads may only access declared scalar fields");
            _arena.ReadNativeGuest(turn, _handles[slot], offset, destination);
        }
    }

    internal void Write(ActorArenaTurn turn, uint reference, int offset,
        ReadOnlySpan<byte> source)
    {
        lock (_gate)
        {
            _arena.ValidatePhysicalTurn(turn);
            int slot = ValidateSlot(reference);
            if (_layouts[slot] is { } typed &&
                !typed.AllowsScalarAccess(offset, source.Length))
                throw new InvalidOperationException(
                    "Typed guest raw writes may only access declared scalar fields");
            _arena.WriteNativeGuest(turn, _handles[slot], offset, source);
        }
    }

    // Guest read loans use the same underlying arena loan accounting and may
    // survive a physical turn, but never grant out-of-turn byte access.
    internal ActorArenaReadLoan BorrowRead(ActorArenaTurn turn, uint reference)
    {
        lock (_gate)
        {
            _arena.ValidatePhysicalTurn(turn);
            return _arena.BorrowNativeGuestRead(turn, _handles[ValidateSlot(reference)]);
        }
    }

    /// <summary>Store one local reference in canonical little-endian form.</summary>
    internal void WriteReference(ActorArenaTurn turn, uint owner, int offset,
        uint target)
    {
        lock (_gate)
        {
            _arena.ValidatePhysicalTurn(turn);
            int slot = ValidateSlot(owner);
            uint requiredType = _layouts[slot]?.RequiredReferenceTarget(offset) ?? 0;
            if (target != Null)
            {
                int targetSlot = ValidateSlot(target);
                if (requiredType != 0 && _layouts[targetSlot]?.TypeId != requiredType)
                    throw new InvalidOperationException(
                        "Guest reference target nominal type mismatch");
            }
            Span<byte> encoded = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(encoded, target);
            _arena.WriteNativeGuest(turn, _handles[slot], offset, encoded);
        }
    }

    /// <summary>Reject stale or unallocated non-null references on load.</summary>
    internal uint ReadReference(ActorArenaTurn turn, uint owner, int offset)
    {
        lock (_gate)
        {
            _arena.ValidatePhysicalTurn(turn);
            int slot = ValidateSlot(owner);
            // Validate the typed field before touching native bytes.
            uint requiredType = _layouts[slot]?.RequiredReferenceTarget(offset) ?? 0;
            Span<byte> encoded = stackalloc byte[sizeof(uint)];
            _arena.ReadNativeGuest(turn, _handles[slot], offset, encoded);
            uint reference = BinaryPrimitives.ReadUInt32LittleEndian(encoded);
            if (reference != Null)
            {
                int targetSlot = ValidateSlot(reference);
                if (requiredType != 0 && _layouts[targetSlot]?.TypeId != requiredType)
                    throw new InvalidOperationException(
                        "Guest reference target nominal type mismatch");
            }
            return reference;
        }
    }

    private int ValidateSlot(uint reference)
    {
        uint encodedSlot = reference & SlotMask;
        uint generation = reference >> SlotBits;
        if (encodedSlot == 0 || generation == 0 ||
            encodedSlot > (uint)_nextSlot)
            throw new InvalidOperationException("Null, forged or stale guest reference");

        int slot = checked((int)encodedSlot - 1);
        if (_handles[slot].Generation == 0 || _generations[slot] != generation)
            throw new InvalidOperationException("Foreign, dropped or stale guest reference");
        return slot;
    }
}
