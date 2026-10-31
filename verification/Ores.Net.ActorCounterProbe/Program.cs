using Ores.Net.Runtime;
using Oreslang.Generated;

static void Require(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("NativeAOT Counter: " + label);
}

static long Deliver(OresVmHost.ActorScheduler scheduler,
    OresVmHost.IsolateHandle isolate, OresVmHost.ActorTicket actor,
    Func<OresVmHost.ActorDispatchScope, long> generatedMethod)
{
    scheduler.Enqueue(isolate, actor);
    return scheduler.DispatchNext(isolate, actor, generatedMethod);
}

var host = new OresVmHost(maxTenants: 2);
var tenantA = host.CreateIsolate("counter-a", maxActors: 2);
var tenantB = host.CreateIsolate("counter-b", maxActors: 1);
host.Start(tenantA); host.Start(tenantB);
var scheduler = host.CreateTrustedScheduler();
var unconfigured = host.RegisterActor(tenantA, 99);
scheduler.Enqueue(tenantA, unconfigured);
Require(scheduler.PendingMessages(tenantA, unconfigured) == 1,
    "unconfigured actor control setup failed");
bool unconfiguredDenied = false;
try { _ = scheduler.DispatchNext(tenantA, unconfigured,
    Actor_Counter.Call_increment); }
catch (InvalidOperationException e) when (
    e.Message.Contains("state layout", StringComparison.Ordinal)) {
    unconfiguredDenied = true;
}
Require(unconfiguredDenied &&
        scheduler.PendingMessages(tenantA, unconfigured) == 1,
    "unconfigured actor consumed its FIFO control receipt");
host.ConfigureActorState(tenantA, unconfigured,
    new OresVmHost.ActorStateField[] {
        new(OresVmHost.ActorStateScalarKind.Int64, Int64Default: 0L)
    });
Require(scheduler.DispatchNext(tenantA, unconfigured,
            Actor_Counter.Call_increment) == 1 &&
        scheduler.PendingMessages(tenantA, unconfigured) == 0,
    "configured actor could not redeem preserved control receipt");
host.RetireActor(tenantA, unconfigured);
var a = Actor_Counter.Create(host, tenantA, 1);
var a2 = Actor_Counter.Create(host, tenantA, 2);
var b = Actor_Counter.Create(host, tenantB, 1);
bool missingTickDenied = false;
try { _ = scheduler.DispatchNext(tenantA, a, Actor_Counter.Call_increment); }
catch (InvalidOperationException) { missingTickDenied = true; }
Require(missingTickDenied, "actor turn admitted with no mailbox dequeue");
Require(Deliver(scheduler, tenantA, a, Actor_Counter.Call_increment) == 1, "A1");
bool reusedTickDenied = false;
try { _ = scheduler.DispatchNext(tenantA, a, Actor_Counter.Call_increment); }
catch (InvalidOperationException) { reusedTickDenied = true; }
Require(reusedTickDenied, "single FIFO receipt reused for multiple turns");
Require(Deliver(scheduler, tenantA, a, Actor_Counter.Call_increment) == 2, "A2");
Require(Deliver(scheduler, tenantB, b, Actor_Counter.Call_increment) == 1, "B1");
Require(Deliver(scheduler, tenantA, a, Actor_Counter.Call_increment) == 3, "A3");
Require(Deliver(scheduler, tenantB, b, Actor_Counter.Call_increment) == 2, "B2");
Require(Deliver(scheduler, tenantA, a2, Actor_Counter.Call_increment) == 1, "independent same-tenant actor");

