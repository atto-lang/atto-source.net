using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ores.Net.Runtime;

/// <summary>
/// Exact-issued, actor-local logical activation frame. A frame is not an OS/CLR
/// machine stack and cannot be used to resume a C# call stack. Only the
/// originating actor's currently admitted physical turn can access its bytes.
/// </summary>
[JsonConverter(typeof(ActorStackFrameNoWireConverter))]
public sealed class ActorStackFrameHandle
{
    internal ActorStackFrameHandle(ActorFrameStack owner, int index,
        int offset, int length, ActorStackFrameLayout? layout)
    {
        Owner = owner;
        Index = index;
        Offset = offset;
        Length = length;
        Layout = layout;
    }

    internal ActorFrameStack Owner { get; }
    internal int Index { get; }
    internal int Offset { get; }
    public int Length { get; }
    internal ActorStackFrameLayout? Layout { get; }
}

/// <summary>
/// Canonical, immutable scalar spill-slot ABI for generated guest continuations.
/// This is a runtime layout, not a compiler assertion that an .atto actor was
/// lowered. Eight-byte little-endian slots make offsets stable across x64/arm64.
/// </summary>
public enum ActorStackSlotKind { I64, Bool }

public sealed class ActorStackFrameLayout
{
    private readonly ActorStackSlotKind[] _slots;

    public ActorStackFrameLayout(params ActorStackSlotKind[] slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Length is 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(slots),
                "Typed guest frames require between 1 and 128 scalar spill slots");
        _slots = (ActorStackSlotKind[])slots.Clone();
        foreach (var slot in _slots)
            if (slot is not (ActorStackSlotKind.I64 or ActorStackSlotKind.Bool))
                throw new ArgumentOutOfRangeException(nameof(slots),
                    "Unknown typed guest frame slot");
    }

    public int SlotCount => _slots.Length;
    public int ByteLength => checked(_slots.Length * 8);
    public ActorStackSlotKind GetKind(int slot)
    {
        if ((uint)slot >= (uint)_slots.Length)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return _slots[slot];
    }

    internal int CheckedOffset(int slot, ActorStackSlotKind kind)
    {
        if (GetKind(slot) != kind)
            throw new InvalidOperationException("Typed guest frame slot kind mismatch");
        return checked(slot * 8);
    }
}

/// <summary>
/// One-claim resume capability, issued for exactly one suspended top frame.
/// It cannot cross an actor incarnation, be claimed in the suspending physical
/// turn, or grant control of a CLR machine stack.
/// </summary>
[JsonConverter(typeof(ActorStackResumeNoWireConverter))]
public sealed class ActorStackResumeTicket
{
    internal ActorStackResumeTicket(ActorFrameStack owner,
        ActorStackFrameHandle frame, ulong suspendingEpoch)
    {
        Owner = owner;
        Frame = frame;
        SuspendingEpoch = suspendingEpoch;
    }

    internal ActorFrameStack Owner { get; }
    internal ActorStackFrameHandle Frame { get; }
    internal ulong SuspendingEpoch { get; }
}

public sealed class ActorStackResumeNoWireConverter : JsonConverter<ActorStackResumeTicket>
{
    public override ActorStackResumeTicket Read(ref Utf8JsonReader reader,
        Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "Actor stack resume authority cannot be deserialized from JSON");

    public override void Write(Utf8JsonWriter writer,
        ActorStackResumeTicket value, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "Actor stack resume authority cannot be serialized as JSON data");
}

/// <summary>
/// Never turn local actor frame authority into user JSON data or accept it
/// from the wire. Structured wire representations must be separate types.
/// </summary>
public sealed class ActorStackFrameNoWireConverter : JsonConverter<ActorStackFrameHandle>
{
    public override ActorStackFrameHandle Read(ref Utf8JsonReader reader,
        Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "Actor logical frame authority cannot be deserialized from JSON");

    public override void Write(Utf8JsonWriter writer, ActorStackFrameHandle value,
        JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "Actor logical frame authority cannot be serialized as JSON data");
}

/// <summary>
/// Bounded logical frame stack, private to one actor incarnation. Payload bytes
/// reside in its ActorArena, not in CLR frame objects or a separate allocator.
/// A suspended compiler continuation may retain opaque frame handles between
/// physical actor turns, then read them only after a new owner turn is admitted.
///
/// This is a runtime substrate for future compiler-generated async frames:
/// it does NOT switch native stacks or implement guest await/actor lowering.
/// </summary>
internal sealed class ActorFrameStack
{
    private readonly ActorArena _arena;
    private readonly int _capacity;
    private readonly ActorStackFrameHandle?[] _frames;
    private ActorArenaHandle? _backing;
    private ActorStackResumeTicket? _pendingResume;
    private int _depth;
    private int _top;

