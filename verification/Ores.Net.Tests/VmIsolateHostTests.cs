using Ores.Net.Runtime;

internal static class VmIsolateHostTests
{
    private static void ExpectFailure(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("IsolateVM gate failed: " + label);
    }

    public static void Run()
    {
        var host = new OresVmHost(maxTenants: 2);
        // Fail closed: NativeAOT and separate host dictionaries do not imply
        // OS sandboxing or a separate GC-managed heap for hostile tenants.
        ExpectFailure(() => host.CreateIsolate("hostile",
            isolation: OresVmHost.IsolationMode.SandboxedProcess),
            "unsupported process sandbox must reject before admission");
        var a = host.CreateIsolate("tenant-a", maxActors: 1, maxResourcePins: 1);
        var b = host.CreateIsolate("tenant-b", maxActors: 1);
        ExpectFailure(() => host.CreateIsolate("tenant-c"), "host limit");
        ExpectFailure(() => host.CreateIsolate("tenant-a"), "duplicate tenant");
        host.Start(a); host.Start(b);
        // Two tenants execute the same statically typed entry but keep their
        // host-owned scalar state independent. This is NOT generated .ores
        // actor state; the production Native AOT host tests real emitted CIL.
        if (host.InvokeGeneratedScalar(a, () => -1) != -1 ||
            host.InvokeGeneratedScalar(a, () => -2) != -3 ||
            host.InvokeGeneratedScalar(b, () => 100) != 100 ||
            host.GuestSnapshot(a).Scalar != -3 ||
            host.GuestSnapshot(b).Scalar != 100)
            throw new Exception("tenant state isolation failed");
        // An exception from a guest delegate is distinct from a typed
        // numeric overflow in the trusted host commit. Quarantine on guest
        // exceptions and require a new incarnation before subsequent calls.
        var faultHost = new OresVmHost(maxTenants: 1);
        var fault = faultHost.CreateIsolate("fault");
        faultHost.Start(fault);
        if (faultHost.InvokeGeneratedScalar(fault, () => 7) != 7)
            throw new Exception("fault fixture setup");
        ExpectFailure(() => faultHost.InvokeGeneratedScalar(fault, () =>
            faultHost.InvokeGeneratedScalar(fault, () => 1)), "nested guest entry");
        if (faultHost.Snapshot(fault).Phase != OresVmHost.Phase.Quiescing ||
            faultHost.GuestSnapshot(fault).GuestCalls != 0)
            throw new Exception("throwing tenant was not quarantined/drained");
        ExpectFailure(() => faultHost.InvokeGeneratedScalar(fault, () => 1),
            "quarantined tenant must refuse new entry");
        faultHost.CompleteShutdown(fault);
        faultHost.Destroy(fault);
        var recovered = faultHost.CreateIsolate("fault");
        faultHost.Start(recovered);
        if (faultHost.InvokeGeneratedScalar(recovered, () => 1) != 1)
            throw new Exception("fault restart reused tenant scalar state");
        ExpectFailure(() => faultHost.Snapshot(fault), "stale fault handle");
        try { faultHost.InvokeGeneratedScalar(recovered, () => throw new OverflowException()); }
        catch (OverflowException) { }
        if (faultHost.Snapshot(recovered).Phase != OresVmHost.Phase.Quiescing ||
            faultHost.GuestSnapshot(recovered).Scalar != 1 ||
            faultHost.GuestSnapshot(recovered).GuestCalls != 0)
            throw new Exception("guest fault leaked a pin or mutated state");
        faultHost.CompleteShutdown(recovered);
        faultHost.Destroy(recovered);
        if (host.GuestSnapshot(a).Scalar != -3)
            throw new Exception("foreign tenant fault changed tenant-a");
        var actorA = host.RegisterActor(a, 3);
        var actorB = host.RegisterActor(b, 3);
        var refA = actorA.Mailbox.CurrentRef;
        host.SendActor(a, actorA, refA, new byte[] { 1, 2, 3 });
        if (actorA.Mailbox.QueuedMessages != 1)
            throw new Exception("actor mailbox must be real, not a fake ticket");

        // Suspension releases no logical actor admission. The exact same turn
        // must resume; later user turns remain blocked until completion.
        var turn = host.BeginActorTurn(a, actorA);
        var turnSnapshot = host.ActorTurnSnapshot(a, actorA);
        if (!turnSnapshot.Active || turnSnapshot.Suspended ||
            turnSnapshot.Generation != turn.Generation)
            throw new Exception("actor logical turn admission snapshot mismatch");
        var writeLoan = host.BeginActorStateLoan(a, actorA, turn, writable: true);
        host.ValidateActorStateLoan(a, actorA, turn, writeLoan, requireWrite: true);
        var readLoan = host.BeginActorStateLoan(a, actorA, turn);
        host.ValidateActorStateLoan(a, actorA, turn, readLoan);
        ExpectFailure(() => host.ValidateActorStateLoan(a, actorA, turn, writeLoan),
            "outer loan must be fenced while nested state scope is active");
        ExpectFailure(() => host.ValidateActorStateLoan(a, actorA, turn, readLoan, requireWrite: true),
            "read-only loan must not admit state mutation");
        ExpectFailure(() => host.EndActorStateLoan(a, actorA, turn, writeLoan),
            "nested scopes must unwind in LIFO order");
        ExpectFailure(() => host.SuspendActorTurn(a, actorA, turn),
            "suspension with live nested state loan");
        ExpectFailure(() => host.CompleteActorTurn(a, actorA, turn),
            "completion with live state loan");
        host.EndActorStateLoan(a, actorA, turn, readLoan);
        host.ValidateActorStateLoan(a, actorA, turn, writeLoan, requireWrite: true);
        host.EndActorStateLoan(a, actorA, turn, writeLoan);
        ExpectFailure(() => host.ValidateActorStateLoan(a, actorA, turn, writeLoan),
            "retired state loan replay");
        host.SuspendActorTurn(a, actorA, turn);
        ExpectFailure(() => host.BeginActorStateLoan(a, actorA, turn),
            "suspended actor turn cannot grant state loans");
        turnSnapshot = host.ActorTurnSnapshot(a, actorA);
        if (!turnSnapshot.Active || !turnSnapshot.Suspended)
            throw new Exception("suspended actor logical turn lost ownership");
        ExpectFailure(() => host.BeginActorTurn(a, actorA),
            "later user turn entered while predecessor suspended");
        ExpectFailure(() => host.ResumeActorTurn(b, actorB, turn),
            "foreign actor resumed logical turn");
        ExpectFailure(() => host.RetireActor(a, actorA),
            "actor retired with suspended logical turn");
        host.ResumeActorTurn(a, actorA, turn);
        var resumedLoan = host.BeginActorStateLoan(a, actorA, turn);
        if (resumedLoan.Generation == writeLoan.Generation)
            throw new Exception("resumed actor state loan generation replay");
        host.EndActorStateLoan(a, actorA, turn, resumedLoan);
        host.CompleteActorTurn(a, actorA, turn);
        ExpectFailure(() => host.ResumeActorTurn(a, actorA, turn),
            "completed logical turn replay");
        var nextTurn = host.BeginActorTurn(a, actorA);
        if (nextTurn.Generation == turn.Generation)
            throw new Exception("actor logical turn generation replay");
        var cancelParent = host.BeginActorStateLoan(a, actorA, nextTurn, writable: true);
        var cancelChild = host.BeginActorStateLoan(a, actorA, nextTurn);
        host.CancelActorTurn(a, actorA, nextTurn);
        ExpectFailure(() => host.ValidateActorStateLoan(a, actorA, nextTurn, cancelParent),
            "cancelled parent loan survived");
        ExpectFailure(() => host.ValidateActorStateLoan(a, actorA, nextTurn, cancelChild),
            "cancelled child loan survived");
        ExpectFailure(() => host.EndActorStateLoan(a, actorA, nextTurn, cancelChild),
            "cancelled state-loan scope replay");
        if (host.ActorTurnSnapshot(a, actorA).Active)
            throw new Exception("cancelled actor logical turn remained active");
        ExpectFailure(() => host.ResumeActorTurn(a, actorA, nextTurn),
            "cancelled logical turn resumed");
        ExpectFailure(() => host.CompleteActorTurn(a, actorA, nextTurn),
            "cancelled logical turn completed twice");
        ExpectFailure(() => host.CancelActorTurn(a, actorA, nextTurn),
            "cancelled logical turn cancelled twice");
        var postCancelTurn = host.BeginActorTurn(a, actorA);
        var postCancelLoan = host.BeginActorStateLoan(a, actorA, postCancelTurn);
        ExpectFailure(() => host.ValidateActorStateLoan(a, actorA, postCancelTurn, cancelChild),
            "cancelled loan crossed actor turn generations");
        host.EndActorStateLoan(a, actorA, postCancelTurn, postCancelLoan);
        if (postCancelTurn.Generation == nextTurn.Generation)
            throw new Exception("cancelled actor logical turn generation replay");
        host.CompleteActorTurn(a, actorA, postCancelTurn);
        if (host.ActorTurnSnapshot(a, actorA).Active)
            throw new Exception("completed actor logical turn remained active");

        var pin = host.PinResource(a, actorA);
        ExpectFailure(() => host.PinResource(a, actorA), "pin quota");
        ExpectFailure(() => host.RetireResource(b, actorB, pin), "foreign tenant pin");
        ExpectFailure(() => host.RetireActor(a, actorA), "actor lifetime pin");
        host.BeginShutdown(a);
        ExpectFailure(() => host.InvokeGeneratedScalar(a, () => 1),
            "guest admission must close at quiescence");
        ExpectFailure(() => host.PinResource(a, actorA), "quiescent admission");
        ExpectFailure(() => host.SendActor(a, actorA, refA, new byte[] { 9 }),
            "quiescent mailbox admission");
        ExpectFailure(() => host.CompleteShutdown(a), "drain outstanding actor");
        host.RetireResource(a, actorA, pin);
        ExpectFailure(() => host.RetireResource(a, actorA, pin), "replay ticket");
        host.RetireActor(a, actorA);
        if (actorA.Mailbox.QueuedMessages != 0)
            throw new Exception("retire must drain pending mailbox-owned copies");
        ExpectFailure(() => host.SendActor(a, actorA, refA, new byte[] { 4 }), "stale actor ref");
        host.CompleteShutdown(a); host.Destroy(a);
        ExpectFailure(() => host.Snapshot(a), "stale tenant handle");
        var a2 = host.CreateIsolate("tenant-a", maxActors: 1);
        if (a2.Generation == a.Generation) throw new Exception("isolate epoch replay");
        host.Start(a2);
        ExpectFailure(() => host.RetireActor(a2, actorA), "actor from old generation");
        var replacement = host.RegisterActor(a2, 3);
        host.RetireActor(a2, replacement);
        host.BeginShutdown(a2); host.CompleteShutdown(a2); host.Destroy(a2);
        // Concurrent physical guest invocation must pin the tenant until the
        // generated host step completes, even when shutdown has begun.
        var isolateVm = new OresVmHost(maxTenants: 1);
        var executing = isolateVm.CreateIsolate("busy");
        isolateVm.Start(executing);
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var active = Task.Run(() => isolateVm.InvokeGeneratedScalar(executing, () => {
            entered.Set();
            if (!finish.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("native generated step did not retire");
            return 7;
        }));
        if (!entered.Wait(TimeSpan.FromSeconds(10)))
            throw new Exception("native tenant test guest never started");
        try {
            isolateVm.BeginShutdown(executing);
            ExpectFailure(() => isolateVm.CompleteShutdown(executing),
                "inflight generated guest pin must block shutdown");
            if (isolateVm.GuestSnapshot(executing).GuestCalls != 1)
                throw new Exception("guest execution lifetime was not pinned");
        }
        finally { finish.Set(); }
        if (active.GetAwaiter().GetResult() != 7)
            throw new Exception("running guest could not commit during quiescence");
        isolateVm.CompleteShutdown(executing);
        isolateVm.Destroy(executing);

        var overflowVm = new OresVmHost(maxTenants: 1);
        var over = overflowVm.CreateIsolate("overflow");
        overflowVm.Start(over);
        if (overflowVm.InvokeGeneratedScalar(over, () => long.MaxValue) != long.MaxValue)
            throw new Exception("incorrect i64 max value");
        try {
            overflowVm.InvokeGeneratedScalar(over, () => 1);
            throw new Exception("expected checked tenant scalar overflow");
        } catch (OverflowException) { }
        if (overflowVm.GuestSnapshot(over).Scalar != long.MaxValue ||
            overflowVm.GuestSnapshot(over).GuestCalls != 0)
            throw new Exception("overflow corrupted tenant state or left guest pinned");
        overflowVm.BeginShutdown(over);overflowVm.CompleteShutdown(over);overflowVm.Destroy(over);
        host.RetireActor(b, actorB);
        host.BeginShutdown(b); host.CompleteShutdown(b); host.Destroy(b);
    }
}