bool deniedReentry = false;
_ = Deliver(scheduler, tenantA, a, scope => {
    try { _ = scheduler.DispatchNext(tenantA, a, Actor_Counter.Call_increment); }
    catch (InvalidOperationException) { deniedReentry = true; }
    return scope.ReadInt64(0);
});
Require(deniedReentry, "reentrant actor method unexpectedly admitted");
bool crossThreadDenied = false;
_ = Deliver(scheduler, tenantA, a, scope => {
    crossThreadDenied = Task.Run(() => {
        try { _ = scope.AddInt64(0, 1); return false; }
        catch (InvalidOperationException) { return true; }
    }).GetAwaiter().GetResult();
    return scope.ReadInt64(0);
});
Require(crossThreadDenied, "live dispatch scope escaped to another physical carrier");

bool cancelled = false;
OresVmHost.ActorDispatchScope? cancelledScope = null;
try {
    _ = Deliver(scheduler, tenantA, a, scope => {
        cancelledScope = scope;
        throw new InvalidOperationException("expected fault");
    });
}
catch (InvalidOperationException e) when (e.Message == "expected fault")
{ cancelled = true; }
bool cancellationRevoked = false;
try { _ = cancelledScope!.ReadInt64(0); }
catch (InvalidOperationException) { cancellationRevoked = true; }
Require(cancelled && cancellationRevoked &&
    Deliver(scheduler, tenantA, a, Actor_Counter.Call_increment) == 4,
    "faulted logical turn not fenced and restarted");

// Overflow must fail in the generated increment path without committing
// any partial change or admitting a stale turn after cancellation.
_ = Deliver(scheduler, tenantA, a2, scope => {
    scope.WriteInt64(0, long.MaxValue);
    return 0L;
});
bool overflowDenied = false;
try { _ = Deliver(scheduler, tenantA, a2, Actor_Counter.Call_increment); }
catch (OverflowException) { overflowDenied = true; }
Require(overflowDenied, "generated checked i64 overflow was admitted");
_ = Deliver(scheduler, tenantA, a2, scope => {
    Require(scope.ReadInt64(0) == long.MaxValue, "generated overflow changed actor state");
    scope.WriteInt64(0, 0);
    return 0L;
});
Require(Deliver(scheduler, tenantA, a2, Actor_Counter.Call_increment) == 1,
    "overflow left actor lane blocked");

OresVmHost.ActorDispatchScope? retained = null;
_ = Deliver(scheduler, tenantA, a, scope => {
    retained = scope;
    return scope.ReadInt64(0);
});
bool staleScopeRejected = false;
try { _ = retained!.AddInt64(0, 1L); }
catch (InvalidOperationException) { staleScopeRejected = true; }
Require(staleScopeRejected, "escaped dispatch scope survived end of turn");

// One queued tick must not admit two concurrent physical carriers.
scheduler.Enqueue(tenantA, a);
long TryCompetingDispatch()
{
    try { return scheduler.DispatchNext(tenantA, a,
        Actor_Counter.Call_increment); }
    catch (InvalidOperationException) { return -1L; }
}
var competing = Task.WhenAll(
    Task.Run(TryCompetingDispatch),
    Task.Run(TryCompetingDispatch)).GetAwaiter().GetResult();
Require(competing.Count(x => x == 5L) == 1 &&
        competing.Count(x => x == -1L) == 1 &&
        scheduler.PendingMessages(tenantA, a) == 0,
    "single mailbox control tick started multiple logical turns");

// Preserve arbitrary user bytes at the FIFO head. The specialized
// Counter scheduler must never consume or skip this nonempty envelope.
host.SendActor(tenantA, a2, scheduler.Address(tenantA, a2),
    new byte[] { 0xA5 });
scheduler.Enqueue(tenantA, a2);
Require(scheduler.PendingMessages(tenantA, a2) == 2,
    "invalid mixed payload/control FIFO setup");
bool wrongHeadDenied = false;
try { _ = scheduler.DispatchNext(tenantA, a2, Actor_Counter.Call_increment); }
catch (InvalidOperationException) { wrongHeadDenied = true; }
Require(wrongHeadDenied && scheduler.PendingMessages(tenantA, a2) == 2,
    "specialized actor dispatcher consumed or skipped user mailbox bytes");
