using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ores.Net.Runtime;

/// <summary>
/// Opaque, generation-fenced, host-side I/O capability. Only the creator
/// of SpawnWithIo receives this port. It can feed io.in and drain io.out;
/// actor-side read/write is restricted to a live ActorContext turn.
/// This port never grants raw file/socket descriptors or mailbox authority.
/// </summary>
[JsonConverter(typeof(ActorIoHostPortNoWireConverter))]
public sealed class ActorIoHostPort
{
    private readonly ActorIoEnvelope _io;
    private readonly ActorRef _actor;
    // Weak ownership: a host-held port must not retain its retired runtime,
    // actor handler, arena or every sibling actor transitively.
    private readonly WeakReference<ActorRuntime>? _wakeRuntime;

    internal ActorIoHostPort(ActorIoEnvelope io, ActorRef actor,
        ActorRuntime? wakeRuntime = null)
    {
        _io = io;
        _actor = actor;
        if (wakeRuntime is not null)
            _wakeRuntime = new WeakReference<ActorRuntime>(wakeRuntime);
    }

    public bool TryFeedInput(ReadOnlySpan<byte> data)
    {
        bool admitted = _io.TryFeedInput(_actor, data);
        if (admitted) NotifyReadiness();
        return admitted;
    }

    private void NotifyReadiness()
    {
        if (_wakeRuntime?.TryGetTarget(out var runtime) == true)
            runtime.NotifyInputReady(_actor);
    }

    public bool TryReadOutput(out byte[]? data) =>
        _io.TryReadOutput(_actor, out data);

    // Only the host input writer can signal input EOF.
    public void CompleteInput()
    {
        if (_io.TryCompleteInput(_actor))
            NotifyReadiness();
    }

    // Output EOF becomes observable after every previously admitted frame
    // has been drained. The host cannot close the actor's output for it.
    public bool OutputEof => _io.OutputEof(_actor);
}

/// <summary>
/// Shared bounded queued-DATA credit for all actor I/O envelopes in one
/// runtime. Includes zero-byte records; not a cap on delivered caller-owned
/// memory, native actor arenas or non-queue metadata snapshots.
/// </summary>
internal sealed class ActorIoBudget
{
    private readonly object _gate = new();
    private readonly int _maxBytes;
    private readonly int _maxRecords;
    private int _queuedBytes;
    private int _queuedRecords;

    internal ActorIoBudget(int maxBytes, int maxRecords)
    {
        if (maxBytes <= 0 || maxBytes > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxRecords <= 0 || maxRecords > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maxRecords));
        _maxBytes = maxBytes;
        _maxRecords = maxRecords;
    }

    internal int QueuedBytes { get { lock (_gate) return _queuedBytes; } }
    internal int QueuedRecords { get { lock (_gate) return _queuedRecords; } }

    internal bool TryReserve(int bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            if (_queuedRecords >= _maxRecords ||
                bytes > _maxBytes - _queuedBytes) return false;
            ++_queuedRecords;
            _queuedBytes += bytes;
            return true;
        }
    }

    internal void Release(int bytes, int records = 1)
    {
        lock (_gate)
        {
            if (bytes < 0 || records < 0 || _queuedRecords < records ||
                _queuedBytes < bytes)
                throw new InvalidOperationException("actor I/O budget accounting corrupted");
            _queuedRecords -= records;
            _queuedBytes -= bytes;
        }
    }
}

/// <summary>
/// Actor I/O host ports are live local capabilities, never DTOs or wire
/// handles. A separate authenticated remote channel ABI must be explicit.
/// </summary>
public sealed class ActorIoHostPortNoWireConverter : JsonConverter<ActorIoHostPort>
{
    public override ActorIoHostPort Read(ref Utf8JsonReader reader,
        Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "Actor I/O host ports cannot be deserialized from JSON");

    public override void Write(Utf8JsonWriter writer, ActorIoHostPort value,
        JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "Actor I/O host ports cannot be serialized as JSON");
}

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
    private readonly ActorIoBudget? _budget;
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
    private bool _inputCompleted;
    private bool _outputCompleted;
    private ulong _metadataVersion = 1;
    private IReadOnlyDictionary<string, string> _metadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public ActorIoEnvelope(ActorMailbox mailbox) : this(mailbox, null) { }

    internal ActorIoEnvelope(ActorMailbox mailbox, ActorIoBudget? budget)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _budget = budget;
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
            return !_inputCompleted &&
                TryEnqueueBytes(_input, ref _inputBytes, data);
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
            return !_outputCompleted &&
                TryEnqueueBytes(_output, ref _outputBytes, data);
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

    /// <summary>
    /// Host-owned half-close for io.in. Existing queued bytes remain readable,
    /// but no further bytes can be admitted. Repeated EOF is idempotent.
    /// </summary>
    public void CompleteInput(ActorRef actor) => _ = TryCompleteInput(actor);

    // Returns false for an already-published EOF so a host cannot manufacture
    // unbounded scheduler wakeups by repeatedly closing the same input.
    internal bool TryCompleteInput(ActorRef actor)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            if (_inputCompleted) return false;
            _inputCompleted = true;
            return true;
        }
    }

    public bool InputEof(ActorRef actor)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            return _inputCompleted && _input.Count == 0;
        }
    }

    /// <summary>
    /// Actor-owned half-close for io.out. Admitted output remains drainable
    /// by the host until the record queue is empty.
    /// </summary>
    public void CompleteOutput(ActorRef actor)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            _outputCompleted = true;
        }
    }

    public bool OutputEof(ActorRef actor)
    {
        lock (_gate)
        {
            RequireCurrent(actor);
            return _outputCompleted && _output.Count == 0;
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
            int bytes = Utf8Bytes(diagnostic.Code) + Utf8Bytes(diagnostic.Message);
            _errorBytes -= bytes;
            _budget?.Release(bytes);
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
            int bytes = Utf8Bytes(@event.Kind) + Utf8Bytes(@event.Payload);
            _eventBytes -= bytes;
            _budget?.Release(bytes);
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
            _inputCompleted = true;
            _outputCompleted = true;
            int queued = _input.Count + _output.Count + _errors.Count + _events.Count;
            int bytes = checked(_inputBytes + _outputBytes + _errorBytes + _eventBytes);
            ScrubPending(_input);
            ScrubPending(_output);
            _errors.Clear();
            _events.Clear();
            _budget?.Release(bytes, queued);
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

    private bool TryEnqueueBytes(
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
        if (_budget is not null && !_budget.TryReserve(source.Length))
            return false;
        try
        {
            var owned = source.ToArray();
            queue.Enqueue(owned);
            queuedBytes += owned.Length;
            return true;
        }
        catch
        {
            _budget?.Release(source.Length);
            throw;
        }
    }

    private bool TryDequeueBytes(
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
        _budget?.Release(data.Length);
        return true;
    }

    private bool TryEnqueueRecord<T>(
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
        int charge = checked((int)bytes);
        if (_budget is not null && !_budget.TryReserve(charge))
            return false;
        try
        {
            queue.Enqueue(item);
            queuedBytes += charge;
            return true;
        }
        catch
        {
            _budget?.Release(charge);
            throw;
        }
    }

    private static int Utf8Bytes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encoding.UTF8.GetByteCount(value);
    }
}
