using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Ores.Net.Runtime;

/// <summary>An exact-issued, actor-local logical execution identity. Never a wire value.</summary>
public sealed class ActorFrameExecution
{
    internal ActorFrameExecution() { }
}

/// <summary>An exact-issued frame capability; it never exposes the native address.</summary>
public sealed class ActorFrameHandle
{
    internal ActorFrameHandle(int length) => Length = length;
    public int Length { get; }
}

/// <summary>One-shot continuation for a suspended logical actor stack.</summary>
public sealed class ActorFrameContinuation
{
    internal ActorFrameContinuation() { }
}

/// <summary>A terminal actor-local frame quota failure, not a CLR StackOverflowException.</summary>
public sealed class ActorFrameBudgetExceededException : InvalidOperationException
{
    internal ActorFrameBudgetExceededException()
        : base("Actor-owned logical frame stack quota exceeded") { }
}

/// <summary>
/// Bounded independent native frame-byte slab for exactly one actor incarnation.
/// This is a *logical* stack: compiler frames/return addresses and CLR carrier
/// recursion are NOT yet lowered to it. Guest or host code must not interpret
/// this object as physical OS stack-overflow protection.
///
/// All access copies bytes under a lock, with no raw pointer escape. An active
/// physical ActorArenaTurn authorizes each operation; frame capability objects
/// are checked by exact issuance rather than value equality. Suspension keeps
/// charged frame storage but revokes the prior physical-turn authority.
/// </summary>
public unsafe sealed class ActorFrameStack : IDisposable
{
    public const int DefaultMaximumFrames = 64;
    public const int DefaultMaximumBytes = 64 * 1024;
    public const int MaximumFrames = 4096;
    public const int MaximumBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly ActorArena _arena;
    private readonly ActorArenaBudget _budget;
    private readonly int _maxBytes;
    private readonly ActorFrameHandle?[] _frames;
    private readonly int[] _offsets;
    private byte* _storage;
    private bool _reserved;
    private bool _processReserved;
    private bool _retired;
    private bool _poisoned;
    private ActorFrameExecution? _execution;
    private ActorArenaTurn? _physicalTurn;
    private ActorFrameContinuation? _continuation;
    private int _depth;
    private int _used;

