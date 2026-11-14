using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Ores.Net.Runtime;
using Oreslang.Generated;

// Executable, source-generated .atto actor subset, NOT general actor CIL lowering.
// The checked-in Actor_MessageCounter.g.cs must be regenerated from the exact
// .atto input and compared byte-for-byte BEFORE building this witness.
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("atto actor GC proof: " + message);
}

const int warmupMessages = 256;
const int sampledMessages = 4096;
const int batchSize = 32;
const long initialBudgetBytes = 8L * 1024;
const long maximumObservedBytesPerMessage = 8192;
// This provisional ceiling is intentionally nonzero: the current mailbox uses
// managed Envelope/byte[] objects and per-turn ActorContext metadata. Do NOT
// present this measurement as a zero-allocation guest message fast path.
bool requireNativeAot = Environment.GetEnvironmentVariable("ATTO_REQUIRE_NATIVE_AOT") == "1";
bool dynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported;
if (requireNativeAot)
    Require(!dynamicCodeSupported, "Native AOT binary still admits runtime dynamic code");

long receipts = 0;
int badReceipts = 0;
ActorRef guest = default;
var options = new ActorRuntimeOptions
{
    MaxActors = 3,
    MessagesPerDispatch = 32,
    MailboxMessages = 128,
    MailboxBytes = 4096,
    ActorPersistentBytes = 256,
    ActorScratchBytes = 64,
    ActorStackBytes = 16,
    ActorMaxAllocations = 16,
    NativeBudgetBytes = initialBudgetBytes
};

using var runtime = new ActorRuntime(options);
ActorRef supervisor = runtime.Spawn(ActorKind.Shared, (_, data) =>
{
    if (data.Length != 16)
    {
        Interlocked.Exchange(ref badReceipts, 1);
        return;
    }

    ulong sender = BinaryPrimitives.ReadUInt64LittleEndian(data.Span);
    long result = BinaryPrimitives.ReadInt64LittleEndian(data.Span[8..]);
    // The private guest actor must preserve FIFO ordering and its own
    // actor-owned persistent state, not recompute values in a host loop.
    long expected = Interlocked.Read(ref receipts) + 1;
    if (sender != guest.Id || result != expected)
        Interlocked.Exchange(ref badReceipts, 1);
    Interlocked.Increment(ref receipts);
});
guest = Actor_MessageCounter.SpawnRuntime(runtime, supervisor);
Require(runtime.NativeReservedBytes > 0 &&
        runtime.NativeReservedBytes <= initialBudgetBytes,
    "native state reservations must remain bounded from actor spawn");

void SendAndVerifyBatch(int count)
{
    long expected = Interlocked.Read(ref receipts) + count;
    Span<byte> payload = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(payload, 1L);
    for (int i = 0; i < count; i++)
        runtime.Send(guest, payload);
    Require(SpinWait.SpinUntil(() =>
            Interlocked.Read(ref receipts) == expected ||
            Volatile.Read(ref badReceipts) != 0 ||
            runtime.FaultedTurns != 0,
        TimeSpan.FromSeconds(20)), "bounded mailbox batch timed out");
    Require(Volatile.Read(ref badReceipts) == 0 &&
            runtime.FaultedTurns == 0 &&
            Interlocked.Read(ref receipts) == expected,
        "source-generated actor mailbox/state sequence mismatch");
}

// Warm the dispatcher, actor native state, logical stack and CLR paths before
// sampling. No actor, state or supervisor is constructed inside the measurement.
for (int i = 0; i < warmupMessages / batchSize; i++)
    SendAndVerifyBatch(batchSize);

Require(Interlocked.Read(ref receipts) == warmupMessages,
    "warm actor-owned i64 state initialization");
long nativeBefore = runtime.NativeReservedBytes;
int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
var clock = Stopwatch.StartNew();
for (int i = 0; i < sampledMessages / batchSize; i++)
    SendAndVerifyBatch(batchSize);
clock.Stop();
long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
long allocated = allocatedAfter - allocatedBefore;
int collections = GC.CollectionCount(0) - gc0 +
                  GC.CollectionCount(1) - gc1 +
                  GC.CollectionCount(2) - gc2;
long bytesPerMessage = allocated / sampledMessages;
Require(allocated >= 0 && allocated <= maximumObservedBytesPerMessage * sampledMessages,
    "provisional total-CLR bytes-per-guest-message ceiling");
Require(nativeBefore > 0 &&
        runtime.NativeReservedBytes == nativeBefore &&
        runtime.NativeReservedBytes <= initialBudgetBytes,
    "actor native reservations changed or exceeded budget during steady state");
Require(Interlocked.Read(ref receipts) == warmupMessages + sampledMessages &&
        Volatile.Read(ref badReceipts) == 0,
    "compiled guest result is not the exact accumulated 4352");

long completed = runtime.CompletedMessages;
Require(completed >= 2L * (warmupMessages + sampledMessages),
    "real mailbox DATA turns for actor and supervisor were not completed");
Require(runtime.FaultedTurns == 0, "guest execution faulted on valid i64 input");

// Wrong wire shape must terminate only the malformed actor; a live sibling
// must continue with its independent state, not inherit the retired state.
ActorRef badActor = Actor_MessageCounter.SpawnRuntime(runtime, supervisor);
Span<byte> malformed = stackalloc byte[7];
runtime.Send(badActor, malformed);
Require(SpinWait.SpinUntil(() => runtime.FaultedTurns == 1,
    TimeSpan.FromSeconds(20)), "malformed signed-i64 DATA turn did not fault");
SendAndVerifyBatch(batchSize);
Require(Interlocked.Read(ref receipts) == warmupMessages + sampledMessages + batchSize,
    "valid source-generated actor failed after malformed sibling STOP");

runtime.Stop(guest);
Require(SpinWait.SpinUntil(() => runtime.LiveActors == 1,
    TimeSpan.FromSeconds(20)), "guest and malformed peer failed to retire");
bool stoppedSendDenied = false;
try
{
    Span<byte> payload = stackalloc byte[8];
    runtime.Send(guest, payload);
}
catch (InvalidOperationException) { stoppedSendDenied = true; }
Require(stoppedSendDenied, "stale actor generation accepted an extra message");
runtime.Stop(supervisor);
runtime.Dispose();
Require(runtime.NativeReservedBytes == 0 &&
        runtime.LiveActors == 0,
    "actor retirement did not refund all native reservations");

string mode = requireNativeAot ? "nativeaot" : "managed";
Console.WriteLine("ATTO_ACTOR_MAILBOX_GC_METRICS_V1" +
    "|mode=" + mode +
    "|warmup=" + warmupMessages +
    "|sampled=" + sampledMessages +
    "|managed_total_allocated_bytes=" + allocated +
    "|managed_bytes_per_message=" + bytesPerMessage +
    "|gc_collections=" + collections +
    "|elapsed_ms=" + clock.ElapsedMilliseconds +
    "|native_reserved_before=" + nativeBefore +
    "|native_reserved_after=0" +
    "|guest_subset=checked_actor_increment" +
    "|general_actor_cil=NOT_IMPLEMENTED");
Console.WriteLine("PASS ATTO_ACTOR_NATIVEAOT_MAILBOX_GC_V1" +
    "|source=actor-message-counter.atto" +
    "|sampled=4096|fifo=TRUE|native_refund=TRUE" +
    "|malformed_isolated=TRUE|guest_subset=checked_actor_increment");
