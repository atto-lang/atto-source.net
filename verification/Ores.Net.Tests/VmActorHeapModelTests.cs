using Ores.Net.Runtime;

/// <summary>
/// atto-spec chapter 27, section 6 conformance for OresVmHost actors: each
/// actor incarnation owns one unmanaged heap holding its state, charged to the
/// host budget, never shared, and refunded exactly once at retirement.
/// </summary>
internal static class VmActorHeapModelTests
{
    private static readonly OresVmHost.ActorStateField[] Layout = {
        new(OresVmHost.ActorStateScalarKind.Int64, Int64Default: 5),
        new(OresVmHost.ActorStateScalarKind.Boolean, BooleanDefault: true)
    };

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new Exception("actor heap model: " + label);
    }

    private static void Write(OresVmHost host, OresVmHost.IsolateHandle tenant,
        OresVmHost.ActorTicket actor, long value, bool flag)
    {
        var turn = host.BeginActorTurn(tenant, actor);
        var loan = host.BeginActorStateLoan(tenant, actor, turn, writable: true);
        host.WriteActorStateInt64(tenant, actor, turn, loan, 0, value);
        host.WriteActorStateBoolean(tenant, actor, turn, loan, 1, flag);
        host.EndActorStateLoan(tenant, actor, turn, loan);
        host.CompleteActorTurn(tenant, actor, turn);
    }

    private static (long Value, bool Flag) Read(OresVmHost host,
        OresVmHost.IsolateHandle tenant, OresVmHost.ActorTicket actor)
    {
        var turn = host.BeginActorTurn(tenant, actor);
        var loan = host.BeginActorStateLoan(tenant, actor, turn);
        var result = (host.ReadActorStateInt64(tenant, actor, turn, loan, 0),
            host.ReadActorStateBoolean(tenant, actor, turn, loan, 1));
        host.EndActorStateLoan(tenant, actor, turn, loan);
        host.CompleteActorTurn(tenant, actor, turn);
        return result;
    }

    public static void Run()
    {
        var host = new OresVmHost(maxTenants: 2);
        var tenant = host.CreateIsolate("heap", maxActors: 3);
        host.Start(tenant);

        // Demand-backed: registering opens the heap but charges nothing yet.
        var a = host.RegisterActor(tenant, 1);
        var b = host.RegisterActor(tenant, 2);
        Require(host.ActorHeapReservedBytes == 0, "an empty heap is not charged");

        host.ConfigureActorState(tenant, a, Layout);
        long afterOne = host.ActorHeapReservedBytes;
        Require(afterOne > 0, "state words are charged to the actor-heap budget");
        host.ConfigureActorState(tenant, b, Layout);
        Require(host.ActorHeapReservedBytes == 2 * afterOne, "one heap per actor");

        // Conformance 1: same offsets, different heaps.
        Require(Read(host, tenant, a) == (5, true), "defaults are written into the heap");
        Write(host, tenant, a, 41, false);
        Write(host, tenant, b, -7, true);
        Require(Read(host, tenant, a) == (41, false), "actor a reads its own heap");
        Require(Read(host, tenant, b) == (-7, true), "actor b reads its own heap");

        // Conformance 4: retirement refunds each heap exactly once.
        host.RetireActor(tenant, a);
        Require(host.ActorHeapReservedBytes == afterOne, "retiring a refunds its heap");
        host.RetireActor(tenant, b);
        Require(host.ActorHeapReservedBytes == 0, "retiring b refunds its heap");

        // Conformance 3: a heap charge past the budget fails without a partial
        // charge, and the actor stays unconfigured.
        var tight = new OresVmHost(maxTenants: 1, maxActorHeapBytes: 8);
        var t2 = tight.CreateIsolate("tight", maxActors: 1);
        tight.Start(t2);
        var c = tight.RegisterActor(t2, 1);
        bool denied = false;
        try { tight.ConfigureActorState(t2, c, Layout); }
        catch (InvalidOperationException) { denied = true; }
        Require(denied, "an allocation past the actor-heap budget fails");
        Require(tight.ActorHeapReservedBytes == 0, "a failed allocation charges nothing");
        bool unconfigured = false;
        var turn = tight.BeginActorTurn(t2, c);
        var loan = tight.BeginActorStateLoan(t2, c, turn);
        try { _ = tight.ReadActorStateInt64(t2, c, turn, loan, 0); }
        catch (InvalidOperationException) { unconfigured = true; }
        tight.EndActorStateLoan(t2, c, turn, loan);
        tight.CompleteActorTurn(t2, c, turn);
        Require(unconfigured, "a failed charge leaves the layout uninstalled");
        tight.RetireActor(t2, c);
        Require(tight.ActorHeapReservedBytes == 0, "retiring an uncharged heap refunds nothing");

        Console.WriteLine("PASS VmActorHeapModelTests: one unmanaged heap per actor incarnation, budget-charged, isolated, refunded once");
    }
}
