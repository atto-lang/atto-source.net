using System.Text;

namespace Ores.Net.Runtime;

internal sealed class GuestOutputLimitException : Exception
{
    public GuestOutputLimitException()
        : base("guest output exceeded worker byte limit")
    {
    }
}

/// <summary>
/// Counts guest console text before forwarding it to the worker log channel.
/// Protocol stdout never flows through this writer.
/// </summary>
internal sealed class BoundedGuestTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly int _maxBytes;
    private readonly object _gate = new();
    private int _writtenBytes;

    public BoundedGuestTextWriter(TextWriter inner, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxBytes < 1 || maxBytes > WorkerProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _inner = inner;
        _maxBytes = maxBytes;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        Span<char> chars = stackalloc char[1];
        chars[0] = value;
        WriteBounded(chars);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        WriteBounded(value.AsSpan());
    }

    public override void Write(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (index < 0 || count < 0 || index > buffer.Length - count)
            throw new ArgumentOutOfRangeException();
        WriteBounded(buffer.AsSpan(index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer) =>
        WriteBounded(buffer);

    public override void Flush() => _inner.Flush();

    private void WriteBounded(ReadOnlySpan<char> value)
    {
        // Encoding.UTF8 uses replacement fallback for isolated surrogate code
        // units. The accounting therefore matches the bytes a normal UTF-8
        // console writer would emit without allowing malformed text to escape
        // the quota.
        var bytes = Encoding.UTF8.GetByteCount(value);
        lock (_gate)
        {
            if (bytes > _maxBytes - _writtenBytes)
                throw new GuestOutputLimitException();
            _writtenBytes += bytes;
            _inner.Write(value);
        }
    }
}
