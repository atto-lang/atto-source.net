using Ores.Net.Runtime;

static void Check(bool ok, string reason)
{
    if (!ok) throw new Exception(reason);
}
static byte[] Hex(string text) => Convert.FromHexString(text);
static void Exact(ActorAdtDataWireV1.Value value, string expected)
{
    byte[] bytes = ActorAdtDataWireV1.Encode(value);
    Check(bytes.SequenceEqual(Hex(expected)), "wire drift");
    Check(ActorAdtDataWireV1.Decode(bytes) == value, "ADT identity lost");
}
static void Deny(byte[] bytes)
{
    try { _ = ActorAdtDataWireV1.Decode(bytes); }
    catch (FormatException) { return; }
    throw new Exception("malformed DATA or CONTROL spoof admitted");
}

Exact(new ActorAdtDataWireV1.None(), "41544144010100");
Exact(new ActorAdtDataWireV1.SomeOk(42), "415441440101012a00000000000000");
Exact(new ActorAdtDataWireV1.SomeOk(-1), "41544144010101ffffffffffffffff");
Exact(new ActorAdtDataWireV1.SomeOk(long.MinValue), "415441440101010000000000000080");
Exact(new ActorAdtDataWireV1.SomeErr(""), "415441440101020000");
Exact(new ActorAdtDataWireV1.SomeErr("é"), "415441440101020200c3a9");
var random = new Random(20261116);
for (int i = 0; i < 256; i++)
{
    var bits = new byte[8];
    random.NextBytes(bits);
    var value = new ActorAdtDataWireV1.SomeOk(BitConverter.ToInt64(bits));
    Check(ActorAdtDataWireV1.Decode(ActorAdtDataWireV1.Encode(value)) == value,
        "random signed i64 payload drift");
}
for (int length = 0; length <= 256; length += 2)
{
    var value = new ActorAdtDataWireV1.SomeErr(new string('x', length));
    Check(ActorAdtDataWireV1.Decode(ActorAdtDataWireV1.Encode(value)) == value,
        "bounded Err length drift");
}
var good = Hex("415441440101020200c3a9");
for (int length = 0; length < good.Length; length++)
    Deny(good.AsSpan(0, length).ToArray());
for (int index = 0; index < 7; index++)
{
    var broken = good.ToArray();
    broken[index] = index == 6 ? (byte)127 : (byte)(broken[index] ^ 1);
    Deny(broken);
}
Deny(Hex("41544144010100ff"));
Deny(Hex("4154414401010100000000000000"));
Deny(Hex("415441440101020300c3a9"));
Deny(Hex("415441440101020200c328"));
var oversized = new byte[266];
good.AsSpan(0, 7).CopyTo(oversized);
Deny(oversized);
try
{
    _ = ActorAdtDataWireV1.Encode(new ActorAdtDataWireV1.SomeErr("\uD800"));
    throw new Exception("unpaired UTF-16 surrogate encoded");
}
catch (ArgumentException) { }
try
{
    _ = ActorAdtDataWireV1.Encode(new ActorAdtDataWireV1.SomeErr(new string('x', 257)));
    throw new Exception("unbounded Err encoded");
}
catch (ArgumentException) { }
Console.WriteLine("NET_ADT_DATA_WIRE_V1_PASS|profiles=6|control_authority=none|guest_execution=no");
