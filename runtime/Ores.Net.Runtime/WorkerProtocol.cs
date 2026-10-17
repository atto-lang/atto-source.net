using System.Buffers.Binary;
using System.Text;

namespace Ores.Net.Runtime;

public enum WorkerFrameKind : byte
{
    Ready = 1,
    Invoke = 2,
    Cancel = 3,
    Response = 4,
    Error = 5,
    Shutdown = 6
}

/// <summary>
/// Versioned, bounded stdio frame for a statically linked Oreslang NativeAOT worker.
/// V1 is intentionally UTF-8/string only. The current scalar .NET bootstrap accepts
/// an empty Invoke payload and returns its i64 result as invariant UTF-8 text.
/// </summary>
public sealed record WorkerFrame(
    WorkerFrameKind Kind,
    ulong RequestId,
    long DeadlineUnixMilliseconds,
    string Payload)
{
    public static WorkerFrame Ready() =>
        new(WorkerFrameKind.Ready, 0, 0, WorkerProtocol.ReadyPayload);

    public static WorkerFrame Invoke(
        ulong requestId,
        long deadlineUnixMilliseconds,
        string payload = "") =>
        new(WorkerFrameKind.Invoke, requestId, deadlineUnixMilliseconds, payload);

    public static WorkerFrame Cancel(ulong requestId) =>
        new(WorkerFrameKind.Cancel, requestId, 0, "");

    public static WorkerFrame Response(ulong requestId, string payload) =>
        new(WorkerFrameKind.Response, requestId, 0, payload);

    public static WorkerFrame Error(ulong requestId, string code) =>
        new(WorkerFrameKind.Error, requestId, 0, code);

    public static WorkerFrame Shutdown() =>
        new(WorkerFrameKind.Shutdown, 0, 0, "");
}

public static class WorkerProtocol
{
    public const uint Magic = 0x4f525731; // "ORW1"
    public const byte Version = 1;
    public const int HeaderBytes = 28;
    public const int MaxPayloadBytes = 1024 * 1024;
    public const int MaxInvocationMilliseconds = 30_000;
    public const string ReadyPayload = "oreslang-dotnet-worker/1";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void WriteFrame(Stream output, WorkerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(frame);
        Validate(frame);

        var payloadBytes = StrictUtf8.GetBytes(frame.Payload);
        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], Magic);
        header[4] = Version;
        header[5] = (byte)frame.Kind;
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(6, 2), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.Slice(8, 8), frame.RequestId);
        BinaryPrimitives.WriteInt64BigEndian(
            header.Slice(16, 8), frame.DeadlineUnixMilliseconds);
        BinaryPrimitives.WriteInt32BigEndian(
            header.Slice(24, 4), payloadBytes.Length);

        output.Write(header);
        if (payloadBytes.Length != 0)
            output.Write(payloadBytes);
        output.Flush();
    }

    /// <summary>
    /// Reads one frame. Returns null only for clean EOF before any header byte.
    /// Partial headers/payloads fail closed.
    /// </summary>
    public static WorkerFrame? ReadFrame(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        Span<byte> header = stackalloc byte[HeaderBytes];
        var first = input.ReadByte();
        if (first < 0) return null;
        header[0] = (byte)first;
        ReadExactly(input, header[1..]);

        if (BinaryPrimitives.ReadUInt32BigEndian(header[..4]) != Magic)
            throw new FormatException("worker frame magic mismatch");
        if (header[4] != Version)
            throw new FormatException("unsupported worker protocol version");
        if (BinaryPrimitives.ReadUInt16BigEndian(header.Slice(6, 2)) != 0)
            throw new FormatException("worker frame flags must be zero");

        var kindByte = header[5];
        if (kindByte < (byte)WorkerFrameKind.Ready ||
            kindByte > (byte)WorkerFrameKind.Shutdown)
            throw new FormatException("unknown worker frame kind");
        var kind = (WorkerFrameKind)kindByte;
        var requestId = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
        var deadline = BinaryPrimitives.ReadInt64BigEndian(header.Slice(16, 8));
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.Slice(24, 4));
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
            throw new FormatException("worker frame payload exceeds 1 MiB");

        var payloadBytes = new byte[payloadLength];
        if (payloadLength != 0)
            ReadExactly(input, payloadBytes);
        string payload;
        try
        {
            payload = StrictUtf8.GetString(payloadBytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("worker frame payload is not strict UTF-8", ex);
        }

        var frame = new WorkerFrame(kind, requestId, deadline, payload);
        Validate(frame);
        return frame;
    }

    public static string? ValidateDeadline(
        long deadlineUnixMilliseconds,
        long nowUnixMilliseconds)
    {
        if (deadlineUnixMilliseconds <= nowUnixMilliseconds)
            return "deadline-expired";
        if (deadlineUnixMilliseconds - nowUnixMilliseconds >
            MaxInvocationMilliseconds)
            return "deadline-exceeds-worker-limit";
        return null;
    }

    private static void Validate(WorkerFrame frame)
    {
        int payloadBytes;
        try
        {
            payloadBytes = StrictUtf8.GetByteCount(frame.Payload);
        }
        catch (EncoderFallbackException ex)
        {
            throw new FormatException("worker frame payload is not valid Unicode", ex);
        }
        if (payloadBytes > MaxPayloadBytes)
            throw new FormatException("worker frame payload exceeds 1 MiB");

        switch (frame.Kind)
        {
            case WorkerFrameKind.Ready:
                Require(frame.RequestId == 0 && frame.DeadlineUnixMilliseconds == 0,
                    "ready frame cannot carry request authority");
                Require(frame.Payload == ReadyPayload,
                    "ready frame payload mismatch");
                return;

            case WorkerFrameKind.Invoke:
                Require(frame.RequestId != 0, "invoke request id must be nonzero");
                Require(frame.DeadlineUnixMilliseconds > 0,
                    "invoke deadline must be positive");
                return;

            case WorkerFrameKind.Cancel:
                Require(frame.RequestId != 0, "cancel request id must be nonzero");
                Require(frame.DeadlineUnixMilliseconds == 0 && frame.Payload.Length == 0,
                    "cancel frame must not carry deadline or payload");
                return;

            case WorkerFrameKind.Response:
            case WorkerFrameKind.Error:
                Require(frame.RequestId != 0, "response request id must be nonzero");
                Require(frame.DeadlineUnixMilliseconds == 0,
                    "response frame cannot carry deadline");
                return;

            case WorkerFrameKind.Shutdown:
                Require(frame.RequestId == 0 && frame.DeadlineUnixMilliseconds == 0 &&
                    frame.Payload.Length == 0,
                    "shutdown frame must be empty");
                return;

            default:
                throw new FormatException("unknown worker frame kind");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new FormatException(message);
    }

    private static void ReadExactly(Stream input, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = input.Read(destination[read..]);
            if (n == 0)
                throw new EndOfStreamException("truncated worker frame");
            read += n;
        }
    }

    private static void ReadExactly(Stream input, byte[] destination) =>
        ReadExactly(input, destination.AsSpan());
}
