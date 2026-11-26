using System;

namespace Ores.Net.Runtime;

// Host-only metadata gate. Internal to trusted runtime; not a decoder or capability.
internal static class ActorDataCodecPolicyV1
{
    internal enum Reject { UnknownCodec, InvalidMask, Denied, Unsupported, InvalidBinding, Mismatch }
    internal sealed class PolicyError : Exception
    {
        internal Reject Reason { get; }
        internal PolicyError(Reject reason) : base(reason.ToString()) { Reason = reason; }
    }
    private static int Parse(string? label) => label switch
    {
        "messagepack" => 1, "protobuf" => 2, "cbor" => 4, "flatbuffers" => 8, _ => 0
    };
    private static bool ValidSchema(string? schema)
    {
        if (schema is null || schema.Length == 0 || schema.Length > 256) return false;
        foreach (char c in schema) if (c < 33 || c > 126) return false;
        return true;
    }
    internal sealed class Binding
    {
        private readonly int _codec;
        private readonly uint _version;
        private readonly string _schema;
        private readonly byte[] _shape;
        private Binding(int codec, uint version, string schema, ReadOnlySpan<byte> shape)
        { _codec = codec; _version = version; _schema = schema; _shape = shape.ToArray(); }

        // Registration is trusted. A supplied support mask is not support evidence.
        internal static Binding Select(string? requested, int allowed, int supported,
            long version, string? schema, ReadOnlySpan<byte> shape)
        {
            if (((allowed | supported) & ~15) != 0) throw new PolicyError(Reject.InvalidMask);
            int codec = Parse(requested ?? "messagepack");
            if (codec == 0) throw new PolicyError(Reject.UnknownCodec);
            if ((allowed & codec) == 0) throw new PolicyError(Reject.Denied);
            if ((supported & codec) == 0) throw new PolicyError(Reject.Unsupported);
            if (version <= 0 || version > uint.MaxValue || !ValidSchema(schema) || shape.Length != 32)
                throw new PolicyError(Reject.InvalidBinding);
            return new Binding(codec, (uint)version, schema!, shape);
        }
        // Authenticated metadata only; never select decoder using incoming fields.
        internal void RequireMatch(string? label, long version, string? schema, ReadOnlySpan<byte> shape)
        {
            if (Parse(label) != _codec || version != _version || !ValidSchema(schema)
                || !string.Equals(_schema, schema, StringComparison.Ordinal)
                || shape.Length != 32 || !shape.SequenceEqual(_shape))
                throw new PolicyError(Reject.Mismatch);
        }
    }
}
