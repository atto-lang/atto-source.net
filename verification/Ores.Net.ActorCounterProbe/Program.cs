using Ores.Net.Runtime;
using Oreslang.Generated;

static void Require(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("NativeAOT Counter: " + label);
}

var host = new OresVmHost(maxTenants: 2);
var tenantA = host.CreateIsolate("counter-a", maxActors: 2);
var tenantB = host.CreateIsolate("counter-b", maxActors: 1);
host.Start(tenantA); host.Start(tenantB);
var scheduler = host.CreateTrustedScheduler();
var a = Actor_Counter.Create(host, tenantA, 1);
var a2 = Actor_Counter.Create(host, tenantA, 2);
var b = Actor_Counter.Create(host, tenantB, 1);
Require(scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment) == 1, "A1");
Require(scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment) == 2, "A2");
Require(scheduler.Dispatch(tenantB, b, Actor_Counter.Call_increment) == 1, "B1");
Require(scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment) == 3, "A3");
Require(scheduler.Dispatch(tenantB, b, Actor_Counter.Call_increment) == 2, "B2");
Require(scheduler.Dispatch(tenantA, a2, Actor_Counter.Call_increment) == 1, "independent same-tenant actor");

bool deniedReentry = false;
_ = scheduler.Dispatch(tenantA, a, scope => {
    try { _ = scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment); }
    catch (InvalidOperationException) { deniedReentry = true; }
    return scope.ReadInt64(0);
});
Require(deniedReentry, "reentrant actor method unexpectedly admitted");

bool cancelled = false;
OresVmHost.ActorDispatchScope? cancelledScope = null;
try {
    _ = scheduler.Dispatch(tenantA, a, scope => {
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
    scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment) == 4,
    "faulted logical turn not fenced and restarted");

// Overflow must fail in the generated increment path without committing
// any partial change or admitting a stale turn after cancellation.
_ = scheduler.Dispatch(tenantA, a2, scope => {
    scope.WriteInt64(0, long.MaxValue);
    return 0L;
});
bool overflowDenied = false;
try { _ = scheduler.Dispatch(tenantA, a2, Actor_Counter.Call_increment); }
catch (OverflowException) { overflowDenied = true; }
Require(overflowDenied, "generated checked i64 overflow was admitted");
_ = scheduler.Dispatch(tenantA, a2, scope => {
    Require(scope.ReadInt64(0) == long.MaxValue, "generated overflow changed actor state");
    scope.WriteInt64(0, 0);
    return 0L;
});
Require(scheduler.Dispatch(tenantA, a2, Actor_Counter.Call_increment) == 1,
    "overflow left actor lane blocked");

OresVmHost.ActorDispatchScope? retained = null;
_ = scheduler.Dispatch(tenantA, a, scope => {
    retained = scope;
    return scope.ReadInt64(0);
});
bool staleScopeRejected = false;
try { _ = retained!.AddInt64(0, 1L); }
catch (InvalidOperationException) { staleScopeRejected = true; }
Require(staleScopeRejected, "escaped dispatch scope survived end of turn");

host.RetireActor(tenantA, a); host.RetireActor(tenantA, a2);
host.RetireActor(tenantB, b);
host.BeginShutdown(tenantA);host.CompleteShutdown(tenantA);host.Destroy(tenantA);
host.BeginShutdown(tenantB);host.CompleteShutdown(tenantB);host.Destroy(tenantB);

var nextTenant = host.CreateIsolate("counter-a", maxActors: 1);
host.Start(nextTenant);
var next = Actor_Counter.Create(host, nextTenant, 1);
Require(scheduler.Dispatch(nextTenant, next, Actor_Counter.Call_increment) == 1,
    "reincarnation leaked old actor state");
host.RetireActor(nextTenant, next);
host.BeginShutdown(nextTenant);
host.CompleteShutdown(nextTenant);
host.Destroy(nextTenant);
Console.WriteLine("PASS actor-counter-native-aot: A=1,2,3 B=1,2 independent state, no reentrancy, cancellation and restart");