    public ActorFrameStack(ActorArena arena, ActorArenaBudget budget,
        int maximumFrames = DefaultMaximumFrames, int maximumBytes = DefaultMaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(budget);
        if (maximumFrames <= 0 || maximumFrames > MaximumFrames)
            throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        if (maximumBytes <= 0 || maximumBytes > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _arena = arena;
        _budget = budget;
        _maxBytes = maximumBytes;
        _frames = new ActorFrameHandle[maximumFrames];
        _offsets = new int[maximumFrames];
    }

    public int MaximumFrameCount => _frames.Length;
    public int MaximumFrameBytes => _maxBytes;
    public bool IsPoisoned { get { lock (_gate) return _poisoned; } }
    public bool IsSuspended { get { lock (_gate) return _continuation is not null; } }
    public bool IsRetired { get { lock (_gate) return _retired; } }
    public int Depth { get { lock (_gate) return _depth; } }
    public int ChargedBytes { get { lock (_gate) return _used; } }

    public ActorFrameExecution Begin(ActorArenaTurn physicalTurn)
    {
        lock (_gate)
        {
            EnsurePhysicalTurn(physicalTurn);
            EnsureHealthy();
            if (_execution is not null)
                throw new InvalidOperationException("An actor already owns a live frame execution");
            try
            {
                ReserveOnDemand();
            }
            catch
            {
                Poison();
                throw;
            }
            _execution = new ActorFrameExecution();
            _physicalTurn = physicalTurn;
            return _execution;
        }
    }

    public ActorFrameHandle Push(ActorArenaTurn physicalTurn,
        ActorFrameExecution execution, int bytes)
    {
        lock (_gate)
        {
            EnsureExecution(physicalTurn, execution);
            if (bytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            if (_depth == _frames.Length || bytes > _maxBytes - _used)
            {
                Poison();
                throw new ActorFrameBudgetExceededException();
            }
            var frame = new ActorFrameHandle(bytes);
            _offsets[_depth] = _used;
            _frames[_depth++] = frame;
            CryptographicOperations.ZeroMemory(new Span<byte>(_storage + _used, bytes));
            _used += bytes;
            return frame;
        }
    }

    public void Write(ActorArenaTurn physicalTurn, ActorFrameExecution execution,
        ActorFrameHandle frame, int offset, ReadOnlySpan<byte> source)
    {
        lock (_gate)
        {
            EnsureExecution(physicalTurn, execution);
            int index = FindFrame(frame);
            CheckRange(frame.Length, offset, source.Length);
            source.CopyTo(new Span<byte>(_storage + _offsets[index] + offset, source.Length));
        }
    }

    public void Read(ActorArenaTurn physicalTurn, ActorFrameExecution execution,
        ActorFrameHandle frame, int offset, Span<byte> destination)
    {
        lock (_gate)
        {
            EnsureExecution(physicalTurn, execution);
            int index = FindFrame(frame);
            CheckRange(frame.Length, offset, destination.Length);
            new ReadOnlySpan<byte>(_storage + _offsets[index] + offset, destination.Length)
                .CopyTo(destination);
        }
    }

    public void Pop(ActorArenaTurn physicalTurn, ActorFrameExecution execution,
        ActorFrameHandle frame)
    {
        lock (_gate)
        {
            EnsureExecution(physicalTurn, execution);
            if (_depth == 0 || !ReferenceEquals(_frames[_depth - 1], frame))
            {
                Poison();
                throw new InvalidOperationException("Actor frame pop violates strict LIFO");
            }
            int index = --_depth;
            CryptographicOperations.ZeroMemory(
                new Span<byte>(_storage + _offsets[index], frame.Length));
            _used = _offsets[index];
            _frames[index] = null;
            _offsets[index] = 0;
        }
    }

    public void Finish(ActorArenaTurn physicalTurn, ActorFrameExecution execution)
    {
        lock (_gate)
        {
            EnsureExecution(physicalTurn, execution);
            if (_depth != 0)
            {
                Poison();
                throw new InvalidOperationException("Actor frame execution has unpopped frames");
            }
            _physicalTurn = null;
            _execution = null;
        }
    }

    /// <summary>
    /// Trusted scheduler seam only. A suspended logical stack is not runnable
    /// on a new handler until the exact continuation is resumed on a new turn.
    /// The shipping synchronous ActorRuntime does not expose source suspension.
    /// </summary>
    public ActorFrameContinuation Suspend(ActorArenaTurn physicalTurn,
        ActorFrameExecution execution)
    {
        lock (_gate)
        {
            EnsureExecution(physicalTurn, execution);
            if (_depth == 0)
            {
                Poison();
                throw new InvalidOperationException("Cannot suspend an empty actor stack");
            }
            _continuation = new ActorFrameContinuation();
            _physicalTurn = null;
            return _continuation;
        }
    }

    public void Resume(ActorArenaTurn physicalTurn, ActorFrameExecution execution,
        ActorFrameContinuation continuation)
    {
        lock (_gate)
        {
            EnsurePhysicalTurn(physicalTurn);
            EnsureHealthy();
            if (_continuation is null ||
                !ReferenceEquals(_execution, execution) ||
                !ReferenceEquals(_continuation, continuation) ||
                _physicalTurn is not null)
            {
                Poison();
                throw new InvalidOperationException("Stale or foreign actor frame continuation");
            }
            _physicalTurn = physicalTurn;
            _continuation = null;
        }
    }

    /// <summary>Detect a swallowed quota failure or forgotten synchronous unwind.</summary>
    public void ValidateTurnExit(ActorArenaTurn physicalTurn)
    {
        lock (_gate)
        {
            EnsurePhysicalTurn(physicalTurn);
            if (_poisoned)
                throw new InvalidOperationException("Actor frame storage has been poisoned");
            if (ReferenceEquals(_physicalTurn, physicalTurn) && _execution is not null)
            {
                Poison();
                throw new InvalidOperationException("Actor handler returned with live frames");
            }
        }
    }

    /// <summary>Close admission on STOP; final reclamation follows carrier quiescence.</summary>
    public void Seal()
    {
        lock (_gate) _retired = true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _retired = true;
            Poison();
            if (_storage != null)
            {
                CryptographicOperations.ZeroMemory(new Span<byte>(_storage, _maxBytes));
                NativeMemory.Free(_storage);
                _storage = null;
            }
            if (_reserved)
            {
                if (_processReserved)
                {
                    ActorArenaBudget.Process.Refund(_maxBytes);
                    _processReserved = false;
                }
                _budget.Refund(_maxBytes);
                _reserved = false;
            }
        }
    }

    private void ReserveOnDemand()
    {
        if (_reserved) return;
        _budget.Reserve(_maxBytes);
        bool global = false;
        try
        {
            if (!ReferenceEquals(_budget, ActorArenaBudget.Process))
            {
                ActorArenaBudget.Process.Reserve(_maxBytes);
                global = true;
            }
            byte* native = (byte*)NativeMemory.AllocZeroed((nuint)_maxBytes);
            if (native == null)
                throw new OutOfMemoryException("Actor frame slab allocation failed");
            _storage = native;
            _reserved = true;
            _processReserved = global;
        }
        catch
        {
            if (global) ActorArenaBudget.Process.Refund(_maxBytes);
            _budget.Refund(_maxBytes);
            throw;
        }
    }

    private void EnsurePhysicalTurn(ActorArenaTurn turn)
    {
        // Checks exact issuing arena, actor incarnation, carrier, and turn epoch.
        _arena.ValidateActiveTurn(turn);
    }

    private void EnsureHealthy()
    {
        if (_retired || _poisoned)
            throw new InvalidOperationException("Actor frame storage closed or poisoned");
    }

    private void EnsureExecution(ActorArenaTurn turn, ActorFrameExecution execution)
    {
        EnsurePhysicalTurn(turn);
        EnsureHealthy();
        if (!ReferenceEquals(_physicalTurn, turn) ||
            !ReferenceEquals(_execution, execution) ||
            _continuation is not null)
        {
            Poison();
            throw new InvalidOperationException("Foreign or suspended actor frame execution");
        }
    }

    private int FindFrame(ActorFrameHandle frame)
    {
        for (int i = 0; i < _depth; i++)
            if (ReferenceEquals(_frames[i], frame))
                return i;
        Poison();
        throw new InvalidOperationException("Stale or foreign actor frame capability");
    }

    private static void CheckRange(int length, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > length || size > length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset),
                "Actor frame byte access exceeds frame bounds");
    }

    private void Poison()
    {
        _poisoned = true;
        _physicalTurn = null;
        _continuation = null;
        _execution = null;
        _depth = 0;
        _used = 0;
        Array.Clear(_frames);
        Array.Clear(_offsets);
        if (_storage != null)
            CryptographicOperations.ZeroMemory(new Span<byte>(_storage, _maxBytes));
    }
}