    internal ActorFrameStack(ActorArena arena, int capacity, int maxFrames)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (capacity <= 0 || capacity > arena.PersistentCapacityBytes)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxFrames <= 0 || maxFrames > 1024)
            throw new ArgumentOutOfRangeException(nameof(maxFrames));

        _arena = arena;
        _capacity = capacity;
        _frames = new ActorStackFrameHandle?[maxFrames];
    }

    internal ActorStackFrameHandle Push(ActorArenaTurn turn,
        int bytes, int alignment = 8, ActorStackFrameLayout? layout = null)
    {
        // Unlike cleanup, new frame admission is forbidden after retirement.
        _arena.ValidateActiveTurn(turn);
        if (_pendingResume is not null)
            throw new InvalidOperationException(
                "Suspended actor stack must resume or cancel before new pushes");
        if (layout is not null &&
            (bytes != layout.ByteLength || alignment != 8))
            throw new InvalidOperationException("Typed guest frame layout mismatch");
        if (bytes <= 0 || bytes > _capacity)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (alignment <= 0 || alignment > 64 ||
            (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        if (_depth == _frames.Length)
            throw new InvalidOperationException("Actor logical frame limit exhausted");

        int offset = checked((_top + alignment - 1) & ~(alignment - 1));
        if (offset > _capacity || bytes > _capacity - offset)
            throw new InvalidOperationException("Actor logical stack capacity exhausted");

        // Construct the capability before native admission. A failed allocation
        // cannot increment depth, publish a frame, or change the stack top.
        var frame = new ActorStackFrameHandle(this, _depth, offset, bytes, layout);
        if (_backing is null)
            _backing = _arena.AllocatePersistent(turn, _capacity, alignment: 64);

        _frames[_depth] = frame;
        _depth++;
        _top = checked(offset + bytes);
        return frame;
    }

    internal ActorStackFrameHandle PushTyped(ActorArenaTurn turn,
        ActorStackFrameLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Push(turn, layout.ByteLength, 8, layout);
    }

    internal ActorStackResumeTicket Suspend(ActorArenaTurn turn,
        ActorStackFrameHandle frame)
    {
        _arena.ValidateActiveTurn(turn);
        ValidateFrame(frame, 0, 0);
        if (_pendingResume is not null ||
            !ReferenceEquals(_frames[_depth - 1], frame))
            throw new InvalidOperationException(
                "Only the live top actor frame may suspend");
        var issued = new ActorStackResumeTicket(this, frame, turn.Epoch);
        _pendingResume = issued;
        return issued;
    }

    internal ActorStackFrameHandle Resume(ActorArenaTurn turn,
        ActorStackResumeTicket ticket)
    {
        _arena.ValidateActiveTurn(turn);
        ValidateTicket(ticket);
        if (turn.Epoch <= ticket.SuspendingEpoch)
            throw new InvalidOperationException(
                "Suspended actor frame requires a newly admitted physical turn");
        _pendingResume = null; // exact-ticket single claim
        return ticket.Frame;
    }

    internal void CancelSuspended(ActorArenaTurn turn,
        ActorStackResumeTicket ticket)
    {
        // In-flight physical-turn cleanup is legal during logical retirement.
        _arena.ValidatePhysicalTurn(turn);
        ValidateTicket(ticket);
        PopCore(turn, ticket.Frame, cancelSuspended: true);
    }

    internal long ReadI64(ActorArenaTurn turn, ActorStackFrameHandle frame, int slot)
    {
        _arena.ValidatePhysicalTurn(turn);
        int offset = CheckedSlotOffset(frame, slot, ActorStackSlotKind.I64);
        Span<byte> encoded = stackalloc byte[8];
        Read(turn, frame, offset, encoded);
        return BinaryPrimitives.ReadInt64LittleEndian(encoded);
    }

    internal void WriteI64(ActorArenaTurn turn,
        ActorStackFrameHandle frame, int slot, long value)
    {
        _arena.ValidatePhysicalTurn(turn);
        int offset = CheckedSlotOffset(frame, slot, ActorStackSlotKind.I64);
        Span<byte> encoded = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, value);
        Write(turn, frame, offset, encoded);
    }

    internal bool ReadBool(ActorArenaTurn turn,
        ActorStackFrameHandle frame, int slot)
    {
        _arena.ValidatePhysicalTurn(turn);
        int offset = CheckedSlotOffset(frame, slot, ActorStackSlotKind.Bool);
        Span<byte> encoded = stackalloc byte[1];
        Read(turn, frame, offset, encoded);
        if (encoded[0] > 1)
            throw new FormatException("Noncanonical bool in typed actor frame");
        return encoded[0] == 1;
    }

    internal void WriteBool(ActorArenaTurn turn,
        ActorStackFrameHandle frame, int slot, bool value)
    {
        _arena.ValidatePhysicalTurn(turn);
        int offset = CheckedSlotOffset(frame, slot, ActorStackSlotKind.Bool);
        Span<byte> encoded = stackalloc byte[1];
        encoded[0] = value ? (byte)1 : (byte)0;
        Write(turn, frame, offset, encoded);
    }

    private int CheckedSlotOffset(ActorStackFrameHandle frame,
        int slot, ActorStackSlotKind kind)
    {
        ValidateFrame(frame, 0, 0);
        if (frame.Layout is null)
            throw new InvalidOperationException(
                "Untyped host frame cannot masquerade as a typed guest spill");
        return frame.Layout.CheckedOffset(slot, kind);
    }

    private void ValidateTicket(ActorStackResumeTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!ReferenceEquals(ticket.Owner, this) ||
            !ReferenceEquals(_pendingResume, ticket))
            throw new InvalidOperationException("Foreign, canceled or replayed actor resume ticket");
        ValidateFrame(ticket.Frame, 0, 0);
        if (!ReferenceEquals(_frames[_depth - 1], ticket.Frame))
            throw new InvalidOperationException("Suspended actor stack is not LIFO");
    }

    internal void Read(ActorArenaTurn turn, ActorStackFrameHandle frame,
        int offset, Span<byte> destination)
    {
        _arena.ValidatePhysicalTurn(turn);
        if (_pendingResume is not null)
            throw new InvalidOperationException("Suspended actor stack cannot be read");
        ValidateFrame(frame, offset, destination.Length);
        _arena.Read(turn, _backing!, checked(frame.Offset + offset), destination);
    }

    internal void Write(ActorArenaTurn turn, ActorStackFrameHandle frame,
        int offset, ReadOnlySpan<byte> source)
    {
        _arena.ValidatePhysicalTurn(turn);
        if (_pendingResume is not null)
            throw new InvalidOperationException("Suspended actor stack cannot be written");
        ValidateFrame(frame, offset, source.Length);
        _arena.Write(turn, _backing!, checked(frame.Offset + offset), source);
    }

    internal void Pop(ActorArenaTurn turn, ActorStackFrameHandle frame) =>
        PopCore(turn, frame, cancelSuspended: false);

    private void PopCore(ActorArenaTurn turn,
        ActorStackFrameHandle frame, bool cancelSuspended)
    {
        _arena.ValidatePhysicalTurn(turn);
        if (_pendingResume is not null && !cancelSuspended)
            throw new InvalidOperationException(
                "Suspended actor frame must be resumed or explicitly canceled");
        if (cancelSuspended &&
            !ReferenceEquals(_pendingResume?.Frame, frame))
            throw new InvalidOperationException("Canceled actor resume ticket is stale");
        ValidateFrame(frame, 0, 0);
        if (!ReferenceEquals(_frames[_depth - 1], frame))
            throw new InvalidOperationException("Actor logical frames must be popped in LIFO order");

        // The persistent slab lives for the actor incarnation. Never free a
        // non-tail slab while other actor objects remain live: that would
        // accumulate unfillable holes in ActorArena's non-compacting region.
        // Scrub on every pop; native reclamation occurs at actor retirement.
        Span<byte> zeros = stackalloc byte[256];
        zeros.Clear();
        int remaining = frame.Length;
        int position = frame.Offset;
        while (remaining != 0)
        {
            int count = Math.Min(remaining, zeros.Length);
            _arena.Write(turn, _backing!, position, zeros[..count]);
            position += count;
            remaining -= count;
        }

        _top = _depth == 1
            ? 0
            : _frames[_depth - 2]!.Offset + _frames[_depth - 2]!.Length;
        _frames[--_depth] = null;
        if (cancelSuspended)
            _pendingResume = null; // only after successful memory scrubbing
        // Reusing the same offset never reissues a previously popped object.
    }

    private void ValidateFrame(ActorStackFrameHandle frame, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(frame.Owner, this) ||
            frame.Index < 0 || frame.Index >= _depth ||
            !ReferenceEquals(_frames[frame.Index], frame))
            throw new InvalidOperationException("Foreign or stale actor logical frame");
        if (offset < 0 || count < 0 || offset > frame.Length ||
            count > frame.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset),
                "Logical frame access outside issued bounds");
    }
}
