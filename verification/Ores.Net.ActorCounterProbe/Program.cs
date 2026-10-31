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
