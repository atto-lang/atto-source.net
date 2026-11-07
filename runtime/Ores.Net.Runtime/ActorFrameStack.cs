using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Ores.Net.Runtime;

/// <summary>
/// Exact, actor-owned frame capability. Frame bytes are never exposed as a
/// managed pointer or span that could outlive the owning actor's physical turn.
/// The containing frame stack enforces issuance, LIFO, actor identity, turn,
/// carrier identity, and bounded depth.
/// </summary>
public sealed class ActorFrame : IDisposable
{
    internal ActorFrame(ActorFrameStack owner, ActorArenaTurn turn,
        int offset, int length, int previousTop, int depth)
    {
        Owner = owner;
        Turn = turn;
        Offset = offset;
        Length = length;
        PreviousTop = previousTop;
        Depth = depth;
    }

    internal ActorFrameStack Owner { get; }
    internal ActorArenaTurn Turn { get; }
    internal int Offset { get; }
    internal int PreviousTop { get; }
    internal int Depth { get; }
    internal bool Released { get; private set; }
    public int Length { get; }

    internal void MarkReleased() => Released = true;

    /// <summary>
    /// Pop this frame on the admitting actor carrier. After disposal, every
    /// read/write capability to its bytes is invalid, and bytes are scrubbed.
    /// </summary>
    public void Dispose() => Owner.Pop(this);
}

/// <summary>
/// Bounded, independently allocated unmanaged frame storage for one actor
/// incarnation. Frame bytes never share another actor's arena, and reservations
/// charge both the runtime/tenant and process-wide native-memory budgets.
///
/// This is a *logical, explicit frame stack*, not a private CLR carrier-thread
/// execution stack, not a guard-paged fiber stack, and not yet source-compiler
/// frame lowering. Arbitrary recursive managed callbacks can still overflow
/// the shared CLR carrier stack; future stackless guest lowering is mandatory
/// before claiming physical stack-overflow containment.
/// </summary>
public unsafe sealed class ActorFrameStack : IDisposable
{
    public const int DefaultStackBytes = 64 * 1024;
    public const int DefaultMaxFrames = 64;
    public const int HandlerRootBytes = 128;
    public const int MaxStackBytes = 4 * 1024 * 1024;
    public const int MaxFrameDepth = 4096;
    private const int Alignment = 64;

    private readonly object _gate = new();
    private readonly ActorArena _arena;
    private readonly ActorArenaBudget _budget;
    private readonly int _stackBytes;
    private readonly int _reservedBytes;
    private readonly ActorFrame?[] _frames;
    private byte* _memory;
    private int _top;
    private int _depth;
    private bool _poisoned;
    private bool _admissionRetired;
    private bool _reclaimed;
    private bool _processBudgetReserved;
    private bool _budgetRefunded;

    public ActorFrameStack(ActorArena arena, int stackBytes = DefaultStackBytes,
        int maxFrames = DefaultMaxFrames, ActorArenaBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (stackBytes < HandlerRootBytes || stackBytes > MaxStackBytes)
            throw new ArgumentOutOfRangeException(nameof(stackBytes));
        if (maxFrames < 1 || maxFrames > MaxFrameDepth)
            throw new ArgumentOutOfRangeException(nameof(maxFrames));

        _arena = arena;
        _stackBytes = stackBytes;
        _reservedBytes = checked((stackBytes + Alignment - 1) & ~(Alignment - 1));
        _frames = new ActorFrame?[maxFrames];
        _budget = budget ?? ActorArenaBudget.Process;
        _budget.Reserve(_reservedBytes);
        try
        {
            if (!ReferenceEquals(_budget, ActorArenaBudget.Process))
            {
                ActorArenaBudget.Process.Reserve(_reservedBytes);
                _processBudgetReserved = true;
            }
            _memory = (byte*)NativeMemory.AlignedAlloc(
                (nuint)_reservedBytes, (nuint)Alignment);
            if (_memory == null)
                throw new OutOfMemoryException("Unable to reserve actor-owned frame stack");
            CryptographicOperations.ZeroMemory(new Span<byte>(_memory, _reservedBytes));
        }
        catch
        {
            if (_memory != null)
            {
                NativeMemory.AlignedFree(_memory);
                _memory = null;
            }
            RefundBudget();
            throw;
        }
    }

    public ulong ActorId => _arena.ActorId;
    public ulong Incarnation => _arena.Incarnation;
    public int CapacityBytes => _stackBytes;
    public int MaxFrames => _frames.Length;
    public int ReservedNativeBytes => _reservedBytes;

    public int ActiveDepth
    {
        get { lock (_gate) return _depth; }
    }

    public bool IsPoisoned
    {
        get { lock (_gate) return _poisoned; }
    }

    public bool IsReclaimed
    {
        get { lock (_gate) return _reclaimed; }
    }

