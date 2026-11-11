using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Ores.Net.Runtime;

public readonly record struct ActorIoDiagnostic(string Code, string Message);
public readonly record struct ActorIoEvent(string Kind, string Payload);

public sealed record ActorMetadataSnapshot(
    ulong Version,
    IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Generation-fenced logical actor I/O substrate.
///
/// This object owns no OS descriptors and never aliases process stdin/stdout/stderr.
/// It is a bounded in-memory transport intended for the future source-level
/// io.in/io.out/io.err/io.events/io.meta lowering.
/// </summary>
public sealed class ActorIoEnvelope : IDisposable
{
    public const int MaxRecordsPerChannel = 1024;
    public const int MaxBytesPerChannel = 4 * 1024 * 1024;
    public const int MaxMetadataEntries = 64;
    public const int MaxMetadataBytes = 8 * 1024;

    private readonly ActorMailbox _mailbox;
    private readonly ActorRef _boundActor;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _input = new();
    private readonly Queue<byte[]> _output = new();
    private readonly Queue<ActorIoDiagnostic> _errors = new();
    private readonly Queue<ActorIoEvent> _events = new();
    private int _inputBytes;
    private int _outputBytes;
    private int _errorBytes;
    private int _eventBytes;
    private bool _disposed;
    private ulong _metadataVersion = 1;
    private IReadOnlyDictionary<string, string> _metadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public ActorIoEnvelope(ActorMailbox mailbox)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _boundActor = _mailbox.CurrentRef;
        if (!_mailbox.Authorizes(_boundActor))
            throw new InvalidOperationException(
                "Actor I/O envelope requires a live actor generation");
    }

    public bool TryFeedInput(ActorRef actor, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            return TryEnqueueBytes(_input, ref _inputBytes, data);
        }
    }

    public bool TryReadInput(ActorRef actor, out byte[]? data)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            return TryDequeueBytes(_input, ref _inputBytes, out data);
        }
    }

    public bool TryWriteOutput(ActorRef actor, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            return TryEnqueueBytes(_output, ref _outputBytes, data);
        }
    }

    public bool TryReadOutput(ActorRef actor, out byte[]? data)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            return TryDequeueBytes(_output, ref _outputBytes, out data);
        }
    }

    public bool TryEmitError(ActorRef actor, ActorIoDiagnostic diagnostic)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            long bytes = (long)Utf8Bytes(diagnostic.Code) + Utf8Bytes(diagnostic.Message);
            return TryEnqueueRecord(_errors, ref _errorBytes, diagnostic, bytes);
        }
    }

    public bool TryReadError(ActorRef actor, out ActorIoDiagnostic diagnostic)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            if (_errors.Count == 0)
            {
                diagnostic = default;
                return false;
            }
            diagnostic = _errors.Dequeue();
            _errorBytes -= Utf8Bytes(diagnostic.Code) + Utf8Bytes(diagnostic.Message);
            return true;
        }
    }

    public bool TryEmitEvent(ActorRef actor, ActorIoEvent @event)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            long bytes = (long)Utf8Bytes(@event.Kind) + Utf8Bytes(@event.Payload);
            return TryEnqueueRecord(_events, ref _eventBytes, @event, bytes);
        }
    }

    public bool TryReadEvent(ActorRef actor, out ActorIoEvent @event)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            if (_events.Count == 0)
            {
                @event = default;
                return false;
            }
            @event = _events.Dequeue();
            _eventBytes -= Utf8Bytes(@event.Kind) + Utf8Bytes(@event.Payload);
            return true;
        }
    }

    public ActorMetadataSnapshot Metadata(ActorRef actor)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            return new ActorMetadataSnapshot(_metadataVersion, _metadata);
        }
    }

    public ActorMetadataSnapshot ReplaceMetadata(
        ActorRef actor,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_gate)
        {
            RequireCurrent(actor);
            if (values.Count > MaxMetadataEntries)
                throw new ArgumentOutOfRangeException(nameof(values),
                    "Actor metadata entry limit exceeded");

            long bytes = 0;
            var owned = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in values)
            {
                if (string.IsNullOrEmpty(pair.Key))
                    throw new ArgumentException("Actor metadata keys must be nonempty", nameof(values));
                long entryBytes = (long)Utf8Bytes(pair.Key) + Utf8Bytes(pair.Value);
                if (entryBytes > MaxMetadataBytes || bytes > MaxMetadataBytes - entryBytes)
                    throw new ArgumentOutOfRangeException(nameof(values),
                        "Actor metadata byte limit exceeded");
                if (!owned.TryAdd(pair.Key, pair.Value))
                    throw new ArgumentException("Duplicate actor metadata key", nameof(values));
                bytes += entryBytes;
            }
            if (_metadataVersion == ulong.MaxValue)
                throw new OverflowException("Actor metadata version exhausted");
            _metadataVersion++;
            _metadata = new ReadOnlyDictionary<string, string>(owned);
            return new ActorMetadataSnapshot(_metadataVersion, _metadata);
        }
    }

    private void RequireCurrent(ActorRef actor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // An envelope belongs to exactly one actor incarnation. Even a fresh,
        // valid ActorRef after mailbox restart cannot reopen records retained
        // by an older generation; the supervisor must construct a new envelope.
        if (actor != _boundActor || !_mailbox.Authorizes(actor))
            throw new InvalidOperationException(
                "Actor I/O capability is stale, foreign, retired, or belongs to another generation");
    }

    /// <summary>
    /// Permanently retire this generation's actor I/O queues. Pending owned
    /// byte frames are scrubbed before releasing their memory. Buffers already
    /// delivered to a caller are owned by that caller and are never modified.
    ///
    /// Stop/restart of the mailbox fences stale calls; the supervisor must
    /// also Dispose its I/O envelope during physical actor retirement.
    /// Managed diagnostic/event strings and external metadata snapshots cannot
    /// be guaranteed physically zeroized by the CLR.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ScrubPending(_input);
            ScrubPending(_output);
            _errors.Clear();
            _events.Clear();
            _inputBytes = 0;
            _outputBytes = 0;
            _errorBytes = 0;
            _eventBytes = 0;
            _metadata = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    private static void ScrubPending(Queue<byte[]> queue)
    {
        foreach (var record in queue)
            CryptographicOperations.ZeroMemory(record);
        queue.Clear();
    }

    private static bool TryEnqueueBytes(
        Queue<byte[]> queue,
        ref int queuedBytes,
        ReadOnlySpan<byte> source)
    {
        if (source.Length > MaxBytesPerChannel)
            return false;
        if (queue.Count >= MaxRecordsPerChannel ||
            queuedBytes > MaxBytesPerChannel ||
            source.Length > MaxBytesPerChannel - queuedBytes)
            return false;
        var owned = source.ToArray();
        queue.Enqueue(owned);
        queuedBytes += owned.Length;
        return true;
    }

    private static bool TryDequeueBytes(
        Queue<byte[]> queue,
        ref int queuedBytes,
        out byte[]? data)
    {
        if (queue.Count == 0)
        {
            data = null;
            return false;
        }
        data = queue.Dequeue();
        queuedBytes -= data.Length;
        return true;
    }

    private static bool TryEnqueueRecord<T>(
        Queue<T> queue,
        ref int queuedBytes,
        T item,
        long bytes)
    {
        if (bytes < 0 || bytes > MaxBytesPerChannel)
            return false;
        if (queue.Count >= MaxRecordsPerChannel ||
            queuedBytes > MaxBytesPerChannel ||
            bytes > MaxBytesPerChannel - queuedBytes)
            return false;
        queue.Enqueue(item);
        queuedBytes += checked((int)bytes);
        return true;
    }

    private static int Utf8Bytes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encoding.UTF8.GetByteCount(value);
    }
}
