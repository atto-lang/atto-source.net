using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ores.Net.Runtime;

/// <summary>
/// Actor-private bounded byte transport prototype. No closures, methods, pointers,
/// reflection-created types, or mutable objects cross a mailbox boundary.
/// </summary>
// This is a LOCAL mailbox capability, not a transferable wire-format token.
// The private random value prevents another mailbox with the same numeric ID
// and generation from accidentally satisfying capability checks.
[JsonConverter(typeof(ActorRefNoWireConverter))]
public readonly record struct ActorRef
{
    public ulong Id { get; }
    public ulong Generation { get; }
    internal Guid Nonce { get; }

    internal ActorRef(ulong id, ulong generation, Guid nonce)
    {
        Id = id;
        Generation = generation;
        Nonce = nonce;
    }

    // Never log the bearer nonce in generated record ToString output.
    public override string ToString() => $"ActorRef(Id={Id}, Generation={Generation})";
}

/// <summary>Explicitly deny using a local actor authority as JSON user data.
/// A wire address must be represented by a separate data-only schema and authenticated bridge.
/// </summary>
public sealed class ActorRefNoWireConverter : JsonConverter<ActorRef>
{
    public override ActorRef Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException(
        "ActorRef is local runtime authority and cannot be deserialized from JSON");

    public override void Write(Utf8JsonWriter writer, ActorRef value,
        JsonSerializerOptions options) => throw new NotSupportedException(
        "ActorRef is local runtime authority and cannot be serialized as JSON data");
}

public sealed class ActorMailbox : IDisposable
{
    public const int MaxMessageBytes = 1024 * 1024;
    public const int MaxMailboxMessages = 4096;
    public const int MaxMailboxBytes = 16 * MaxMessageBytes;

    private readonly object _gate = new();
    private readonly Queue<byte[]> _messages = new();
    private readonly ulong _id;
    private readonly int _maxMessages;
    private readonly int _maxQueuedBytes;
    private ulong _generation = 1;
    private Guid _nonce;
    private Func<Guid> _nextNonce = Guid.NewGuid;
    private int _queuedBytes;
    private bool _closed;
    // Closing new DATA admission must NOT discard already accepted work.
    // Unlike Stop, draining keeps I/O retirement callbacks alive until the
    // final physical turn has completed. This is host substrate only.
    private bool _draining;
    private bool _disposed;
    // Host-owned logical actor I/O envelopes are revoked when this mailbox
    // generation stops. Callbacks run OUTSIDE _gate to avoid the lock order
    // inversion (I/O gate -> mailbox gate) used by envelope reads/writes.
    private readonly List<Action> _ioRetireCallbacks = new();
    private const int MaxIoRetireCallbacks = 64;

