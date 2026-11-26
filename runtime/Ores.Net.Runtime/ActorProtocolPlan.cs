using System.Buffers.Binary;

namespace Ores.Net.Runtime;

public enum ProtocolTurnStatus : byte
{
    Committed = 1,
    IllegalEdge,
    InvalidPayload,
    InvalidEnvelope,
    Interrupted,
    InvariantFailed,
}

public readonly record struct ProtocolTurnReceipt(
    ulong ActorId, ulong Generation, ulong Turn, ProtocolTurnStatus Status,
    ushort Source, ushort Destination)
{
    internal const int FrameSize = 34;

    internal void Write(Span<byte> frame)
    {
        "ATPR\x01"u8.CopyTo(frame);
        BinaryPrimitives.WriteUInt64LittleEndian(frame[5..], ActorId);
        BinaryPrimitives.WriteUInt64LittleEndian(frame[13..], Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(frame[21..], Turn);
        frame[29] = (byte)Status;
        BinaryPrimitives.WriteUInt16LittleEndian(frame[30..], Source);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[32..], Destination);
    }

    public static ProtocolTurnReceipt Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameSize || !frame[..5].SequenceEqual("ATPR\x01"u8) ||
            frame[29] is < (byte)ProtocolTurnStatus.Committed or > (byte)ProtocolTurnStatus.InvariantFailed)
            throw new FormatException("Invalid protocol turn receipt");
        return new ProtocolTurnReceipt(
            BinaryPrimitives.ReadUInt64LittleEndian(frame[5..]),
            BinaryPrimitives.ReadUInt64LittleEndian(frame[13..]),
            BinaryPrimitives.ReadUInt64LittleEndian(frame[21..]),
            (ProtocolTurnStatus)frame[29],
            BinaryPrimitives.ReadUInt16LittleEndian(frame[30..]),
            BinaryPrimitives.ReadUInt16LittleEndian(frame[32..]));
    }
}

public readonly record struct ProtocolPayloadField(long Minimum, long Maximum);
public readonly record struct ProtocolMailboxEdge(ushort Source, ushort Message, ushort Destination);
public readonly record struct ProtocolStateField(long Initial, long Minimum, long Maximum);
internal readonly record struct ProtocolPublishedState(ushort State, long Value);

/// <summary>Static transition-only DATA binding used by compiler-generated actor classes.</summary>
public sealed class ActorProtocolPlan
{
    private const int HeaderSize = 39; // magic/version, schema fingerprint, message ordinal
    private readonly byte[] _schema;
    private readonly ProtocolPublishedState _initial;
    private readonly ProtocolStateField? _field;
    private readonly int[]? _increments;
    private readonly ProtocolPayloadField[][] _messages;
    private readonly Dictionary<(ushort State, ushort Message), ushort> _edges = new();

    public ActorProtocolPlan(string schema, int stateCount, ushort initial,
        ProtocolPayloadField[][] messages, ProtocolMailboxEdge[] edges)
        : this(schema, stateCount, initial, messages, edges, null, null) { }

    public ActorProtocolPlan(string schema, int stateCount, ushort initial,
        ProtocolPayloadField[][] messages, ProtocolMailboxEdge[] edges,
        ProtocolStateField? field, int[]? increments)
    {
        _schema = Convert.FromHexString(schema);
        if (_schema.Length != 32 || stateCount is < 1 or > 128 || initial >= stateCount ||
            messages.Length > 256 || edges.Length > 256)
            throw new ArgumentException("Invalid compiled protocol table");
        if ((field is null) != (increments is null) ||
            (field is { } owned && (owned.Initial < owned.Minimum || owned.Initial > owned.Maximum ||
             increments!.Length != messages.Length)))
            throw new ArgumentException("Invalid compiled protocol state");
        _field = field;
        _increments = increments is null ? null : (int[])increments.Clone();
        _initial = new(initial, field?.Initial ?? 0);
        _messages = new ProtocolPayloadField[messages.Length][];
        for (int i = 0; i < messages.Length; i++)
        {
            var fields = messages[i];
            if (fields.Length > 16 || (_increments is not null &&
                (_increments[i] < -1 || _increments[i] >= fields.Length)))
                throw new ArgumentException("Invalid compiled protocol payload schema");
            _messages[i] = (ProtocolPayloadField[])fields.Clone();
            if (_messages[i].Any(field => field.Minimum > field.Maximum))
                throw new ArgumentException("Invalid compiled protocol payload schema");
        }
        foreach (var edge in edges)
        {
            if (edge.Source >= stateCount || edge.Destination >= stateCount ||
                edge.Message >= messages.Length || !_edges.TryAdd((edge.Source, edge.Message), edge.Destination))
                throw new ArgumentException("Invalid or duplicate compiled protocol edge");
        }
    }