var userBytes = scheduler.DequeueUser(tenantA, a2);
Require(userBytes is { Length: 1 } && userBytes[0] == 0xA5 &&
        scheduler.PendingMessages(tenantA, a2) == 1,
    "trusted host failed to drain FIFO-head user payload");
Require(scheduler.DequeueUser(tenantA, a2) is null,
    "trusted user dequeue stole scheduler control");
Require(scheduler.DispatchNext(tenantA, a2, Actor_Counter.Call_increment) == 2 &&
        scheduler.PendingMessages(tenantA, a2) == 0,
    "draining user bytes did not unblock original control tick");

// Zero-byte user data has no authority to redeem a scheduler control
// tick, even if the caller knows the live ActorRef and there is a later
// legitimate tick in the same bounded FIFO.
host.SendActor(tenantB, b, scheduler.Address(tenantB, b),
    Array.Empty<byte>());
scheduler.Enqueue(tenantB, b);
Require(scheduler.PendingMessages(tenantB, b) == 2,
    "zero-byte user envelope setup failed");
bool userEmptyDenied = false;
try { _ = scheduler.DispatchNext(tenantB, b,
    Actor_Counter.Call_increment); }
catch (InvalidOperationException) { userEmptyDenied = true; }
Require(userEmptyDenied && scheduler.PendingMessages(tenantB, b) == 2,
    "zero-byte user payload was misclassified as control authority");
var zeroBytes = scheduler.DequeueUser(tenantB, b);
Require(zeroBytes is { Length: 0 } &&
        scheduler.PendingMessages(tenantB, b) == 1,
    "zero-byte user envelope lost during trusted drain");
Require(scheduler.DequeueUser(tenantB, b) is null,
    "trusted user drain consumed typed control");
Require(scheduler.DispatchNext(tenantB, b, Actor_Counter.Call_increment) == 3 &&
        scheduler.PendingMessages(tenantB, b) == 0,
    "zero-byte user drain did not unblock control");
// Interleaved user/control/user/control must preserve every FIFO head.
host.SendActor(tenantA, a2, scheduler.Address(tenantA, a2),
    new byte[] { 0x61, 0x62 });
scheduler.Enqueue(tenantA, a2);
host.SendActor(tenantA, a2, scheduler.Address(tenantA, a2),
    new byte[] { 0x63 });
scheduler.Enqueue(tenantA, a2);
Require(scheduler.PendingMessages(tenantA, a2) == 4,
    "mixed FIFO setup lost an envelope");
bool firstHeadBlocked = false;
try { _ = scheduler.DispatchNext(tenantA, a2, Actor_Counter.Call_increment); }
catch (InvalidOperationException) { firstHeadBlocked = true; }
Require(firstHeadBlocked && scheduler.PendingMessages(tenantA, a2) == 4,
    "first mixed user payload was bypassed");
var first = scheduler.DequeueUser(tenantA, a2);
Require(first is { Length: 2 } && first[0] == 0x61 &&
        first[1] == 0x62 && scheduler.PendingMessages(tenantA, a2) == 3,
    "first mixed payload lost or changed");
Require(scheduler.DequeueUser(tenantA, a2) is null,
    "mixed user drain stole first control");
Require(scheduler.DispatchNext(tenantA, a2,
        Actor_Counter.Call_increment) == 3 &&
        scheduler.PendingMessages(tenantA, a2) == 2,
    "mixed first tick failed or replayed");
bool secondHeadBlocked = false;
try { _ = scheduler.DispatchNext(tenantA, a2, Actor_Counter.Call_increment); }
catch (InvalidOperationException) { secondHeadBlocked = true; }
Require(secondHeadBlocked && scheduler.PendingMessages(tenantA, a2) == 2,
    "second mixed user payload was bypassed");
var second = scheduler.DequeueUser(tenantA, a2);
Require(second is { Length: 1 } && second[0] == 0x63 &&
        scheduler.PendingMessages(tenantA, a2) == 1,
    "second mixed payload lost or changed");
