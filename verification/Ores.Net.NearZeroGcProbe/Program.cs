using Ores.Net.Runtime;

// Executable HOST-ONLY NativeAOT memory and tracing probe, NOT compiled .atto.
// No success marker is emitted until all measurements/guards finish.
static void Require(bool yes, string why)
{
    if (!yes) throw new InvalidOperationException("near-zero-GC probe: " + why);
}

const int nativeOps = 50_000;
const int disabledProbes = 50_000;
const int invalidRounds = 10_000;
const int enabledScopes = 512;
const int maxReceipts = 8;

// Charge native payload memory against BOTH process and tenant budgets.
// A long-lived root remains while transient allocations churn.
var tenantBudget = new ActorArenaBudget(256);
long processBefore = ActorArenaBudget.Process.ReservedBytes;
long nativeAllocated;
int nativeCollections;
using (var arena = new ActorNativeArena(256, 4, tenantBudget))
{
    ulong root = arena.Allocate(16);
    Require(root != ActorNativeArena.InvalidHandle &&
            arena.WriteInt64(root, 0, 0x12345678),
        "permanent native actor root");
    for (int i = 0; i < 512; i++)
    {
        ulong warm = arena.Allocate(32);
        Require(warm != ActorNativeArena.InvalidHandle &&
                arena.WriteInt64(warm, 0, i) &&
                arena.ReadInt64(warm) == i &&
                arena.Drop(warm), "warm actor transient");
    }
    Require(arena.BackingBytes == 256 && tenantBudget.ReservedBytes == 256,
        "physical native backing is charged, not only logical capacity");

    _ = GC.GetAllocatedBytesForCurrentThread(); // warm counter implementation
    int gc0 = GC.CollectionCount(0);
    int gc1 = GC.CollectionCount(1);
    int gc2 = GC.CollectionCount(2);
    long before = GC.GetAllocatedBytesForCurrentThread();
    bool coherent = true;
    for (int i = 0; i < nativeOps; i++)
    {
        ulong item = arena.Allocate(32);
        if (item == ActorNativeArena.InvalidHandle ||
            !arena.WriteInt64(item, 0, i) ||
            arena.ReadInt64(item) != i ||
            !arena.Drop(item))
        {
            coherent = false;
            break;
        }
    }
    nativeAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
    nativeCollections = (GC.CollectionCount(0) - gc0) +
                        (GC.CollectionCount(1) - gc1) +
                        (GC.CollectionCount(2) - gc2);
    Require(coherent, "all numeric-handle actor reads, writes and drops");
    Require(nativeAllocated == 0,
        "50,000 warmed native operations must allocate zero CLR bytes");
    Require(nativeCollections == 0,
        "isolated native actor hot-path must perform no managed collections");
    Require(arena.ReadInt64(root) == 0x12345678,
        "persistent actor root must survive transient churn");
    Require(arena.LiveAllocations == 1 && arena.UsedBytes == 16,
        "native actor tail must reclaim with a persistent root");
    Require(arena.Drop(root) && arena.LiveAllocations == 0 &&
            arena.UsedBytes == 0,
        "last root drop must reclaim the arena cursor");
}
Require(tenantBudget.ReservedBytes == 0 &&
        ActorArenaBudget.Process.ReservedBytes == processBefore,
    "physical native memory refund must occur after final dispose");

// OFF: explicitly allocated owner is outside measured path; Begin must not
// mint random IDs, a scope, a receipt or any other managed object.
var off = new NativeTraceContinuationV1();
var offOwner = off.MintOwner(1);
for (int i = 0; i < 512; i++)
    Require(off.Begin(offOwner, 1, 10) is null, "disabled probe warmup");
long offBefore = GC.GetAllocatedBytesForCurrentThread();
bool offOk = true;
for (int i = 0; i < disabledProbes; i++)
    if (off.Begin(offOwner, 1, 10) is not null) offOk = false;
long offAllocated = GC.GetAllocatedBytesForCurrentThread() - offBefore;
Require(offOk && offAllocated == 0 && off.Active == 0 &&
        off.Completed == 0 && off.Dropped == 0,
    "50,000 disabled trace sites must have zero managed allocations");

// INVALID CONTEXT: network headers must fail before allocating identifiers.
// Immutable inputs and the allocated malformed[] table are outside measurement.
string[] malformed = {
    "01-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
    "00-00000000000000000000000000000000-0123456789abcdef-01",
    "00-0123456789abcdef0123456789abcdef-0000000000000000-01",
    "00-0123456789ABCDEF0123456789abcdef-0123456789abcdef-01",
    "00-0123456789abcdef0123456789abcdef-0123456789abcdef-0g",
    "00-0123456789abcdef0123456789abcdef-0123456789abcdef-",
    "wrong"
};
for (int i = 0; i < 512; i++)
    foreach (string header in malformed)
        Require(!NativeTraceParentV1.TryParse(header, out _),
            "malformed traceparent warmup");
long badBefore = GC.GetAllocatedBytesForCurrentThread();
bool badOk = true;
for (int i = 0; i < invalidRounds; i++)
    foreach (string header in malformed)
        if (NativeTraceParentV1.TryParse(header, out _)) badOk = false;
long badAllocated = GC.GetAllocatedBytesForCurrentThread() - badBefore;
Require(badOk && badAllocated == 0,
    "70,000 malformed trace headers must allocate zero managed bytes");

// ON: tracing is intentionally NOT zero-allocation. Report allocated bytes,
// bound active scopes and receipts; retain only the configured receipt budget.
Require(NativeTraceParentV1.TryParse(
    "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", out var parent),
    "valid W3C parent");
var on = new NativeTraceContinuationV1(enabled: true,
    maxActive: 2, maxReceipts: maxReceipts);
var onOwner = on.MintOwner(9);
long onBefore = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < enabledScopes; i++)
{
    var scope = on.Begin(onOwner, 9, 10, parent);
    Require(scope is not null, "enabled trace scope admission");
    Require(on.Finish(onOwner, scope!, 9,
        NativeTraceContinuationV1.Outcome.Ok),
        "enabled scope exactly-once finish");
}
long onAllocated = GC.GetAllocatedBytesForCurrentThread() - onBefore;
Require(onAllocated > 0, "enabled tracing must not be misreported as zero GC");
Require(on.Active == 0 && on.Completed == enabledScopes &&
        on.Dropped == enabledScopes - maxReceipts,
    "enabled trace receipts are capped and dropped explicitly");
for (int i = 0; i < maxReceipts; i++)
    Require(on.PollReceipt()?.Result ==
            NativeTraceContinuationV1.Outcome.Ok,
        "bounded terminal receipt");
Require(!on.PollReceipt().HasValue, "no additional terminal receipts");
on.Retire(onOwner);
off.Retire(offOwner);

Console.WriteLine("NET_NATIVEAOT_NEAR_ZERO_GC_METRICS_V1" +
    "|native_alloc_bytes=" + nativeAllocated +
    "|native_gc_collections=" + nativeCollections +
    "|disabled_alloc_bytes=" + offAllocated +
    "|invalid_alloc_bytes=" + badAllocated +
    "|enabled_alloc_bytes=" + onAllocated +
    "|enabled_bytes_per_span=" + (onAllocated / enabledScopes) +
    "|receipt_drops=" + on.Dropped +
    "|guest_atto=NOT_EXECUTED");
Console.WriteLine("PASS NET_NATIVEAOT_NEAR_ZERO_GC_HOST_V1" +
    "|native_ops=50000|disabled_probes=50000|invalid_headers=70000" +
    "|on_spans=512|guest_atto=NOT_EXECUTED");