    // This is a host ingress API. The generated method fixes the message
    // ordinal and each field's compiler-checked carrier and scope.
    public void Send(ActorRuntime runtime, ActorRef actor, ushort message, ReadOnlySpan<long> fields)
    {
        if (message >= _messages.Length || fields.Length != _messages[message].Length)
            throw new ArgumentException("Unexpected protocol message shape");
        Span<byte> frame = stackalloc byte[HeaderSize + sizeof(long) * fields.Length];
        "ATPM\x01"u8.CopyTo(frame);
        _schema.CopyTo(frame[5..]);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[37..], message);
        for (int i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(frame[(HeaderSize + sizeof(long) * i)..], fields[i]);
        // Route through the existing reviewed in-memory mailbox admission.
        runtime.PostHostMessage(actor, frame);
    }

    public ActorRef Spawn(ActorRuntime runtime, ActorRef replyTo)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _ = runtime.ObserveTermination(replyTo);
        var state = _initial;
        return runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            ushort source = state.State;
            ProtocolTurnStatus status = Dispatch(runtime, context, message.Span, ref state);
            var receipt = new ProtocolTurnReceipt(context.Self.Id, context.Self.Generation,
                context.Execution.ExecutionSequence, status, source, state.State);
            Span<byte> output = stackalloc byte[ProtocolTurnReceipt.FrameSize];
            receipt.Write(output);
            runtime.PostHostMessage(replyTo, output);
        });
    }

    private ProtocolTurnStatus Dispatch(ActorRuntime runtime, ActorContext context, ReadOnlySpan<byte> frame,
        ref ProtocolPublishedState state)
    {
        if (frame.Length < HeaderSize || !frame[..5].SequenceEqual("ATPM\x01"u8) ||
            !frame.Slice(5, 32).SequenceEqual(_schema))
            return ProtocolTurnStatus.InvalidEnvelope;
        ushort message = BinaryPrimitives.ReadUInt16LittleEndian(frame[37..]);
        if (message >= _messages.Length)
            return ProtocolTurnStatus.InvalidEnvelope;
        if (!_edges.TryGetValue((state.State, message), out ushort destination))
            return ProtocolTurnStatus.IllegalEdge;
        var fields = _messages[message];
        if (frame.Length != HeaderSize + sizeof(long) * fields.Length)
            return ProtocolTurnStatus.InvalidPayload;
        for (int i = 0; i < fields.Length; i++)
        {
            long value = BinaryPrimitives.ReadInt64LittleEndian(frame[(HeaderSize + sizeof(long) * i)..]);
            if (value < fields[i].Minimum || value > fields[i].Maximum)
                return ProtocolTurnStatus.InvalidPayload;
        }
        long staged = state.Value;
        if (_increments is not null && _increments[message] is int increment && increment >= 0)
        {
            try
            {
                staged = checked(staged + BinaryPrimitives.ReadInt64LittleEndian(
                    frame[(HeaderSize + sizeof(long) * increment)..]));
            }
            catch (OverflowException)
            {
                return ProtocolTurnStatus.InvariantFailed;
            }
            var field = _field!.Value;
            if (staged < field.Minimum || staged > field.Maximum) return ProtocolTurnStatus.InvariantFailed;
        }
        return runtime.TryPublishProtocolState(context, ref state, new(destination, staged))
            ? ProtocolTurnStatus.Committed : ProtocolTurnStatus.Interrupted;
    }
}
