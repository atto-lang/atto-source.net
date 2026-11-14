using Ores.Net.Runtime;
using System.Buffers.Binary;
using System.Text;

static byte[] B(string value) => Encoding.UTF8.GetBytes(value);
static void Require(bool value, string reason)
{
    if (!value) throw new Exception("DURABLE_COMMAND_WIRE_HOST_FAILURE: " + reason);
}
static void ExpectFormat(Action work)
{
    bool rejected = false;
    try { work(); } catch (FormatException) { rejected = true; }
    Require(rejected, "malformed batch admitted");
}
static void ExpectRange(Action work)
{
    bool rejected = false;
    try { work(); } catch (ArgumentOutOfRangeException) { rejected = true; }
    Require(rejected, "oversized event or command admitted");
}

var events = new List<DurableCommandEventV1>
{
    new(1, B("MoneyDeposited(60)")),
    new(1, B("MoneyDeposited(40)"))
};
byte[] wire = DurableCommandWireV1.EncodeEventBatch(events);
const string golden =
    "0000000200000001000000124d6f6e65794465706f736974656428363029" +
    "00000001000000124d6f6e65794465706f736974656428343029";
Require(Convert.ToHexString(wire).ToLowerInvariant() == golden,
    "canonical Java/LLVM/Wasm/Forge wire mismatch");
var decoded = DurableCommandWireV1.DecodeEventBatch(wire);
Require(decoded.Count == 2 &&
        decoded[0].SchemaVersion == 1 &&
        decoded[0].Payload.AsSpan().SequenceEqual(B("MoneyDeposited(60)")) &&
        decoded[1].Payload.AsSpan().SequenceEqual(B("MoneyDeposited(40)")),
        "two-event roundtrip mismatch");

byte[] request = B("Deposit(100)");
byte[] reply = B("Ok(balance=100)");
var envelope = DurableCommandWireV1.SnapshotCommand(request, reply, events);
request[0] = 0;
reply[0] = 0;
Require(envelope.Request.AsSpan().SequenceEqual(B("Deposit(100)")),
    "request snapshot retained a caller alias");
Require(envelope.Reply.AsSpan().SequenceEqual(B("Ok(balance=100)")),
    "reply snapshot retained a caller alias");
var changed = envelope.Batch;
changed[0] = 0;
Require(envelope.Batch.AsSpan().SequenceEqual(wire), "envelope leaked a batch alias");
byte[] mutablePayload = decoded[0].Payload;
mutablePayload[0] = 0;
Require(decoded[0].Payload.AsSpan().SequenceEqual(B("MoneyDeposited(60)")),
    "decoder leaked a mutable event alias");

foreach (int size in new[] {0, 4, 11, wire.Length - 1})
{
    byte[] truncated = wire[..size];
    ExpectFormat(() => DurableCommandWireV1.DecodeEventBatch(truncated));
}
byte[] extra = new byte[wire.Length + 1];
wire.CopyTo(extra, 0);
ExpectFormat(() => DurableCommandWireV1.DecodeEventBatch(extra));
byte[] badCount = (byte[])wire.Clone();
BinaryPrimitives.WriteUInt32BigEndian(badCount.AsSpan(0, 4), 0);
ExpectFormat(() => DurableCommandWireV1.DecodeEventBatch(badCount));
byte[] badSchema = (byte[])wire.Clone();
BinaryPrimitives.WriteUInt32BigEndian(badSchema.AsSpan(4, 4), 0);
ExpectFormat(() => DurableCommandWireV1.DecodeEventBatch(badSchema));
ExpectRange(() => new DurableCommandEventV1(1, new byte[65537]));
ExpectRange(() => DurableCommandWireV1.EncodeEventBatch(
    Enumerable.Range(0, 257)
        .Select(_ => new DurableCommandEventV1(1, Array.Empty<byte>())).ToArray()));
ExpectRange(() => DurableCommandWireV1.EncodeEventBatch(
    Enumerable.Range(0, 17)
        .Select(_ => new DurableCommandEventV1(1, new byte[65536])).ToArray()));
ExpectRange(() => DurableCommandWireV1.SnapshotCommand(
    new byte[65537], B("ok"), events));
ExpectRange(() => DurableCommandWireV1.SnapshotCommand(
    B("cmd"), new byte[65537], events));
Console.WriteLine("NET_NATIVEAOT_HOST_DURABLE_COMMAND_WIRE_V1_PASS|guest=0|durable=0");
