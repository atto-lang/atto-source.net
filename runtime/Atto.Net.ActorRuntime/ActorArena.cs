using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atto.Net.ActorRuntime;

public sealed class ActorResourceExhaustedException : InvalidOperationException
{
    public ActorResourceExhaustedException(string reason) : base(reason) { }
}

/// <summary>Exact-issued physical turn authority; never serialize or send.</summary>
[JsonConverter(typeof(ActorTurnNoWireConverter))]
public sealed class ActorTurn
{
    internal ActorTurn(ActorArena owner) => Owner = owner;
    internal ActorArena Owner { get; }
}

/// <summary>Exact-issued actor-local logical frame authority; never a wire value.</summary>
[JsonConverter(typeof(ActorFrameNoWireConverter))]
public sealed class ActorFrameHandle
{
    internal ActorFrameHandle(ActorArena owner, int index, int offset, int length)
    {
        Owner = owner;
        Index = index;
        Offset = offset;
        Length = length;
    }

    internal ActorArena Owner { get; }
    internal int Index { get; }
    internal int Offset { get; }
    public int Length { get; }
    internal ActorScalarKind[]? SealedKinds { get; set; }
    internal int SealedBlockCount { get; set; }
}

public enum ActorScalarKind : byte
{
    I64 = 1,
    Bool = 2,
}

/// <summary>
/// One bounded native arena per actor incarnation, also backing all logical
/// frames. There is NO independent frame slab or additional native allocator.
/// Physical CLR machine stacks remain shared and are NOT isolated by this type.
/// All native bytes are copied under the actor lock; pointers never escape.
/// </summary>
public unsafe sealed class ActorArena : IDisposable
{
    public const int MaximumArenaBytes = 4 * 1024 * 1024;
    public const int MaximumFrames = 1024;

    private readonly object _gate = new();
    private readonly TenantVm _tenant;
    private readonly int _capacity;
    private readonly ActorFrameHandle?[] _frames;
    private readonly GuestCooperationBudget _meter;
    private byte* _native;
    private ActorTurn? _activeTurn;
    private int _depth;
    private int _top;
    private bool _stopping;
    private bool _terminal;

    internal ActorArena(TenantVm tenant, int capacity, int maximumFrames, ulong logicalFuel)
    {
        _tenant = tenant;
        _capacity = capacity;
        _frames = new ActorFrameHandle[maximumFrames];
        _meter = new GuestCooperationBudget(logicalFuel);
        _native = (byte*)NativeMemory.AllocZeroed((nuint)capacity);
        if (_native == null) throw new OutOfMemoryException("Actor native arena allocation failed");
    }

    public int NativeReservedBytes { get { lock (_gate) return _native == null ? 0 : _capacity; } }
    public bool IsStopped { get { lock (_gate) return _stopping; } }
    public int FrameDepth { get { lock (_gate) return _depth; } }

    public ActorTurn BeginTurn()
    {
        lock (_gate)
        {
            EnsureRunning();
            if (_activeTurn is not null)
                TerminalFailure("Concurrent or reentrant actor turn admission");
            var turn = new ActorTurn(this);
            _activeTurn = turn;
            _meter.BeginDispatch();
            return turn;
        }
    }

    // Called in a finally block by the trusted dispatcher. Even if guest
    // code catches a quota/fuel exception, its enclosing turn cannot pass.
    public void EndTurn(ActorTurn turn)
    {
        lock (_gate)
        {
            ValidateTurn(turn);
            _activeTurn = null;
            bool failed = _terminal;
            if (_stopping || failed) ReclaimIfQuiescent();
            if (failed)
                throw new ActorResourceExhaustedException("Terminal actor failure cannot be swallowed");
        }
    }