Require(scheduler.DispatchNext(tenantA, a2,
        Actor_Counter.Call_increment) == 4 &&
        scheduler.PendingMessages(tenantA, a2) == 0,
    "mixed second tick failed or replayed");
bool exhaustedReceiptDenied = false;
try { _ = scheduler.DispatchNext(tenantA, a2, Actor_Counter.Call_increment); }
catch (InvalidOperationException) { exhaustedReceiptDenied = true; }
Require(exhaustedReceiptDenied, "mixed FIFO admitted unqueued native turn");

bool foreignDrainDenied = false;
try { _ = scheduler.DequeueUser(tenantA, b); }
catch (InvalidOperationException) { foreignDrainDenied = true; }
Require(foreignDrainDenied, "trusted user dequeue crossed tenant");
bool activeDrainDenied = false;
_ = Deliver(scheduler, tenantA, a, scope => {
    try { _ = scheduler.DequeueUser(tenantA, a); }
    catch (InvalidOperationException) { activeDrainDenied = true; }
    return scope.ReadInt64(0);
});
Require(activeDrainDenied, "trusted user dequeue reentered actor turn");

host.RetireActor(tenantA, a); host.RetireActor(tenantA, a2);
host.RetireActor(tenantB, b);
host.BeginShutdown(tenantA);host.CompleteShutdown(tenantA);host.Destroy(tenantA);
host.BeginShutdown(tenantB);host.CompleteShutdown(tenantB);host.Destroy(tenantB);

var nextTenant = host.CreateIsolate("counter-a", maxActors: 1);
host.Start(nextTenant);
var next = Actor_Counter.Create(host, nextTenant, 1);
Require(Deliver(scheduler, nextTenant, next, Actor_Counter.Call_increment) == 1,
    "reincarnation leaked old actor state");
host.RetireActor(nextTenant, next);
host.BeginShutdown(nextTenant);
host.CompleteShutdown(nextTenant);
host.Destroy(nextTenant);

// Pre-layout FIFO control must survive a rejected dispatch, and the same
// tick must run once after trusted state initialization (no replacement tick).
var rawTenant = host.CreateIsolate("raw-counter", maxActors: 1);
host.Start(rawTenant);
var raw = host.RegisterActor(rawTenant, 13);
scheduler.Enqueue(rawTenant, raw);
Require(scheduler.PendingMessages(rawTenant, raw) == 1,
    "pre-layout tick was not queued");
bool layoutDenied = false;
try { _ = scheduler.DispatchNext(rawTenant, raw, Actor_Counter.Call_increment); }
catch (InvalidOperationException) { layoutDenied = true; }
Require(layoutDenied &&
        scheduler.PendingMessages(rawTenant, raw) == 1 &&
        !host.ActorTurnSnapshot(rawTenant, raw).Active,
    "pre-layout dispatch lost tick or admitted a turn");
host.ConfigureActorState(rawTenant, raw, new[] {
    new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64)
});
Require(scheduler.DispatchNext(rawTenant, raw, Actor_Counter.Call_increment) == 1 &&
        scheduler.PendingMessages(rawTenant, raw) == 0,
    "original pre-layout tick was not redeemed exactly once");
bool prelayoutTickReplayDenied = false;
try { _ = scheduler.DispatchNext(rawTenant, raw, Actor_Counter.Call_increment); }
catch (InvalidOperationException) { prelayoutTickReplayDenied = true; }
Require(prelayoutTickReplayDenied, "pre-layout tick was replayed");
host.RetireActor(rawTenant, raw);
host.BeginShutdown(rawTenant);
host.CompleteShutdown(rawTenant);
host.Destroy(rawTenant);
Console.WriteLine("PASS actor-counter-native-aot: A=1,2,3 B=1,2 independent state, no reentrancy, cancellation and restart");
