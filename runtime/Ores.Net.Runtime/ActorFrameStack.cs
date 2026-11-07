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
        int offset, int length)
    {
        Owner = owner;
        Index = index;
        Offset = offset;
        Length = length;
    }

    internal ActorFrameStack Owner { get; }
    internal int Index { get; }
    internal int Offset { get; }
    public int Length { get; }

    // The compiler's declared scalar kinds and CFG block count are sealed to
    // an exact-issued frame at the end of ABI initialization. Raw native bytes
    // remain mutable for trusted host tests, but cannot retype a sealed ABI.
    private byte[]? _scalarKinds;
    private int _scalarBlocks;
    internal int ScalarSlotCount => _scalarKinds?.Length ??
        throw new InvalidOperationException("Unsealed actor scalar frame ABI");
    internal int ScalarBlockCount => _scalarKinds is null
        ? throw new InvalidOperationException("Unsealed actor scalar frame ABI")
        : _scalarBlocks;

    internal byte ScalarKind(int slot)
    {
        if (_scalarKinds is null)
            throw new InvalidOperationException("Unsealed actor scalar frame ABI");
        if ((uint)slot >= (uint)_scalarKinds.Length)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return _scalarKinds[slot];
    }

    internal void SealScalarLayout(ReadOnlySpan<byte> kinds, int blocks)
    {
        if (_scalarKinds is not null)
            throw new InvalidOperationException("Actor scalar frame ABI already sealed");
        _scalarKinds = kinds.ToArray();
        _scalarBlocks = blocks;
    }
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
        int bytes, int alignment = 8)
    {
        // Unlike cleanup, new frame admission is forbidden after retirement.
        _arena.ValidateActiveTurn(turn);
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
        var frame = new ActorStackFrameHandle(this, _depth, offset, bytes);
        if (_backing is null)
            _backing = _arena.AllocatePersistent(turn, _capacity, alignment: 64);

        _frames[_depth] = frame;
        _depth++;
        _top = checked(offset + bytes);
        return frame;
    }

    internal void Read(ActorArenaTurn turn, ActorStackFrameHandle frame,
        int offset, Span<byte> destination)
    {
        _arena.ValidatePhysicalTurn(turn);
        ValidateFrame(frame, offset, destination.Length);
        _arena.Read(turn, _backing!, checked(frame.Offset + offset), destination);
    }

    internal void Write(ActorArenaTurn turn, ActorStackFrameHandle frame,
        int offset, ReadOnlySpan<byte> source)
    {
        _arena.ValidatePhysicalTurn(turn);
        ValidateFrame(frame, offset, source.Length);
        _arena.Write(turn, _backing!, checked(frame.Offset + offset), source);
    }

    internal void Pop(ActorArenaTurn turn, ActorStackFrameHandle frame)
    {
        _arena.ValidatePhysicalTurn(turn);
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
        // Reusing the same offset never reissues a previously popped object.
    }

    // Host-only compiler ABI control. These operations authenticate the same
    // actor incarnation and active physical carrier as native frame accesses.
    // A copied/forged native header or slot tag cannot redefine the frozen
    // compiler schema after SealScalarFrame succeeds.
    internal void SealScalarFrame(ActorArenaTurn turn,
        ActorStackFrameHandle frame, ReadOnlySpan<byte> declaredTypes, int blocks)
    {
        _arena.ValidateActiveTurn(turn);
        ValidateFrame(frame, 0, 0);
        if (declaredTypes.Length is <= 0 or > ActorScalarFrameAbiV1.MaxValueSlots ||
            blocks is <= 0 or > ActorScalarFrameAbiV1.MaxBlocks ||
            frame.Length != ActorScalarFrameAbiV1.FrameBytes(declaredTypes.Length))
            throw new FormatException("Invalid actor scalar ABI seal layout");
        foreach (byte tag in declaredTypes)
            if (tag is not (1 or 2))
                throw new FormatException("Invalid actor scalar ABI sealed type");
        frame.SealScalarLayout(declaredTypes, blocks);
    }

    internal void ValidateScalarHeader(ActorArenaTurn turn,
        ActorStackFrameHandle frame, int slots, int blocks)
    {
        _arena.ValidatePhysicalTurn(turn);
        ValidateFrame(frame, 0, 0);
        if (frame.ScalarSlotCount != slots || frame.ScalarBlockCount != blocks)
            throw new InvalidOperationException(
                "Actor scalar frame header disagrees with immutable compiler layout");
    }

    internal void ValidateScalarSlot(ActorArenaTurn turn,
        ActorStackFrameHandle frame, int slot, byte expected)
    {
        _arena.ValidatePhysicalTurn(turn);
        ValidateFrame(frame, 0, 0);
        if (frame.ScalarKind(slot) != expected)
            throw new InvalidOperationException(
                "Actor scalar frame slot retyping violates verified CFG schema");
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
