using System.Buffers.Binary;
using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>
/// Compiler/host bridge for the bounded synchronous scalar actor profile.
/// Defaults and handles are host metadata; all live mutable guest values are
/// in the owning actor's native arena. No raw state/arena reference escapes.
/// This does not implement shared application regions or concurrent actors.
/// </summary>
public sealed class ActorNativeScalarState
{
    private readonly long[] _defaults;
    private readonly byte[] _kinds;
    private ActorArenaHandle? _storage;
    private ActorRef _owner;
    private bool _initialized;
    private int _executing;

    public ActorNativeScalarState(ReadOnlySpan<long> defaults, ReadOnlySpan<byte> kinds)
    {
        if (defaults.Length is < 1 or > 16 || defaults.Length != kinds.Length)
            throw new ArgumentException("Scalar actor state requires 1..16 exact typed defaults");
        for (int i = 0; i < defaults.Length; i++)
            if ((kinds[i] != 1 && kinds[i] != 2) || (kinds[i] == 2 && defaults[i] is not (0 or 1)))
                throw new ArgumentException("Invalid scalar actor state kind/default");
        _defaults = defaults.ToArray();
        _kinds = kinds.ToArray();
    }

    /// <summary>
    /// Compiler-owned critical-read of one actor-local native i64 cell.
    /// Returns only a detached Copy scalar. The capability and the arena
    /// read loan remain private and are disposed before caller Send().
    /// A foreign actor, wrong generation, or off-carrier access is denied.
    /// </summary>
    public long ReadInt64(ActorContext context, int field)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ValidateActiveTurn();
        if ((uint)field >= (uint)_kinds.Length || _kinds[field] != 1)
            throw new InvalidOperationException("Scalar state field is not int64");
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            throw new InvalidOperationException("Scalar state is physically in use");
        try
        {
            context.ValidateActiveTurn();
            if (!_initialized || context.Self != _owner || _storage is null)
                throw new InvalidOperationException("Missing or foreign native scalar state owner");
            ActorArenaHandle storage = _storage;
            using (context.BorrowRead(storage))
            {
                Span<byte> detached = stackalloc byte[sizeof(long)];
                context.Read(storage, field * sizeof(long), detached);
                return BinaryPrimitives.ReadInt64LittleEndian(detached);
            }
        }
        finally { Volatile.Write(ref _executing, 0); }
    }

    public long AddInt64(ActorContext context, int field, long delta)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ValidateActiveTurn();
        if ((uint)field >= (uint)_kinds.Length || _kinds[field] != 1)
            throw new InvalidOperationException("Scalar state field is not int64");
        // A wrong caller may never park an actor carrier waiting for private
        // state. The current source runtime admits one physical turn; this
        // nonparking fence also denies accidental cross-actor host aliasing.
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            throw new InvalidOperationException("Scalar state is physically in use");
        try
        {
            context.ValidateActiveTurn();
            if (_initialized && context.Self != _owner)
                throw new InvalidOperationException("Scalar state belongs to another actor incarnation");
            if (!_initialized)
            {
                Span<byte> bytes = stackalloc byte[_defaults.Length * sizeof(long)];
                for (int i = 0; i < _defaults.Length; i++)
                    BinaryPrimitives.WriteInt64LittleEndian(bytes[(i * sizeof(long))..], _defaults[i]);
                ActorArenaHandle candidate = context.AllocatePersistent(bytes.Length);
                context.Write(candidate, 0, bytes);
                _storage = candidate;
                _owner = context.Self;
                _initialized = true;
            }

            // One activation on the caller's existing logical stack, not a
            // new execution context or independent receive-bound stack.
            ActorStackFrameHandle frame = context.PushStackFrame(16);
            try
            {
                ActorArenaHandle storage = _storage ?? throw new InvalidOperationException("Scalar state initialization incomplete");
                Span<byte> slot = stackalloc byte[8];
                context.Read(storage, field * sizeof(long), slot);
                context.WriteStackFrame(frame, 0, slot);
                BinaryPrimitives.WriteInt64LittleEndian(slot, delta);
                context.WriteStackFrame(frame, 8, slot);
                context.ReadStackFrame(frame, 0, slot);
                long previous = BinaryPrimitives.ReadInt64LittleEndian(slot);
                context.ReadStackFrame(frame, 8, slot);
                long result = checked(previous + BinaryPrimitives.ReadInt64LittleEndian(slot));
                BinaryPrimitives.WriteInt64LittleEndian(slot, result);
                context.WriteStackFrame(frame, 0, slot);
                context.ReadStackFrame(frame, 0, slot);
                context.Write(storage, field * sizeof(long), slot);
                return BinaryPrimitives.ReadInt64LittleEndian(slot);
            }
            finally { context.PopStackFrame(frame); }
        }
        finally { Volatile.Write(ref _executing, 0); }
    }
}
