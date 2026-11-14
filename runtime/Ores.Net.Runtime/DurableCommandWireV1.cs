using System.Buffers.Binary;

namespace Ores.Net.Runtime;

// Host-only canonical event batch, not durable storage or a compiled guest.
// All payloads are copied to prohibit aliases across actor/worker boundaries.
public sealed class DurableCommandEventV1
{
    private readonly byte[] _payload;
    public uint SchemaVersion { get; }
    public byte[] Payload => (byte[])_payload.Clone();
    internal ReadOnlySpan<byte> OwnedPayload => _payload;

    public DurableCommandEventV1(uint schemaVersion, byte[] payload)
    {
        if (schemaVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > DurableCommandWireV1.MaxEventBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));
        SchemaVersion = schemaVersion;
        _payload = (byte[])payload.Clone();
    }
}

public sealed class DurableCommandEnvelopeV1
{
    private readonly byte[] _request;
    private readonly byte[] _reply;
    private readonly byte[] _batch;

    public byte[] Request => (byte[])_request.Clone();
    public byte[] Reply => (byte[])_reply.Clone();
    public byte[] Batch => (byte[])_batch.Clone();

    internal DurableCommandEnvelopeV1(byte[] request, byte[] reply, byte[] batch)
    {
        _request = (byte[])request.Clone();
        _reply = (byte[])reply.Clone();
        _batch = (byte[])batch.Clone();
    }
}

public static class DurableCommandWireV1
{
    public const int MaxEvents = 256;
    public const int MaxEventBytes = 65536;
    public const int MaxBatchBytes = 1048576;
    public const int MaxCommandBytes = 65536;
    public const int MaxReplyBytes = 65536;

    public static byte[] EncodeEventBatch(IReadOnlyList<DurableCommandEventV1> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count < 1 || events.Count > MaxEvents)
            throw new ArgumentOutOfRangeException(nameof(events));
        int total = 4;
        foreach (DurableCommandEventV1 evt in events)
        {
            ArgumentNullException.ThrowIfNull(evt);
            int len = evt.OwnedPayload.Length;
            if (evt.SchemaVersion == 0 || len > MaxEventBytes ||
                len + 8 > MaxBatchBytes - total)
                throw new ArgumentOutOfRangeException(nameof(events));
            total += len + 8;
        }
        byte[] output = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)events.Count);
        int pos = 4;
        foreach (DurableCommandEventV1 evt in events)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(pos, 4), evt.SchemaVersion);
            BinaryPrimitives.WriteUInt32BigEndian(
                output.AsSpan(pos + 4, 4), (uint)evt.OwnedPayload.Length);
            evt.OwnedPayload.CopyTo(output.AsSpan(pos + 8, evt.OwnedPayload.Length));
            pos += evt.OwnedPayload.Length + 8;
        }
        return output;
    }

    public static IReadOnlyList<DurableCommandEventV1> DecodeEventBatch(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || bytes.Length > MaxBatchBytes)
            throw new FormatException("DURABLE_COMMAND_WIRE_V1_SIZE");
        uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(0, 4));
        if (count < 1 || count > MaxEvents)
            throw new FormatException("DURABLE_COMMAND_WIRE_V1_COUNT");
        var output = new List<DurableCommandEventV1>((int)count);
        int pos = 4;
        for (uint i = 0; i < count; i++)
        {
            if (bytes.Length - pos < 8)
                throw new FormatException("DURABLE_COMMAND_WIRE_V1_TRUNCATED");
            uint schema = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos + 4, 4));
            pos += 8;
            if (schema == 0 || length > MaxEventBytes)
                throw new FormatException("DURABLE_COMMAND_WIRE_V1_SCHEMA_OR_LENGTH");
            if (length > (uint)(bytes.Length - pos))
                throw new FormatException("DURABLE_COMMAND_WIRE_V1_TRUNCATED");
            byte[] payload = bytes.Slice(pos, (int)length).ToArray();
            output.Add(new DurableCommandEventV1(schema, payload));
            pos += (int)length;
        }
        if (pos != bytes.Length)
            throw new FormatException("DURABLE_COMMAND_WIRE_V1_TRAILING_BYTES");
        return output;
    }

    public static DurableCommandEnvelopeV1 SnapshotCommand(
        ReadOnlySpan<byte> originalRequest,
        ReadOnlySpan<byte> originalReply,
        IReadOnlyList<DurableCommandEventV1> events)
    {
        if (originalRequest.Length > MaxCommandBytes ||
            originalReply.Length > MaxReplyBytes)
            throw new ArgumentOutOfRangeException(nameof(originalRequest));
        return new DurableCommandEnvelopeV1(
            originalRequest.ToArray(), originalReply.ToArray(), EncodeEventBatch(events));
    }
}
