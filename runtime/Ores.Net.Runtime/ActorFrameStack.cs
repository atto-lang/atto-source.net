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
    internal ActorStackFrameHandle(ActorFrameStack owner, ActorExecutionContext execution, int index,
        int offset, int length)
    {
        Owner = owner;
        Execution = execution;
        Index = index;
        Offset = offset;
        Length = length;
    }

    internal ActorFrameStack Owner { get; }
    internal ActorExecutionContext Execution { get; }
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
/// Bounded receive-invocation frame storage within one actor incarnation.
/// Payload bytes reside in its ActorArena, not in CLR frame objects or a
/// separate allocator. Each synchronous receive has a fresh execution context;
/// completion scrubs and invalidates its stack before another DATA invocation.
///
/// This is a runtime substrate for future compiler-generated async frames:
/// it does NOT switch native stacks or implement guest await/actor lowering.
/// Future suspension needs an authenticated continuation retaining the original
/// execution identity; a subsequent ordinary DATA message is not that resume.
/// </summary>
/// <summary>
/// An actor-local logical frame budget exceeded its admitted bounds. Catching
/// this exception cannot recover the poisoned actor or complete the turn.
/// Physical CLR machine-stack overflow is a separate, unproven boundary.
/// </summary>
public sealed class ActorStackQuotaExceededException : InvalidOperationException
{
    public ActorStackQuotaExceededException() :
        base("Actor logical stack frame/byte budget exhausted") { }
}

internal sealed class ActorFrameStack
{
    private readonly ActorArena _arena;
    private readonly int _capacity;
    private readonly ActorStackFrameHandle?[] _frames;
    private ActorArenaHandle? _backing;
    private int _depth;
    private int _top;
    private bool _quotaPoisoned;
    private ActorExecutionContext? _execution;

    internal void BeginInvocation(ActorArenaTurn turn, ActorExecutionContext execution)
    {
        _arena.ValidateActiveTurn(turn);
        EnsureUsable();
        if (_execution is not null || _depth != 0 || _top != 0 ||
            execution.Actor.Id != turn.ActorId || execution.Actor.Generation != turn.Incarnation)
            throw new InvalidOperationException("Actor invocation stack identity mismatch");
        _execution = execution;
    }

    internal void EndInvocation(ActorArenaTurn turn, ActorExecutionContext execution)
    {
        _arena.ValidatePhysicalTurn(turn);
        if (!ReferenceEquals(_execution, execution))
            throw new InvalidOperationException("Foreign actor invocation cleanup");
        // Also runs on handler failure or quota poisoning. The persistent slab
        // stays actor-owned; only this completed receive's bytes are retired.
        Span<byte> zeros = stackalloc byte[256];
        zeros.Clear();
        for (int position = 0; position < _top;)
        {
            int count = Math.Min(_top - position, zeros.Length);
            _arena.Write(turn, _backing!, position, zeros[..count]);
            position += count;
        }
        Array.Clear(_frames, 0, _depth);
        _depth = _top = 0;
        _execution = null;
    }

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
        EnsureUsable();
        if (bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (alignment <= 0 || alignment > 64 ||
            (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        if (_depth == _frames.Length || bytes > _capacity)
            ThrowQuotaExceeded();

        int offset = checked((_top + alignment - 1) & ~(alignment - 1));
        if (offset > _capacity || bytes > _capacity - offset)
            ThrowQuotaExceeded();

        // Construct the capability before native admission. A failed allocation
        // cannot increment depth, publish a frame, or change the stack top.
        var frame = new ActorStackFrameHandle(this,
            _execution ?? throw new InvalidOperationException("No admitted actor invocation"),
            _depth, offset, bytes);
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
        EnsureUsable();
        ValidateFrame(frame, offset, destination.Length);
        _arena.Read(turn, _backing!, checked(frame.Offset + offset), destination);
    }

    internal void Write(ActorArenaTurn turn, ActorStackFrameHandle frame,
        int offset, ReadOnlySpan<byte> source)
    {
        _arena.ValidatePhysicalTurn(turn);
        EnsureUsable();
        ValidateFrame(frame, offset, source.Length);
        _arena.Write(turn, _backing!, checked(frame.Offset + offset), source);
    }

    internal void Pop(ActorArenaTurn turn, ActorStackFrameHandle frame)
    {
        _arena.ValidatePhysicalTurn(turn);
        EnsureUsable();
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
        EnsureUsable();
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
        EnsureUsable();
        ValidateFrame(frame, 0, 0);
        if (frame.ScalarSlotCount != slots || frame.ScalarBlockCount != blocks)
            throw new InvalidOperationException(
                "Actor scalar frame header disagrees with immutable compiler layout");
    }

    internal void ValidateScalarSlot(ActorArenaTurn turn,
        ActorStackFrameHandle frame, int slot, byte expected)
    {
        _arena.ValidatePhysicalTurn(turn);
        EnsureUsable();
        ValidateFrame(frame, 0, 0);
        if (frame.ScalarKind(slot) != expected)
            throw new InvalidOperationException(
                "Actor scalar frame slot retyping violates verified CFG schema");
    }

    // Synchronous dispatch must check this after the host handler returns.
    // A synchronous receive completes here; retaining a frame cannot grant
    // the next DATA invocation continuation authority.
    internal void ValidateSynchronousTurnExit(ActorArenaTurn turn)
    {
        _arena.ValidatePhysicalTurn(turn);
        if (_quotaPoisoned)
            throw new ActorStackQuotaExceededException();
    }

    private void EnsureUsable()
    {
        if (_quotaPoisoned)
            throw new InvalidOperationException(
                "Actor logical stack is terminally poisoned after quota exhaustion");
    }

    private void ThrowQuotaExceeded()
    {
        _quotaPoisoned = true;
        throw new ActorStackQuotaExceededException();
    }

    private void ValidateFrame(ActorStackFrameHandle frame, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(frame.Owner, this) ||
            !ReferenceEquals(frame.Execution, _execution) ||
            frame.Index < 0 || frame.Index >= _depth ||
            !ReferenceEquals(_frames[frame.Index], frame))
            throw new InvalidOperationException("Foreign or stale actor logical frame");
        if (offset < 0 || count < 0 || offset > frame.Length ||
            count > frame.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset),
                "Logical frame access outside issued bounds");
    }
}
