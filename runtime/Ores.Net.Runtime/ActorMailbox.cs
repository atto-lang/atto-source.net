namespace Ores.Net.Runtime;

/// <summary>
/// Actor-private bounded byte transport prototype. No closures, methods, pointers,
/// reflection-created types, or mutable objects cross a mailbox boundary.
/// </summary>
public readonly record struct ActorRef(ulong Id, ulong Generation);

public sealed class ActorMailbox
{
    public const int MaxMessageBytes = 1024 * 1024;

    private readonly object _gate = new();
    private readonly Queue<byte[]> _messages = new();
    private readonly ulong _id;
    private readonly int _maxMessages;
    private readonly int _maxQueuedBytes;
    private ulong _generation = 1;
    private int _queuedBytes;
    private bool _closed;

    public ActorMailbox(ulong id, int maxMessages = 1024, int maxQueuedBytes = 4 * MaxMessageBytes)
    {
        if (id == 0 || maxMessages <= 0 || maxQueuedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Actor ID and queue limits must be positive");
        _id = id;
        _maxMessages = maxMessages;
        _maxQueuedBytes = maxQueuedBytes;
    }

    public ActorRef CurrentRef
    {
        get { lock (_gate) return new ActorRef(_id, _generation); }
    }

    public int QueuedMessages
    {
        get { lock (_gate) return _messages.Count; }
    }

    public void Send(ActorRef destination, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(data), "Message exceeds 1 MiB");
        lock (_gate)
        {
            if (_closed || destination.Id != _id || destination.Generation != _generation)
                throw new InvalidOperationException("Actor reference is stale or mailbox is closed");
            if (_messages.Count >= _maxMessages || data.Length > _maxQueuedBytes - _queuedBytes)
                throw new InvalidOperationException("Mailbox capacity exceeded");
            var ownedCopy = data.ToArray();
            _messages.Enqueue(ownedCopy);
            _queuedBytes += ownedCopy.Length;
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

    public void Stop()
    {
        lock (_gate)
        {
            _closed = true;
            _messages.Clear();
            _queuedBytes = 0;
        }
    }

    /// <summary>Invalidate old references; local numeric IDs are not secure distributed capabilities.</summary>
    public ActorRef Restart()
    {
        lock (_gate)
        {
            if (_generation == ulong.MaxValue)
                throw new OverflowException("Actor generation exhausted");
            _generation++;
            _closed = false;
            _messages.Clear();
            _queuedBytes = 0;
            return new ActorRef(_id, _generation);
        }
    }
}
