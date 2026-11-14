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
bool nativeQueuedStorage = Environment.GetEnvironmentVariable("ATTO_NATIVE_MAILBOX") == "1";
// Both variants execute identical source-generated actor code. Native storage
// still makes an owned managed copy at receive; this is NOT zero-GC mail.
bool dynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported;
if (requireNativeAot)
    Require(!dynamicCodeSupported, "Native AOT binary still admits runtime dynamic code");

long receipts = 0;
int badReceipts = 0;
// Legacy ReadOnlyMemory may be retained, but its sensitive bytes must stay
// scrubbed forever after that turn. Future pooled transport must use a
// separate borrow-only Span API, never recycle this externally retainable view.
ReadOnlyMemory<byte> retainedFirstReply = default;
int retainedFirstReplyCaptured = 0;
ActorRef guest = default;
var options = new ActorRuntimeOptions
{
    MaxActors = 3,
    MessagesPerDispatch = 32,
    MailboxMessages = 128,
    MailboxBytes = 4096,
    NativeMailboxSlotBytes = nativeQueuedStorage ? 16 : 0,
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

    if (Interlocked.CompareExchange(ref retainedFirstReplyCaptured, 1, 0) == 0)
        retainedFirstReply = data;
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

if (nativeQueuedStorage)
{
    long reserved = runtime.NativeReservedBytes;
    bool oversizedRejected = false;
    try { runtime.Send(guest, new byte[17]); }
    catch (ArgumentOutOfRangeException) { oversizedRejected = true; }
    Require(oversizedRejected && runtime.NativeReservedBytes == reserved &&
            runtime.CompletedMessages == 0,
        "native slot size admission must fail without scheduling or quota mutation");
}


void SendAndVerifyBatch(int count)
{
    long expected = Interlocked.Read(ref receipts) + count;
    long expectedFaults = runtime.FaultedTurns;
    Span<byte> payload = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(payload, 1L);
    for (int i = 0; i < count; i++)
        runtime.Send(guest, payload);
    Require(SpinWait.SpinUntil(() =>
            Interlocked.Read(ref receipts) == expected ||
            Volatile.Read(ref badReceipts) != 0 ||
            runtime.FaultedTurns != expectedFaults,
        TimeSpan.FromSeconds(20)), "bounded mailbox batch timed out");
    Require(Volatile.Read(ref badReceipts) == 0 &&
            runtime.FaultedTurns == expectedFaults &&
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

Require(SpinWait.SpinUntil(() =>
        runtime.CompletedMessages >= 2L * (warmupMessages + sampledMessages),
        TimeSpan.FromSeconds(20)),
    "real mailbox DATA turns for actor and supervisor were not completed");
Require(runtime.FaultedTurns == 0, "guest execution faulted on valid i64 input");
Require(retainedFirstReplyCaptured == 1 && retainedFirstReply.Length == 16,
    "first message buffer canary was not observed");
foreach (byte value in retainedFirstReply.Span)
    Require(value == 0, "retained legacy payload escaped turn zeroization");


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
Require(SpinWait.SpinUntil(() =>
    runtime.CompletedMessages >= 2L * (warmupMessages + sampledMessages + batchSize),
    TimeSpan.FromSeconds(20)),
    "survivor and supervisor did not complete final actor turns");
foreach (byte value in retainedFirstReply.Span)
    Require(value == 0, "legacy retained message exposed a later actor payload");


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

if (nativeQueuedStorage)
{
    // A full native queue must never leak its leases when STOP overtakes
    // pending data. The active turn holds a *managed copy*, not an unsafe
    // pointer into a slot that STOP may reclaim on a different thread.
    using var started = new ManualResetEventSlim(false);
    using var unblock = new ManualResetEventSlim(false);
    using var stopped = new ActorRuntime(new ActorRuntimeOptions
    {
        MaxActors = 1,
        MailboxMessages = 32,
        MailboxBytes = 512,
        NativeMailboxSlotBytes = 16,
        ActorPersistentBytes = 256,
        ActorScratchBytes = 64,
        ActorStackBytes = 16,
        NativeBudgetBytes = 4096
    });
    int turns = 0;
    ActorRef pending = stopped.Spawn(ActorKind.Private, (_, _) =>
    {
        Interlocked.Increment(ref turns);
        started.Set();
        if (!unblock.Wait(TimeSpan.FromSeconds(15)))
            throw new InvalidOperationException("blocked turn release timed out");
    });
    Span<byte> input = stackalloc byte[8];
    stopped.Send(pending, input);
    Require(started.Wait(TimeSpan.FromSeconds(15)), "first native turn never started");
    for (int i = 0; i < 32; i++) stopped.Send(pending, input);
    bool fullQueueDenied = false;
    try { stopped.Send(pending, input); }
    catch (InvalidOperationException) { fullQueueDenied = true; }
    Require(fullQueueDenied, "saturated native mailbox admitted a 33rd message");
    stopped.Stop(pending);
    unblock.Set();
    Require(SpinWait.SpinUntil(() =>
            stopped.LiveActors == 0 && stopped.NativeReservedBytes == 0,
        TimeSpan.FromSeconds(15)),
        "STOP of saturated queue failed native lease/arena refund");
    Require(turns == 1 && stopped.CompletedMessages == 1 &&
            stopped.FaultedTurns == 0,
        "STOP must discard queued native mail without extra actor turns");

    // Native slab succeeds, then actor arena admission fails: the Spawn
    // rollback must still refund the independently charged native pool.
    using var exhausted = new ActorRuntime(new ActorRuntimeOptions
    {
        MaxActors = 1,
        MailboxMessages = 32,
        MailboxBytes = 512,
        NativeMailboxSlotBytes = 16,
        ActorPersistentBytes = 256,
        ActorScratchBytes = 64,
        ActorStackBytes = 16,
        NativeBudgetBytes = 32L * 16
    });
    bool budgetDenied = false;
    try { _ = exhausted.Spawn(ActorKind.Private, (_, _) => { }); }
    catch (InvalidOperationException) { budgetDenied = true; }
    Require(budgetDenied && exhausted.LiveActors == 0 &&
            exhausted.NativeReservedBytes == 0,
        "failed Spawn must refund native pool even when arena quota fails");
}


string mode = requireNativeAot ? "nativeaot" : "managed";
Console.WriteLine("ATTO_ACTOR_MAILBOX_GC_METRICS_V1" +
    "|mode=" + mode +
    "|mailbox_storage=" + (nativeQueuedStorage ? "native_copy_on_dequeue" : "managed_copy_on_send") +
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
