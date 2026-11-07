namespace Ores.Net.Runtime;

/// <summary>
/// Exact-issued, actor-local logical activation frame. A frame is not an OS/CLR
/// machine stack and cannot be used to resume a C# call stack. Only the
/// originating actor's currently admitted physical turn can access its bytes.
/// </summary>
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
        ValidateFrame(frame, offset, destination.Length);
        _arena.Read(turn, _backing!, checked(frame.Offset + offset), destination);
    }

    internal void Write(ActorArenaTurn turn, ActorStackFrameHandle frame,
        int offset, ReadOnlySpan<byte> source)
    {
        ValidateFrame(frame, offset, source.Length);
        _arena.Write(turn, _backing!, checked(frame.Offset + offset), source);
    }

    internal void Pop(ActorArenaTurn turn, ActorStackFrameHandle frame)
    {
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
