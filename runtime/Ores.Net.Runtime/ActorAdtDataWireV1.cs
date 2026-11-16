using System;
using System.Buffers.Binary;
using System.Text;

namespace Ores.Net.Runtime;

/// <summary>
/// Bounded, canonical DATA-only transport profile for Option&lt;Result&lt;int,string&gt;&gt;.
/// The actor host must separately validate admission, generation and ownership.
/// Decoding DATA never constructs a supervisor CONTROL notice. This class is
/// independent of guest compiler emission and is safe to use with NativeAOT.
/// </summary>
public static class ActorAdtDataWireV1
{
    public abstract record Value;
    public sealed record None : Value;
    public sealed record SomeOk(long Number) : Value;
    public sealed record SomeErr(string Error) : Value;

    private const int Header = 7;
    private const int MaxErrorBytes = 256;
    private const int MaxBytes = Header + 2 + MaxErrorBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte tag;
        byte[] payload;
        switch (value)
        {
            case None:
                tag = 0;
                payload = Array.Empty<byte>();
                break;
            case SomeOk ok:
                tag = 1;
                payload = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(payload, ok.Number);
                break;
            case SomeErr err:
                tag = 2;
                if (err.Error is null)
                    throw new ArgumentException("null ADT error value");
                if (err.Error.Length > MaxErrorBytes)
                    throw new ArgumentException("ADT error exceeds character budget");
                byte[] error;
                try { error = StrictUtf8.GetBytes(err.Error); }
                catch (EncoderFallbackException ex)
                {
                    throw new ArgumentException("invalid ADT error Unicode", ex);
                }
                if (error.Length > MaxErrorBytes)
                    throw new ArgumentException("ADT error exceeds byte budget");
                payload = new byte[2 + error.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(payload, checked((ushort)error.Length));
                error.AsSpan().CopyTo(payload.AsSpan(2));
                break;
            default:
                throw new ArgumentException("unknown ADT variant");
        }
        byte[] output = new byte[Header + payload.Length];
        output[0] = (byte)'A';
        output[1] = (byte)'T';
        output[2] = (byte)'A';
        output[3] = (byte)'D';
        output[4] = 1; // version
        output[5] = 1; // nominal family Option<Result<i64,UTF8>>
        output[6] = tag;
        payload.AsSpan().CopyTo(output.AsSpan(Header));
        return output;
    }

    public static Value Decode(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < Header || wire.Length > MaxBytes ||
            wire[0] != 'A' || wire[1] != 'T' || wire[2] != 'A' ||
            wire[3] != 'D' || wire[4] != 1 || wire[5] != 1)
            throw new FormatException("invalid ADT magic, version, family or size");
        switch (wire[6])
        {
            case 0:
                if (wire.Length != Header) throw new FormatException("None contains data");
                return new None();
            case 1:
                if (wire.Length != Header + 8) throw new FormatException("Ok payload length");
                return new SomeOk(BinaryPrimitives.ReadInt64LittleEndian(wire.Slice(Header, 8)));
            case 2:
                if (wire.Length < Header + 2) throw new FormatException("Err length missing");
                int length = BinaryPrimitives.ReadUInt16LittleEndian(wire.Slice(Header, 2));
                if (length > MaxErrorBytes || wire.Length != Header + 2 + length)
                    throw new FormatException("Err payload length invalid");
                try { return new SomeErr(StrictUtf8.GetString(wire.Slice(Header + 2, length))); }
                catch (DecoderFallbackException ex)
                {
                    throw new FormatException("invalid ADT Err UTF-8", ex);
                }
            default:
                // Reject CONTROL-shaped tags: these bytes grant no host authority.
                throw new FormatException("unknown ADT variant / forbidden CONTROL tag");
        }
    }
}
