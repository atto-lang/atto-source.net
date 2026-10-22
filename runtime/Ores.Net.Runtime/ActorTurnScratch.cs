using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ores.Net.Runtime;

/// <summary>One live serial-actor turn; stale after EndTurn.</summary>
[JsonConverter(typeof(ActorScratchTurnNoWireConverter))]
public readonly struct ActorScratchTurn
{
    internal Guid Arena { get; }
    internal ulong Epoch { get; }
    internal ActorScratchTurn(Guid arena, ulong epoch) { Arena = arena; Epoch = epoch; }
}

/// <summary>One non-owning slot in a serial actor's current scratch turn.</summary>
[JsonConverter(typeof(ActorScratchHandleNoWireConverter))]
public readonly struct ActorScratchHandle
{
    internal Guid Arena { get; }
    internal ulong Epoch { get; }
    internal int Offset { get; }
    internal int Length { get; }
    internal ActorScratchHandle(Guid arena, ulong epoch, int offset, int length)
    {
        Arena = arena; Epoch = epoch; Offset = offset; Length = length;
    }
}

public sealed class ActorScratchTurnNoWireConverter : JsonConverter<ActorScratchTurn>
{
    public override ActorScratchTurn Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException(
        "Actor scratch turn authority is not transferable");
    public override void Write(Utf8JsonWriter writer, ActorScratchTurn value,
        JsonSerializerOptions options) => throw new NotSupportedException(
        "Actor scratch turn authority is not transferable");
}

public sealed class ActorScratchHandleNoWireConverter : JsonConverter<ActorScratchHandle>
{
    public override ActorScratchHandle Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException(
        "Actor scratch memory is never a wire address");
    public override void Write(Utf8JsonWriter writer, ActorScratchHandle value,
        JsonSerializerOptions options) => throw new NotSupportedException(
        "Actor scratch memory is never a wire address");
}

/// <summary>
/// One fixed native scratch buffer for a SERIAL actor (not concurrent handlers).
/// The scheduler must begin/end turns only while holding exclusive actor
/// execution authority; no borrowed scratch address may cross await or FFI.
/// Persistent actor state and pinned continuation frames go in ActorNativeHeap.
///
/// The CLR traces only fixed control metadata; ordinary scratch allocation,
/// bounds-checked read/write and EndTurn create no managed payload objects.
/// This class is a host substrate, not source borrow-checker proof.
/// </summary>
public sealed unsafe class ActorTurnScratch : IDisposable
{
    public const int MaximumCapacity = 16 * 1024 * 1024;

    // Physical carrier authority: a copied actor ref/turn token alone is not
    // sufficient to access live scratch from another OS/CLR thread. This
    // intentionally does not flow through ExecutionContext or await.
    [ThreadStatic] private static ActorTurnScratch? s_currentCarrier;
    [ThreadStatic] private static ulong s_carrierEpoch;

    private readonly object _gate = new();
    private readonly ActorRef _owner;
    private readonly ActorNativeMemoryBudget? _budget;
    private readonly Guid _identity = Guid.NewGuid();
    private readonly int _capacity;
    private byte* _buffer;
    private int _used;
    private ulong _epoch;
    private bool _active;
    private bool _retired;