    public ActorFrameHandle PushFrame(ActorTurn turn, int bytes, int alignment = 8)
    {
        lock (_gate)
        {
            EnsureActive(turn);
            if (bytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            if (alignment is < 1 or > 64 || (alignment & (alignment - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(alignment));

            int offset = checked((_top + alignment - 1) & ~(alignment - 1));
            if (_depth == _frames.Length || offset > _capacity ||
                bytes > _capacity - offset)
                TerminalFailure("Actor logical stack frame/byte quota exhausted");

            var frame = new ActorFrameHandle(this, _depth, offset, bytes);
            CryptographicOperations.ZeroMemory(new Span<byte>(_native + offset, bytes));
            _frames[_depth++] = frame;
            _top = checked(offset + bytes);
            return frame;
        }
    }

    public void PopFrame(ActorTurn turn, ActorFrameHandle frame)
    {
        lock (_gate)
        {
            EnsureActive(turn);
            ValidateFrame(frame, 0, 0);
            if (frame.Index != _depth - 1)
                TerminalFailure("Actor logical frame LIFO violation");
            CryptographicOperations.ZeroMemory(new Span<byte>(_native + frame.Offset, frame.Length));
            _frames[--_depth] = null;
            _top = frame.Offset;
        }
    }

    public void WriteFrame(ActorTurn turn, ActorFrameHandle frame, int offset, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            EnsureActive(turn);
            ValidateFrame(frame, offset, bytes.Length);
            bytes.CopyTo(new Span<byte>(_native + frame.Offset + offset, bytes.Length));
        }
    }

    public void ReadFrame(ActorTurn turn, ActorFrameHandle frame, int offset, Span<byte> destination)
    {
        lock (_gate)
        {
            EnsureActive(turn);
            ValidateFrame(frame, offset, destination.Length);
            new ReadOnlySpan<byte>(_native + frame.Offset + offset, destination.Length).CopyTo(destination);
        }
    }

    /// <summary>Freeze trusted verified-CFG scalar metadata; caller arrays cannot retype it.</summary>
    public void SealScalarSchema(
        ActorTurn turn, ActorFrameHandle frame,
        ReadOnlySpan<ActorScalarKind> kinds, int blockCount)
    {
        lock (_gate)
        {
            EnsureActive(turn);
            ValidateFrame(frame, 0, 0);
            if (frame.SealedKinds is not null)
                throw new InvalidOperationException("Actor frame schema already sealed");
            if (kinds.Length is < 1 or > 256 || blockCount is < 1 or > 128 ||
                kinds.Length > frame.Length / 8)
                throw new ArgumentOutOfRangeException(nameof(kinds));
            foreach (var kind in kinds)
                if (kind is not (ActorScalarKind.I64 or ActorScalarKind.Bool))
                    throw new ArgumentOutOfRangeException(nameof(kinds));
            frame.SealedKinds = kinds.ToArray();
            frame.SealedBlockCount = blockCount;
        }
    }

    public int ReadSealedBlockCount(ActorTurn turn, ActorFrameHandle frame)
    {
        lock (_gate)
        {
            EnsureActive(turn);
            ValidateFrame(frame, 0, 0);
            if (frame.SealedKinds is null)
                throw new InvalidOperationException("Unsealed frame");
            return frame.SealedBlockCount;
        }
    }

    public long ReadI64(ActorTurn turn, ActorFrameHandle frame, int slot)
    {
        lock (_gate)
        {
            int offset = ValidateScalarSlot(turn, frame, slot, ActorScalarKind.I64);
            return BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(_native + offset, 8));
        }
    }

    public void WriteI64(ActorTurn turn, ActorFrameHandle frame, int slot, long value)
    {
        lock (_gate)
        {
            int offset = ValidateScalarSlot(turn, frame, slot, ActorScalarKind.I64);
            BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(_native + offset, 8), value);
        }
    }

    public bool ReadBool(ActorTurn turn, ActorFrameHandle frame, int slot)
    {
        lock (_gate)
        {
            int offset = ValidateScalarSlot(turn, frame, slot, ActorScalarKind.Bool);
            var bytes = new ReadOnlySpan<byte>(_native + offset, 8);
            if (bytes[0] > 1)
                TerminalFailure("Noncanonical actor bool tag");
            for (int i = 1; i < 8; i++)
                if (bytes[i] != 0) TerminalFailure("Noncanonical actor bool padding");
            return bytes[0] == 1;
        }
    }

