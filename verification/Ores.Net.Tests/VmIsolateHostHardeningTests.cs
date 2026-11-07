using Ores.Net.Runtime;

// Audit regressions for OresVmHost. Each case failed against the code it fixes.
internal static class VmIsolateHostHardeningTests
{
    public static void Run()
    {
        QueuedCallerRejectedWhileTurnRuns();
        TenantAndHostMailQuotas();
        FaultDiscardsTenantState();
        Console.WriteLine("PASS .NET tenant host hardening: queued callers fail fast, tenant and host mail quotas, fault state discarded");
    }

    // A caller queued behind a running turn must be rejected promptly when the
    // tenant stops admitting work, not wait for the running turn to return.
    private static void QueuedCallerRejectedWhileTurnRuns()
    {
        var host = new OresVmHost(maxTenants: 1);
        var t = host.CreateIsolate("queued");
        host.Start(t);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? firstError = null, queuedError = null;
        var first = new Thread(() => {
            try { host.InvokeGeneratedScalar(t, () => { entered.Set(); release.Wait(); return 1; }); }
            catch (Exception e) { firstError = e; }
        });
        first.Start();
        if (!entered.Wait(5000)) throw new Exception("first turn never started");
        var queued = new Thread(() => {
            try { host.InvokeGeneratedScalar(t, () => 100); }
            catch (Exception e) { queuedError = e; }
        });
        queued.Start();
        Thread.Sleep(50);
        host.BeginShutdown(t);
        bool rejected = queued.Join(2000);
        release.Set();
        first.Join();
        queued.Join();
        if (!rejected) throw new Exception("FAILED: queued caller stayed blocked behind a running turn after shutdown");
        if (queuedError is not InvalidOperationException)
            throw new Exception("FAILED: queued caller was not rejected: " + queuedError);
        if (firstError is not null) throw new Exception("FAILED: running turn must drain and commit: " + firstError);
        if (host.GuestSnapshot(t).Scalar != 1) throw new Exception("FAILED: queued work must not commit");
        host.CompleteShutdown(t); host.Destroy(t);
    }

    private static void Expect<TEx>(Action a, string what) where TEx : Exception
    {
        try { a(); } catch (TEx) { return; }
        throw new Exception("FAILED: expected rejection: " + what);
    }

    // Per-mailbox caps alone let one tenant queue unbounded mail across many
    // actors. Tenant and host byte quotas bound it, and receive/retire refund.
    private static void TenantAndHostMailQuotas()
    {
        var host = new OresVmHost(maxTenants: 2, maxQueuedMailBytes: 1500);
        var a = host.CreateIsolate("mail-a", maxQueuedMailBytes: 1000);
        var b = host.CreateIsolate("mail-b", maxQueuedMailBytes: 1000);
        host.Start(a); host.Start(b);
        var a1 = host.RegisterActor(a, 1); var a2 = host.RegisterActor(a, 2); var a3 = host.RegisterActor(a, 3);
        var four = new byte[400];
        host.SendActor(a, a1, a1.Mailbox.CurrentRef, four);
        host.SendActor(a, a2, a2.Mailbox.CurrentRef, four);
        Expect<InvalidOperationException>(() => host.SendActor(a, a3, a3.Mailbox.CurrentRef, four),
            "third actor exceeds the tenant mail quota");
        if (!host.TryReceiveActor(a, a1, out var got) || got!.Length != 400)
            throw new Exception("FAILED: host receive must return the queued message");
        host.SendActor(a, a3, a3.Mailbox.CurrentRef, four); // refunded by the receive
        var b1 = host.RegisterActor(b, 1);
        host.SendActor(b, b1, b1.Mailbox.CurrentRef, new byte[600]);
        Expect<InvalidOperationException>(() => host.SendActor(b, b1, b1.Mailbox.CurrentRef, new byte[200]),
            "host mail quota (800 + 600 + 200 > 1500)");
        if (host.QueuedMail(a) != (800, 1400)) throw new Exception("FAILED: mail accounting " + host.QueuedMail(a));
        host.RetireActor(a, a2);
        if (host.QueuedMail(a) != (400, 1000)) throw new Exception("FAILED: retire must refund queued mail");
        host.SendActor(b, b1, b1.Mailbox.CurrentRef, new byte[200]);
        Expect<ArgumentOutOfRangeException>(() => host.CreateIsolate("bad", maxQueuedMailBytes: 0), "zero tenant mail quota");
        Expect<ArgumentOutOfRangeException>(() => new OresVmHost(maxQueuedMailBytes: 0), "zero host mail quota");
    }

    // After a guest fault, the incarnation's state is discarded and the
    // reason is recorded.
    private static void FaultDiscardsTenantState()
    {
        var host = new OresVmHost(maxTenants: 2);
        var bad = host.CreateIsolate("faulting");
        var good = host.CreateIsolate("healthy");
        host.Start(bad); host.Start(good);
        host.InvokeGeneratedScalar(bad, () => 41);
        host.InvokeGeneratedScalar(good, () => 7);
        Expect<InvalidCastException>(() => host.InvokeGeneratedScalar(bad, () => throw new InvalidCastException()),
            "guest fault propagates");
        if (host.EndReason(bad) != "guest-fault") throw new Exception("FAILED: fault reason not recorded");
        if (host.GuestSnapshot(bad).Scalar != 0) throw new Exception("FAILED: faulted state must be discarded");
        if (host.EndReason(good) is not null || host.GuestSnapshot(good).Scalar != 7)
            throw new Exception("FAILED: neighbor tenant affected by fault");
        Expect<InvalidOperationException>(() => host.InvokeGeneratedScalar(bad, () => 1), "faulted tenant refuses work");
        host.CompleteShutdown(bad); host.Destroy(bad);
        var again = host.CreateIsolate("faulting");
        if (host.EndReason(again) is not null) throw new Exception("FAILED: fault reason leaked into new incarnation");
    }
}
