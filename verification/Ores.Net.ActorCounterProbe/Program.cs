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
var a = Actor_Counter.Create(host, tenantA, 1);
var a2 = Actor_Counter.Create(host, tenantA, 2);
var b = Actor_Counter.Create(host, tenantB, 1);
Require(Actor_Counter.Call_increment(host, tenantA, a) == 1, "A1");
Require(Actor_Counter.Call_increment(host, tenantA, a) == 2, "A2");
Require(Actor_Counter.Call_increment(host, tenantB, b) == 1, "B1");
Require(Actor_Counter.Call_increment(host, tenantA, a) == 3, "A3");
Require(Actor_Counter.Call_increment(host, tenantB, b) == 2, "B2");
Require(Actor_Counter.Call_increment(host, tenantA, a2) == 1, "independent same-tenant actor");

bool deniedReentry = false;
_ = host.InvokeTrustedActorStateLong(tenantA, a, (turn, loan) => {
    try { _ = Actor_Counter.Call_increment(host, tenantA, a); }
    catch (InvalidOperationException) { deniedReentry = true; }
    return host.ReadActorStateInt64(tenantA, a, turn, loan, 0);
});
Require(deniedReentry, "reentrant actor method unexpectedly admitted");

bool cancelled = false;
try {
    _ = host.InvokeTrustedActorStateLong(tenantA, a,
        (_, _) => throw new InvalidOperationException("expected fault"));
}
catch (InvalidOperationException e) when (e.Message == "expected fault")
{ cancelled = true; }
Require(cancelled && Actor_Counter.Call_increment(host, tenantA, a) == 4,
    "faulted logical turn not fenced and restarted");

// Overflow must fail in the generated increment path without committing
// any partial change or admitting a stale turn after cancellation.
var extremeTurn = host.BeginActorTurn(tenantA, a2);
var extremeLoan = host.BeginActorStateLoan(tenantA, a2, extremeTurn, writable: true);
host.WriteActorStateInt64(tenantA, a2, extremeTurn, extremeLoan, 0, long.MaxValue);
host.EndActorStateLoan(tenantA, a2, extremeTurn, extremeLoan);
host.CompleteActorTurn(tenantA, a2, extremeTurn);
bool overflowDenied = false;
try { _ = Actor_Counter.Call_increment(host, tenantA, a2); }
catch (OverflowException) { overflowDenied = true; }
Require(overflowDenied, "generated checked i64 overflow was admitted");
var checkTurn = host.BeginActorTurn(tenantA, a2);
var checkLoan = host.BeginActorStateLoan(tenantA, a2, checkTurn, writable: true);
Require(host.ReadActorStateInt64(tenantA, a2, checkTurn, checkLoan, 0) == long.MaxValue,
    "generated overflow changed actor state");
host.WriteActorStateInt64(tenantA, a2, checkTurn, checkLoan, 0, 0);
host.EndActorStateLoan(tenantA, a2, checkTurn, checkLoan);
host.CompleteActorTurn(tenantA, a2, checkTurn);
Require(Actor_Counter.Call_increment(host, tenantA, a2) == 1,
    "overflow left actor lane blocked");

host.RetireActor(tenantA, a); host.RetireActor(tenantA, a2);
host.RetireActor(tenantB, b);
host.BeginShutdown(tenantA);host.CompleteShutdown(tenantA);host.Destroy(tenantA);
host.BeginShutdown(tenantB);host.CompleteShutdown(tenantB);host.Destroy(tenantB);

var nextTenant = host.CreateIsolate("counter-a", maxActors: 1);
host.Start(nextTenant);
var next = Actor_Counter.Create(host, nextTenant, 1);
Require(Actor_Counter.Call_increment(host, nextTenant, next) == 1,
    "reincarnation leaked old actor state");
host.RetireActor(nextTenant, next);
host.BeginShutdown(nextTenant);
host.CompleteShutdown(nextTenant);
host.Destroy(nextTenant);
Console.WriteLine("PASS actor-counter-native-aot: A=1,2,3 B=1,2 independent state, no reentrancy, cancellation and restart");
