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
    // Control ticks must never be confused with user-supplied zero-byte
    // messages. The envelope kind is runtime-owned and not on the wire.
    private sealed record Envelope(byte[] Payload, bool IsControl);
    private readonly Queue<Envelope> _messages = new();
    private readonly ulong _id;
    private readonly int _maxMessages;
    private readonly int _maxQueuedBytes;
    private ulong _generation = 1;
    private Guid _nonce;
    private Func<Guid> _nextNonce = Guid.NewGuid;
    private int _queuedBytes;
    private bool _closed;
    private bool _disposed;

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
            return !_closed &&
                destination.Id == _id &&
                destination.Generation == _generation &&
                destination.Nonce == _nonce;
        }
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
            _messages.Enqueue(new Envelope(ownedCopy, IsControl: false));
            _queuedBytes += ownedCopy.Length;
        }
    }

    /// <summary>
    /// Only the trusted in-assembly scheduler may enqueue dispatch control.
    /// An empty user Send is always USER DATA, never scheduler authority.
    /// Control tokens participate in the same bounded FIFO message quota.
    /// </summary>
    internal void EnqueueTrustedDispatch(ActorRef destination)
    {
        lock (_gate)
        {
            ValidateSendLocked(destination, 0);
            _messages.Enqueue(new Envelope(Array.Empty<byte>(), IsControl: true));
        }
    }

    private void ValidateSendLocked(ActorRef destination, int length)
    {
        if (_closed || destination.Id != _id ||
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

    public bool TryReceive(out byte[]? payload)
    {
        lock (_gate)
        {
            if (_messages.Count == 0)
            {
                payload = null;
                return false;
            }
            if (_messages.Peek().IsControl)
            {
                // Generic receive cannot steal an internal scheduler receipt.
                payload = null;
                return false;
            }
            payload = _messages.Dequeue().Payload;
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
        lock (_gate)
        {
            if (expected.Id != _id || expected.Generation != _generation ||
                expected.Nonce != _nonce)
                return false;
            _closed = true;
            DiscardPending();
            _queuedBytes = 0;
            return true; // Repeated stop of the same incarnation is idempotent.
        }
    }

    /// <summary>
    /// One FIFO-backed synchronous actor tick. Only an admitted empty
    /// control envelope can schedule the narrow generated Counter method.
    /// A nonempty user message is never removed, consumed or reclassified
    /// as an actor tick. Called only under the owning host admission lock.
    /// </summary>
    internal bool TryReceiveEmptyDispatch()
    {
        lock (_gate)
        {
            if (_closed || _messages.Count == 0 ||
                !_messages.Peek().IsControl)
                return false;
            _ = _messages.Dequeue(); // Empty envelope: no user bytes to leak.
            return true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _closed = true;
            DiscardPending();
            _queuedBytes = 0;
        }
    }

    // Called only while holding _gate. The sender's input was copied on
    // admission, so zeroing pending envelopes never mutates sender buffers.
    // Delivered arrays have been transferred to callers and are not ours to erase.
    private void DiscardPending()
    {
        foreach (var envelope in _messages)
            CryptographicOperations.ZeroMemory(envelope.Payload);
        _messages.Clear();
    }

    /// <summary>
    /// Permanently retire mailbox authority and erase pending owned messages.
    /// Unlike Stop(), Dispose() must never allow a later Restart().
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _closed = true;
            DiscardPending();
            _queuedBytes = 0;
        }
    }

    /// <summary>Invalidate old references; local numeric IDs are not secure distributed capabilities.</summary>
    public ActorRef Restart()
    {
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
            DiscardPending();
            _queuedBytes = 0;
            return new ActorRef(_id, _generation, _nonce);
        }
    }
}

