using Ores.Net.Compiler;
using Ores.Net.Runtime;

/// <summary>
/// Host-only integration: compiler schema -> immutable plan -> NativeAOT
/// actor-local cells. No .ores method body is lowered or executed here.
/// </summary>
internal static class ActorStateLayoutIntegrationTests
{
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new Exception("actor layout integration: " + label);
    }

    private static void Deny(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("actor layout integration permitted " + label);
    }

    public static void Run()
    {
        var source = "define isolated actor Counter as state { " +
            "count: int = -7; enabled: bool = true; } end";
        var parsed = OresParser.InspectActorStateSchema(source);
        var plan = ActorStateLayoutPlan.FromSchema(parsed);
        Require(plan.ActorName == "Counter" && plan.Fields.Count == 2 &&
            plan.Fields[0].Index == 0 && plan.Fields[0].Name == "count" &&
            plan.Fields[0].Kind == ActorStateFieldKind.Int64 &&
            plan.Fields[0].Int64Default == -7 &&
            plan.Fields[1].Index == 1 && plan.Fields[1].Name == "enabled" &&
            plan.Fields[1].Kind == ActorStateFieldKind.Boolean &&
            plan.Fields[1].BooleanDefault,
            "schema field identity/order/defaults");
        // The host layout must be a fresh copy; changing it must not change
        // the authoritative compiler-owned initialization plan.
        var tampered = plan.ToHostFields();
        tampered[0] = new OresVmHost.ActorStateField(
            OresVmHost.ActorStateScalarKind.Int64, Int64Default: 999);
        Require(plan.ToHostFields()[0].Int64Default == -7,
            "mutable host array escaped into immutable compiler plan");
        var fields = plan.Fields as IList<ActorStateLayoutPlan.Field>;
        bool readonlyRejected = false;
        try { fields![0] = new ActorStateLayoutPlan.Field(
            0, "forged", ActorStateFieldKind.Int64, 999, false, 0); }
        catch (NotSupportedException) { readonlyRejected = true; }
        Require(readonlyRejected, "plan metadata was mutable");

        var vm = new OresVmHost(maxTenants: 2);
        var a = vm.CreateIsolate("actor-plan-a", maxActors: 2);
        var b = vm.CreateIsolate("actor-plan-b", maxActors: 1);
        vm.Start(a); vm.Start(b);
        var a1 = vm.RegisterActor(a, 1);
        var a2 = vm.RegisterActor(a, 2);
        var b1 = vm.RegisterActor(b, 1);
        plan.Install(vm, a, a1);
        plan.Install(vm, a, a2);
        plan.Install(vm, b, b1);
        Deny(() => plan.Install(vm, a, a1), "duplicate layout installation");
        Deny(() => plan.Install(vm, b, a1), "cross-isolate installation");

        var turn = vm.BeginActorTurn(a, a1);
        var writable = vm.BeginActorStateLoan(a, a1, turn, writable: true);
        Require(vm.ReadActorStateInt64(a, a1, turn, writable, 0) == -7 &&
            vm.ReadActorStateBoolean(a, a1, turn, writable, 1),
            "compiler plan defaults were not initialized");
        Require(vm.AddActorStateInt64(a, a1, turn, writable, 0, 8) == 1,
            "atomic compiler-indexed increment");
        vm.WriteActorStateBoolean(a, a1, turn, writable, 1, false);
        vm.EndActorStateLoan(a, a1, turn, writable);
        vm.CompleteActorTurn(a, a1, turn);

        var t2 = vm.BeginActorTurn(a, a2);
        var l2 = vm.BeginActorStateLoan(a, a2, t2);
        Require(vm.ReadActorStateInt64(a, a2, t2, l2, 0) == -7 &&
            vm.ReadActorStateBoolean(a, a2, t2, l2, 1),
            "same-tenant state was shared between actor instances");
        vm.EndActorStateLoan(a, a2, t2, l2);
        vm.CompleteActorTurn(a, a2, t2);

        var tb = vm.BeginActorTurn(b, b1);
        var lb = vm.BeginActorStateLoan(b, b1, tb);
        Require(vm.ReadActorStateInt64(b, b1, tb, lb, 0) == -7,
            "cross-tenant state was shared");
        Deny(() => vm.ReadActorStateInt64(b, b1, tb, writable, 0),
            "foreign retired loan");
        vm.EndActorStateLoan(b, b1, tb, lb);
        vm.CompleteActorTurn(b, b1, tb);

        vm.RetireActor(a, a1); vm.RetireActor(a, a2);
        vm.RetireActor(b, b1);
        vm.BeginShutdown(a); vm.CompleteShutdown(a); vm.Destroy(a);
        vm.BeginShutdown(b); vm.CompleteShutdown(b); vm.Destroy(b);

        var restarted = vm.CreateIsolate("actor-plan-a", maxActors: 1);
        vm.Start(restarted);
        var next = vm.RegisterActor(restarted, 1);
        plan.Install(vm, restarted, next);
        var fresh = vm.BeginActorTurn(restarted, next);
        var loan = vm.BeginActorStateLoan(restarted, next, fresh);
        Require(vm.ReadActorStateInt64(restarted, next, fresh, loan, 0) == -7 &&
            vm.ReadActorStateBoolean(restarted, next, fresh, loan, 1),
            "actor restart reused stale state");
        vm.EndActorStateLoan(restarted, next, fresh, loan);
        vm.CompleteActorTurn(restarted, next, fresh);
        vm.RetireActor(restarted, next);
        vm.BeginShutdown(restarted);
        vm.CompleteShutdown(restarted);
        vm.Destroy(restarted);
    }
}