    /// <summary>
    /// Enter an explicit frame. Quota/depth exhaustion permanently poisons
    /// this actor's frame stack, even if guest code catches the exception.
    /// Runtime handler exit checks the poisoned state and retires only the
    /// affected actor; sibling actor stacks are independent.
    /// </summary>
    public ActorFrame Push(ActorArenaTurn turn, int bytes, int alignment = 8)
    {
        _arena.ValidateActiveTurn(turn);
        if (bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (alignment <= 0 || alignment > Alignment ||
            (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));

        lock (_gate)
        {
            EnsureOpenLocked();
            EnsureAdmissionOpenLocked();
            if (_poisoned)
                throw new InvalidOperationException("Actor frame stack is poisoned");
            int start;
            try
            {
                start = checked((_top + alignment - 1) & ~(alignment - 1));
            }
            catch (OverflowException)
            {
                _poisoned = true;
                throw new InvalidOperationException("Actor frame stack offset exhausted");
            }
            if (_depth >= _frames.Length ||
                start > _stackBytes || bytes > _stackBytes - start)
            {
                _poisoned = true;
                throw new InvalidOperationException(
                    "Actor frame stack depth or native-byte quota exhausted");
            }
            var frame = new ActorFrame(this, turn, start, bytes, _top, _depth + 1);
            _frames[_depth] = frame;
            _depth++;
            _top = start + bytes;
            return frame;
        }
    }

    public void Write(ActorArenaTurn turn, ActorFrame frame, int offset,
        ReadOnlySpan<byte> source)
    {
        _arena.ValidateActiveTurn(turn);
        lock (_gate)
        {
            ValidateIssuedFrameLocked(turn, frame, offset, source.Length);
            source.CopyTo(new Span<byte>(_memory + frame.Offset + offset, source.Length));
        }
    }

    public void Read(ActorArenaTurn turn, ActorFrame frame, int offset,
        Span<byte> destination)
    {
        _arena.ValidateActiveTurn(turn);
        lock (_gate)
        {
            ValidateIssuedFrameLocked(turn, frame, offset, destination.Length);
            new ReadOnlySpan<byte>(_memory + frame.Offset + offset, destination.Length)
                .CopyTo(destination);
        }
    }

    /// <summary>
    /// Release only the top frame from the same physical actor turn and carrier.
    /// Cleanup is still permitted after actor retirement, allowing an in-flight
    /// handler to unwind after Stop while forbidding further allocations.
    /// </summary>
    public void Pop(ActorFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(frame.Owner, this))
            throw new InvalidOperationException("Foreign actor frame capability");
        if (frame.Released)
            return;
        _arena.ValidateTurnForCleanup(frame.Turn);
        lock (_gate)
        {
            EnsureOpenLocked();
            if (_depth == 0 || frame.Depth != _depth ||
                !ReferenceEquals(_frames[_depth - 1], frame))
            {
                _poisoned = true;
                throw new InvalidOperationException("Out-of-order or stale actor frame pop");
            }

            CryptographicOperations.ZeroMemory(
                new Span<byte>(_memory + frame.PreviousTop, _top - frame.PreviousTop));
            _top = frame.PreviousTop;
            _frames[--_depth] = null;
            frame.MarkReleased();
        }
    }

    /// <summary>
    /// Called before a completed handler is accounted as successful, so that
    /// caught overflow/forgery attempts never let a poisoned actor continue.
    /// </summary>
    public void EnsureHealthy(ActorArenaTurn turn)
    {
        _arena.ValidateTurnForCleanup(turn);
        lock (_gate)
        {
            EnsureOpenLocked();
            if (_poisoned)
                throw new InvalidOperationException("Actor frame stack was poisoned");
        }
    }

    /// <summary>
    /// Serialize STOP with frame allocations and data access. The actor
    /// arena's physical-turn check alone is insufficient because STOP may
    /// retire the arena between that check and the frame-stack lock.
    /// </summary>
    internal void RetireAdmission()
    {
        lock (_gate)
            _admissionRetired = true;
    }

    /// <summary>
    /// Runtime-only physical cleanup after actor dispatch has quiesced.
    /// Orphaned frames are forcibly invalidated and the private native slab
    /// is fully zeroed before release. Never call while a handler is running.
    /// </summary>
    internal void ReclaimQuiescent()
    {
        lock (_gate)
            ReclaimLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_depth != 0 && !_reclaimed)
                throw new InvalidOperationException(
                    "Cannot dispose actor stack with live frames");
            ReclaimLocked();
        }
    }

    private void ValidateIssuedFrameLocked(ActorArenaTurn turn, ActorFrame frame,
        int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureOpenLocked();
        EnsureAdmissionOpenLocked();
        if (_poisoned)
            throw new InvalidOperationException("Actor frame stack is poisoned");
        if (!ReferenceEquals(frame.Owner, this) || frame.Released ||
            !ReferenceEquals(frame.Turn, turn) ||
            frame.Depth <= 0 || frame.Depth > _depth ||
            !ReferenceEquals(_frames[frame.Depth - 1], frame))
            throw new InvalidOperationException("Stale or foreign actor frame capability");
        if (offset < 0 || length < 0 ||
            offset > frame.Length || length > frame.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset),
                "Actor frame read/write exceeds its bounded allocation");
    }

    private void EnsureOpenLocked()
    {
        if (_reclaimed)
            throw new ObjectDisposedException(nameof(ActorFrameStack));
    }

    private void EnsureAdmissionOpenLocked()
    {
        if (_admissionRetired)
            throw new InvalidOperationException("Actor frame stack admission is retired");
    }

    private void ReclaimLocked()
    {
        if (_reclaimed)
            return;
        _admissionRetired = true;
        if (_memory != null)
        {
            CryptographicOperations.ZeroMemory(new Span<byte>(_memory, _reservedBytes));
            NativeMemory.AlignedFree(_memory);
            _memory = null;
        }
        Array.Clear(_frames, 0, _frames.Length);
        _depth = 0;
        _top = 0;
        _poisoned = true;
        _reclaimed = true;
        RefundBudget();
    }

    private void RefundBudget()
    {
        if (_budgetRefunded)
            return;
        _budget.Refund(_reservedBytes);
        if (_processBudgetReserved)
        {
            ActorArenaBudget.Process.Refund(_reservedBytes);
            _processBudgetReserved = false;
        }
        _budgetRefunded = true;
    }
}