    public void WriteBool(ActorTurn turn, ActorFrameHandle frame, int slot, bool value)
    {
        lock (_gate)
        {
            int offset = ValidateScalarSlot(turn, frame, slot, ActorScalarKind.Bool);
            var bytes = new Span<byte>(_native + offset, 8);
            bytes.Clear();
            bytes[0] = value ? (byte)1 : (byte)0;
        }
    }

    public GuestMeterSnapshot MeterSnapshot(ActorTurn turn)
    {
        lock (_gate)
        {
            EnsureActive(turn);
            return _meter.Snapshot();
        }
    }

    public GuestChargeResult ChargeBackedge(ActorTurn turn, ulong weight = 1) =>
        Charge(turn, weight, backedge: true);

    public GuestChargeResult ChargeOperation(ActorTurn turn, ulong weight) =>
        Charge(turn, weight, backedge: false);

    private GuestChargeResult Charge(ActorTurn turn, ulong weight, bool backedge)
    {
        lock (_gate)
        {
            EnsureActive(turn);
            try { return _meter.Charge(weight, backedge); }
            catch (ActorResourceExhaustedException)
            {
                _terminal = true;
                _stopping = true;
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_native == null) return;
            _stopping = true;
            ReclaimIfQuiescent();
        }
    }

    public void Dispose() => Stop();

    private int ValidateScalarSlot(
        ActorTurn turn, ActorFrameHandle frame, int slot, ActorScalarKind expected)
    {
        EnsureActive(turn);
        ValidateFrame(frame, 0, 0);
        var kinds = frame.SealedKinds ??
            throw new InvalidOperationException("Unsealed actor scalar frame");
        if (slot < 0 || slot >= kinds.Length || kinds[slot] != expected)
            throw new InvalidOperationException("Actor scalar slot type/extent mismatch");
        return checked(frame.Offset + slot * 8);
    }

    private void ValidateFrame(ActorFrameHandle frame, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(frame.Owner, this) || frame.Index < 0 ||
            frame.Index >= _depth || !ReferenceEquals(_frames[frame.Index], frame))
            throw new InvalidOperationException("Foreign, stale or forged actor frame handle");
        if (offset < 0 || count < 0 || offset > frame.Length ||
            count > frame.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }

    private void ValidateTurn(ActorTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (!ReferenceEquals(turn.Owner, this) || !ReferenceEquals(turn, _activeTurn))
            throw new InvalidOperationException("Foreign, stale or forged actor turn");
    }

    private void EnsureActive(ActorTurn turn)
    {
        ValidateTurn(turn);
        EnsureRunning();
    }

    private void EnsureRunning()
    {
        if (_native == null || _stopping || _terminal)
            throw new InvalidOperationException("Actor arena is stopping or terminated");
    }

    private void TerminalFailure(string message)
    {
        _terminal = true;
        _stopping = true;
        throw new ActorResourceExhaustedException(message);
    }

    private void ReclaimIfQuiescent()
    {
        if (_activeTurn is not null || _native == null) return;
        CryptographicOperations.ZeroMemory(new Span<byte>(_native, _capacity));
        NativeMemory.Free(_native);
        _native = null;
        Array.Clear(_frames);
        _depth = 0;
        _top = 0;
        _tenant.ReleaseActor(this, _capacity);
    }
}

//// Only these built-in JSON codecs are explicitly denied. MessagePack,
//// protobuf, or any other transport must separately deny authority objects
//// at its broker boundary; these C# types are never guest-message payloads.
public sealed class ActorTurnNoWireConverter : JsonConverter<ActorTurn>
{
    public override ActorTurn Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Actor turn authority cannot be deserialized");
    public override void Write(
        Utf8JsonWriter writer, ActorTurn value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Actor turn authority cannot be serialized");
}

public sealed class ActorFrameNoWireConverter : JsonConverter<ActorFrameHandle>
{
    public override ActorFrameHandle Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Actor frame authority cannot be deserialized");
    public override void Write(
        Utf8JsonWriter writer, ActorFrameHandle value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Actor frame authority cannot be serialized");
}