    public ActorTurnScratch(ActorRef owner, int capacity,
        ActorNativeMemoryBudget? sharedBudget = null)
    {
        if (owner.Id == 0 || owner.Generation == 0 || owner.Nonce == Guid.Empty)
            throw new ArgumentException("live actor authority required", nameof(owner));
        if (capacity <= 0 || capacity > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _owner = owner;
        _capacity = capacity;
        if (sharedBudget != null && !sharedBudget.TryReserve(capacity))
            throw new InvalidOperationException("actor scratch aggregate quota exceeded");
        try
        {
            _buffer = (byte*)NativeMemory.AllocZeroed((nuint)capacity);
            if (_buffer == null) throw new OutOfMemoryException("actor scratch allocation failed");
            _budget = sharedBudget;
        }
        catch
        {
            sharedBudget?.Release(capacity);
            throw;
        }
    }

    private void RequireOwner(ActorRef actor)
    {
        if (actor != _owner)
            throw new InvalidOperationException("foreign or stale scratch owner");
    }

    private bool ValidTurn(ActorScratchTurn turn)
        => !_retired && _active && turn.Arena == _identity && turn.Epoch == _epoch
            && ReferenceEquals(s_currentCarrier, this) && s_carrierEpoch == _epoch;

    private bool ValidHandle(ActorScratchTurn turn, ActorScratchHandle h)
        => ValidTurn(turn) && h.Arena == _identity && h.Epoch == _epoch &&
           h.Offset >= 0 && h.Length > 0 && h.Offset <= _used &&
           h.Length <= _used - h.Offset;

    public int Capacity => _capacity;
    public int UsedBytes { get { lock (_gate) return _used; } }
    public bool Retired { get { lock (_gate) return _retired; } }

    public ActorScratchTurn BeginTurn(ActorRef actor)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (_retired || _active || s_currentCarrier != null || _epoch == ulong.MaxValue)
                throw new InvalidOperationException(
                    "scratch turn is active, retired or epoch space exhausted");
            ++_epoch;
            _active = true;
            s_currentCarrier = this;
            s_carrierEpoch = _epoch;
            return new ActorScratchTurn(_identity, _epoch);
        }
    }

    public ActorScratchHandle? Allocate(ActorRef actor, ActorScratchTurn turn,
        int bytes, int alignment = 8)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (!ValidTurn(turn)) return null;
            if (bytes <= 0 || bytes > _capacity || alignment <= 0 ||
                alignment > 64 || (alignment & (alignment - 1)) != 0)
                return null;
            nuint mask = (nuint)(alignment - 1);
            nuint current = (nuint)(_buffer + _used);
            int padding = (int)(((nuint)alignment - (current & mask)) & mask);
            if (padding > _capacity - _used || bytes > _capacity - _used - padding)
                return null;
            int offset = _used + padding;
            _used = offset + bytes;
            return new ActorScratchHandle(_identity, _epoch, offset, bytes);
        }
    }

    public bool Write(ActorRef actor, ActorScratchTurn turn,
        ActorScratchHandle handle, ReadOnlySpan<byte> data, int offset = 0)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (!ValidHandle(turn, handle) || offset < 0 ||
                offset > handle.Length || data.Length > handle.Length - offset)
                return false;
            data.CopyTo(new Span<byte>(_buffer + handle.Offset + offset, data.Length));
            return true;
        }
    }

    public bool Read(ActorRef actor, ActorScratchTurn turn,
        ActorScratchHandle handle, Span<byte> result, int offset = 0)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (!ValidHandle(turn, handle) || offset < 0 ||
                offset > handle.Length || result.Length > handle.Length - offset)
                return false;
            new ReadOnlySpan<byte>(_buffer + handle.Offset + offset, result.Length)
                .CopyTo(result);
            return true;
        }
    }

    /// <summary>
    /// Called after all work for the serial turn has actually completed.
    /// It invalidates every scratch handle and scrubs used native bytes before
    /// reusing the region. Suspension/FFI retention must be rejected by the
    /// source borrow checker before any future scheduler integration.
    /// </summary>
    public void EndTurn(ActorRef actor, ActorScratchTurn turn)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (!ValidTurn(turn))
                throw new InvalidOperationException("foreign or stale scratch turn");
            CryptographicOperations.ZeroMemory(new Span<byte>(_buffer, _used));
            _used = 0;
            _active = false;
            s_carrierEpoch = 0;
            s_currentCarrier = null;
        }
    }

    public void Retire(ActorRef actor)
    {
        lock (_gate)
        {
            RequireOwner(actor);
            if (_retired) return;
            if (_active)
                throw new InvalidOperationException(
                    "cannot reclaim scratch bytes while a turn is active");
            CryptographicOperations.ZeroMemory(new Span<byte>(_buffer, _capacity));
            NativeMemory.Free(_buffer);
            _buffer = null;
            _retired = true;
            _budget?.Release(_capacity);
        }
    }

    public void Dispose() => Retire(_owner);
}
