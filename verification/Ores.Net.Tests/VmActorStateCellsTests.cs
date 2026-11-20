using Ores.Net.Runtime;

internal static class VmActorStateCellsTests
{
    private static void Deny(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("actor state cell admission failed: " + label);
    }

    private static readonly OresVmHost.ActorStateField[] Layout = {
        new(OresVmHost.ActorStateScalarKind.Int64, Int64Default: 0),
        new(OresVmHost.ActorStateScalarKind.Boolean, BooleanDefault: true)
    };

    public static void Run()
    {
        var host = new OresVmHost(maxTenants: 2);
        var tenantA = host.CreateIsolate("a", maxActors: 2);
        var tenantB = host.CreateIsolate("b", maxActors: 1);
        host.Start(tenantA);
        host.Start(tenantB);
        var a1 = host.RegisterActor(tenantA, 11);
        var a2 = host.RegisterActor(tenantA, 12);
        var b1 = host.RegisterActor(tenantB, 11);

        Deny(() => host.ConfigureActorState(tenantA, a1,
            Array.Empty<OresVmHost.ActorStateField>()), "empty layout");
        Deny(() => host.ConfigureActorState(tenantA, a1,
            new OresVmHost.ActorStateField[17]), "layout over 16");
        Deny(() => host.ConfigureActorState(tenantA, a1,
            new[] { new OresVmHost.ActorStateField(
                OresVmHost.ActorStateScalarKind.Boolean, Int64Default: 1) }),
            "boolean with i64 default");
        Deny(() => host.ConfigureActorState(tenantA, a1,
            new[] { new OresVmHost.ActorStateField(
                OresVmHost.ActorStateScalarKind.Int64, BooleanDefault: true) }),
            "i64 with boolean default");
        Deny(() => host.ConfigureActorState(tenantA, a1,
            new[] { new OresVmHost.ActorStateField((OresVmHost.ActorStateScalarKind)99) }),
            "unrecognized field kind");
        host.ConfigureActorState(tenantA, a1, Layout);
        host.ConfigureActorState(tenantA, a2, Layout);
        host.ConfigureActorState(tenantB, b1, Layout);
        Deny(() => host.ConfigureActorState(tenantA, a1, Layout),
            "layout can only be installed once");
        Deny(() => host.ConfigureActorState(tenantB, a1, Layout),
            "foreign tenant cannot install actor layout");

        var turnA = host.BeginActorTurn(tenantA, a1);
        Deny(() => host.ConfigureActorState(tenantA, a1, Layout),
            "cannot replace layout during a live turn");
        var read = host.BeginActorStateLoan(tenantA, a1, turnA);
        if (host.ReadActorStateInt64(tenantA, a1, turnA, read, 0) != 0 ||
            !host.ReadActorStateBoolean(tenantA, a1, turnA, read, 1))
            throw new Exception("actor scalar default initialization mismatch");
        Deny(() => host.WriteActorStateInt64(tenantA, a1, turnA, read, 0, 7),
            "read-only write must be denied");
        Deny(() => host.WriteActorStateBoolean(tenantA, a1, turnA, read, 1, false),
            "read-only boolean write must be denied");
        Deny(() => host.ReadActorStateBoolean(tenantA, a1, turnA, read, 0),
            "i64 field read as boolean");
        Deny(() => host.ReadActorStateInt64(tenantA, a1, turnA, read, 1),
            "boolean field read as i64");
        Deny(() => host.ReadActorStateInt64(tenantA, a1, turnA, read, -1),
            "negative index");
        Deny(() => host.ReadActorStateInt64(tenantA, a1, turnA, read, 2),
            "out-of-bounds index");
        Deny(() => host.ReadActorStateInt64(tenantB, b1, turnA, read, 0),
            "cross-isolate state read");
        host.EndActorStateLoan(tenantA, a1, turnA, read);

        var write = host.BeginActorStateLoan(tenantA, a1, turnA, writable: true);
        if (host.AddActorStateInt64(tenantA, a1, turnA, write, 0, 1) != 1 ||
            host.AddActorStateInt64(tenantA, a1, turnA, write, 0, 2) != 3)
            throw new Exception("checked atomic counter increment mismatch");
        host.WriteActorStateBoolean(tenantA, a1, turnA, write, 1, false);
        var nested = host.BeginActorStateLoan(tenantA, a1, turnA);
        Deny(() => host.AddActorStateInt64(tenantA, a1, turnA, write, 0, 1),
            "nested read-only loan must fence writable parent");
        host.EndActorStateLoan(tenantA, a1, turnA, nested);
        host.WriteActorStateInt64(tenantA, a1, turnA, write, 0, long.MaxValue);
        try {
            host.AddActorStateInt64(tenantA, a1, turnA, write, 0, 1);
            throw new Exception("expected checked i64 overflow");
        } catch (OverflowException) { }
        if (host.ReadActorStateInt64(tenantA, a1, turnA, write, 0) != long.MaxValue)
            throw new Exception("overflow must not partially commit");
        host.WriteActorStateInt64(tenantA, a1, turnA, write, 0, 3);
        host.EndActorStateLoan(tenantA, a1, turnA, write);
        Deny(() => host.AddActorStateInt64(tenantA, a1, turnA, write, 0, 1),
            "retired state loan cannot mutate cells");
        host.SuspendActorTurn(tenantA, a1, turnA);
        Deny(() => host.BeginActorTurn(tenantA, a1),
            "suspended handler cannot admit another user turn");
        Deny(() => host.ReadActorStateInt64(tenantA, a1, turnA, write, 0),
            "suspended turn cannot access old state");
        host.ResumeActorTurn(tenantA, a1, turnA);
        var resumed = host.BeginActorStateLoan(tenantA, a1, turnA);
        if (host.ReadActorStateInt64(tenantA, a1, turnA, resumed, 0) != 3 ||
            host.ReadActorStateBoolean(tenantA, a1, turnA, resumed, 1))
            throw new Exception("resumed state continuity mismatch");
        host.EndActorStateLoan(tenantA, a1, turnA, resumed);
        host.CompleteActorTurn(tenantA, a1, turnA);

        var turnA2 = host.BeginActorTurn(tenantA, a2);
        var a2loan = host.BeginActorStateLoan(tenantA, a2, turnA2);
        if (host.ReadActorStateInt64(tenantA, a2, turnA2, a2loan, 0) != 0 ||
            !host.ReadActorStateBoolean(tenantA, a2, turnA2, a2loan, 1))
            throw new Exception("same-tenant actor state leaked");
        host.EndActorStateLoan(tenantA, a2, turnA2, a2loan);
        host.CompleteActorTurn(tenantA, a2, turnA2);

        var turnB = host.BeginActorTurn(tenantB, b1);
        var bloan = host.BeginActorStateLoan(tenantB, b1, turnB, writable: true);
        if (host.AddActorStateInt64(tenantB, b1, turnB, bloan, 0, -4) != -4)
            throw new Exception("foreign tenant signed counter mismatch");
        Deny(() => host.ReadActorStateInt64(tenantB, b1, turnB, write, 0),
            "cross-actor stale state loan");
        host.CancelActorTurn(tenantB, b1, turnB);
        Deny(() => host.WriteActorStateInt64(tenantB, b1, turnB, bloan, 0, 8),
            "cancelled turn write must be denied");
        var bnext = host.BeginActorTurn(tenantB, b1);
        var bread = host.BeginActorStateLoan(tenantB, b1, bnext);
        if (host.ReadActorStateInt64(tenantB, b1, bnext, bread, 0) != -4)
            throw new Exception("cancel should retain committed state");
        host.EndActorStateLoan(tenantB, b1, bnext, bread);
        host.CompleteActorTurn(tenantB, b1, bnext);

        host.RetireActor(tenantA, a1);
        host.RetireActor(tenantA, a2);
        var lateActor = host.RegisterActor(tenantA, 13);
        var lateTurn = host.BeginActorTurn(tenantA, lateActor);
        host.CompleteActorTurn(tenantA, lateActor, lateTurn);
        Deny(() => host.ConfigureActorState(tenantA, lateActor, Layout),
            "actor layout cannot be installed after a completed turn");
        host.RetireActor(tenantA, lateActor);
        host.RetireActor(tenantB, b1);
        host.BeginShutdown(tenantA); host.CompleteShutdown(tenantA); host.Destroy(tenantA);
        host.BeginShutdown(tenantB); host.CompleteShutdown(tenantB); host.Destroy(tenantB);

        var replacementTenant = host.CreateIsolate("a", maxActors: 1);
        host.Start(replacementTenant);
        var newActor = host.RegisterActor(replacementTenant, 11);
        host.ConfigureActorState(replacementTenant, newActor, Layout);
        var freshTurn = host.BeginActorTurn(replacementTenant, newActor);
        var freshLoan = host.BeginActorStateLoan(replacementTenant, newActor, freshTurn);
        if (host.ReadActorStateInt64(replacementTenant, newActor, freshTurn, freshLoan, 0) != 0)
            throw new Exception("restart reused actor-local state");
        Deny(() => host.ReadActorStateInt64(replacementTenant, newActor, freshTurn, read, 0),
            "retired isolate state loan crossed restart");
        host.EndActorStateLoan(replacementTenant, newActor, freshTurn, freshLoan);
        host.CompleteActorTurn(replacementTenant, newActor, freshTurn);
        host.RetireActor(replacementTenant, newActor);
        host.BeginShutdown(replacementTenant);
        host.CompleteShutdown(replacementTenant);
        host.Destroy(replacementTenant);
    }
}