    public ActorMailbox(ulong id, int maxMessages = 1024, int maxQueuedBytes = 4 * MaxMessageBytes)
    {
        if (id == 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Actor ID must be positive");
        if (maxMessages <= 0 || maxMessages > MaxMailboxMessages)
            throw new ArgumentOutOfRangeException(nameof(maxMessages), "Message capacity must be positive");
        if (maxQueuedBytes <= 0 || maxQueuedBytes > MaxMailboxBytes)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes), "Byte capacity must be positive");
        _id = id;
        _maxMessages = maxMessages;
        _maxQueuedBytes = maxQueuedBytes;
        // Mint runtime authority only after rejecting invalid capacities.
        _nonce = _nextNonce();
        if (_nonce == Guid.Empty)
            throw new InvalidOperationException("Actor mailbox capability must not be empty");
    }

    public ActorRef CurrentRef
    {
        get { lock (_gate) return new ActorRef(_id, _generation, _nonce); }
    }

    public int QueuedMessages
    {
        get { lock (_gate) return _messages.Count; }
    }

    internal bool Authorizes(ActorRef destination)
    {
        lock (_gate)
        {
            return !_closed && !_draining &&
                destination.Id == _id &&
                destination.Generation == _generation &&
                destination.Nonce == _nonce;
        }
    }

    internal void RegisterIoRetirement(ActorRef generation, Action retire)
    {
        ArgumentNullException.ThrowIfNull(retire);
        lock (_gate)
        {
            if (_disposed || _closed || generation.Id != _id ||
                generation.Generation != _generation || generation.Nonce != _nonce)
                throw new InvalidOperationException("Actor I/O cannot bind a stale or stopped mailbox");
            if (_ioRetireCallbacks.Count >= MaxIoRetireCallbacks)
                throw new InvalidOperationException("Actor I/O envelope registration quota exceeded");
            _ioRetireCallbacks.Add(retire);
        }
    }

    // Call only while _gate is held; invocation MUST happen after the lock.
    private Action[] DetachIoRetireCallbacksLocked()
    {
        Action[] callbacks = _ioRetireCallbacks.ToArray();
        _ioRetireCallbacks.Clear();
        return callbacks;
    }

    private static void RetireIo(Action[] callbacks)
    {
        foreach (Action callback in callbacks)
            callback();
    }

    // Runtime callers may check admission before starting a worker. The
    // actual Send checks again under this gate before publishing any bytes.
    internal void ValidateAdmission(ActorRef destination, int length)
    {
        lock (_gate)
            ValidateSendLocked(destination, length);
    }

    public void Send(ActorRef destination, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            ValidateSendLocked(destination, data.Length);
            var ownedCopy = data.ToArray();
            _messages.Enqueue(ownedCopy);
            _queuedBytes += ownedCopy.Length;
        }
    }

    private void ValidateSendLocked(ActorRef destination, int length)
    {
        if (_closed || _draining || destination.Id != _id ||
            destination.Generation != _generation || destination.Nonce != _nonce)
            throw new InvalidOperationException("Actor reference is stale or mailbox is closed");

        // Match Forge: check receiver authority before disclosing payload sizes.
        if (length > MaxMessageBytes)
            throw new ArgumentOutOfRangeException("data", "Message exceeds 1 MiB");
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (_messages.Count >= _maxMessages || _queuedBytes > _maxQueuedBytes ||
            length > _maxQueuedBytes - _queuedBytes)
            throw new InvalidOperationException("Mailbox capacity exceeded");
    }

    /// <summary>
    /// Generation-pinned close of incoming DATA admission, without erasing
    /// already accepted DATA. Only the owner scheduler may subsequently
    /// consume queued messages. Stop/Dispose remain abortive and scrub bytes.
    /// Does not signal termination or promise guest actor graph semantics.
    /// </summary>
    public bool TryBeginDrain(ActorRef expected)
    {
        lock (_gate)
        {
            if (_disposed || _closed || _draining || expected.Id != _id ||
                expected.Generation != _generation || expected.Nonce != _nonce)
                return false;
            _draining = true;
            return true;
        }
    }

    public bool TryReceive(out byte[]? payload)
    {
        lock (_gate)
        {
            if (_messages.Count == 0)
            {
                payload = null;
                return false;
            }
            payload = _messages.Dequeue();
            _queuedBytes -= payload.Length;
            return true;
        }
    }

    /// <summary>
    /// AF07 generation- and nonce-pinned mailbox stop. A stale or foreign
    /// local ActorRef cannot erase queued bytes or close a replacement
    /// incarnation. This is host-runtime authority, not guest actor lowering.
    /// </summary>
    public bool TryStop(ActorRef expected)
    {
        Action[] callbacks;
        lock (_gate)
        {
            if (expected.Id != _id || expected.Generation != _generation ||
                expected.Nonce != _nonce)
                return false;
            _closed = true;
            DiscardPending();
            _queuedBytes = 0;
            callbacks = DetachIoRetireCallbacksLocked();
        }
        RetireIo(callbacks);
        return true; // Repeated stop of this incarnation is idempotent.
    }

    public void Stop()
    {
        Action[] callbacks;
        lock (_gate)
        {
            _closed = true;
            DiscardPending();
            _queuedBytes = 0;
            callbacks = DetachIoRetireCallbacksLocked();
        }
        RetireIo(callbacks);
    }

    // Called only while holding _gate. The sender's input was copied on
    // admission, so zeroing pending envelopes never mutates sender buffers.
    // Delivered arrays have been transferred to callers and are not ours to erase.
    private void DiscardPending()
    {
        foreach (var envelope in _messages)
            CryptographicOperations.ZeroMemory(envelope);
        _messages.Clear();
    }

    /// <summary>
    /// Permanently retire mailbox authority and erase pending owned messages.
    /// Unlike Stop(), Dispose() must never allow a later Restart().
    /// </summary>
    public void Dispose()
    {
        Action[] callbacks;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _closed = true;
            DiscardPending();
            _queuedBytes = 0;
            callbacks = DetachIoRetireCallbacksLocked();
        }
        RetireIo(callbacks);
    }

    /// <summary>Invalidate old references; local numeric IDs are not secure distributed capabilities.</summary>
    public ActorRef Restart()
    {
        Action[] callbacks;
        ActorRef fresh;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ActorMailbox),
                    "Disposed mailbox authority cannot be restarted");
            if (_generation == ulong.MaxValue)
                throw new OverflowException("Actor generation exhausted");
            var nextNonce = _nextNonce();
            if (nextNonce == Guid.Empty || nextNonce == _nonce)
                throw new InvalidOperationException("Restart requires fresh actor authority");
            _generation++;
            _nonce = nextNonce;
            _closed = false;
            _draining = false;
            DiscardPending();
            _queuedBytes = 0;
            callbacks = DetachIoRetireCallbacksLocked();
            fresh = new ActorRef(_id, _generation, _nonce);
        }
        RetireIo(callbacks);
        return fresh;
    }
}

