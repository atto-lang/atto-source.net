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
        ExpectFailure(() => host.InvokeGeneratedScalar(a, () =>
            host.InvokeGeneratedScalar(a, () => 1)), "nested guest entry");
        if (host.GuestSnapshot(a).GuestCalls != 0)
            throw new Exception("guest execution pin leaked after error");
        try { host.InvokeGeneratedScalar(a, () => throw new OverflowException()); }
        catch (OverflowException) { }
        if (host.GuestSnapshot(a).Scalar != -3)
            throw new Exception("failed guest mutated tenant state");
        var actorA = host.RegisterActor(a, 3);
        var actorB = host.RegisterActor(b, 3);
        var refA = actorA.Mailbox.CurrentRef;
        actorA.Mailbox.Send(refA, new byte[] { 1, 2, 3 });
        if (actorA.Mailbox.QueuedMessages != 1)
            throw new Exception("actor mailbox must be real, not a fake ticket");
        var pin = host.PinResource(a, actorA);
        ExpectFailure(() => host.PinResource(a, actorA), "pin quota");
        ExpectFailure(() => host.RetireResource(b, actorB, pin), "foreign tenant pin");
        ExpectFailure(() => host.RetireActor(a, actorA), "actor lifetime pin");
        host.BeginShutdown(a);
        ExpectFailure(() => host.InvokeGeneratedScalar(a, () => 1),
            "guest admission must close at quiescence");
        ExpectFailure(() => host.PinResource(a, actorA), "quiescent admission");
        ExpectFailure(() => host.CompleteShutdown(a), "drain outstanding actor");
        host.RetireResource(a, actorA, pin);
        ExpectFailure(() => host.RetireResource(a, actorA, pin), "replay ticket");
        host.RetireActor(a, actorA);
        if (actorA.Mailbox.QueuedMessages != 0)
            throw new Exception("retire must drain pending mailbox-owned copies");
        ExpectFailure(() => actorA.Mailbox.Send(refA, new byte[] { 4 }), "stale actor ref");
        host.CompleteShutdown(a); host.Destroy(a);
        ExpectFailure(() => host.Snapshot(a), "stale tenant handle");
        var a2 = host.CreateIsolate("tenant-a", maxActors: 1);
        if (a2.Generation == a.Generation) throw new Exception("isolate epoch replay");
        host.Start(a2);
        ExpectFailure(() => host.RetireActor(a2, actorA), "actor from old generation");
        var replacement = host.RegisterActor(a2, 3);
        host.RetireActor(a2, replacement);
        host.BeginShutdown(a2); host.CompleteShutdown(a2); host.Destroy(a2);
        host.RetireActor(b, actorB);
        host.BeginShutdown(b); host.CompleteShutdown(b); host.Destroy(b);
    }
}
