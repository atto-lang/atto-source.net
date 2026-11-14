using Ores.Net.Runtime;
using System.Threading;

internal static class NativeTraceInstrumentationV1SelfTest
{
    private static int checks;
    private static void Check(bool yes, string why)
    {
        ++checks;
        if (!yes) throw new Exception("NativeAOT tracing policy: " + why);
    }
    private static void Deny<T>(Action action, string why) where T : Exception
    {
        ++checks;
        try { action(); }
        catch (T) { return; }
        throw new Exception("Missing safety denial: " + why);
    }
    private static NativeTraceCodeIdentityV1 Identity(string tenant = "tenant-a") =>
        new(tenant, "checkout", new string('a',64), new string('b',64),
            new string('c',64), new string('d',64), new string('e',64));

    internal static void Run()
    {
        var none = new NativeTraceInstrumentationV1(
            NativeTraceInstrumentationV1.BuildMode.None);
        Check(!none.Enabled(NativeTraceInstrumentationV1.Boundary),
            "stripped binary remains disabled");
        Deny<ArgumentOutOfRangeException>(() =>
            none.Configure(NativeTraceInstrumentationV1.Boundary, true),
            "none cannot activate missing sites");
        Deny<UnauthorizedAccessException>(() => none.Configure(0, false),
            "guest cannot configure even off mode");
        Check(none.Configure(0, true) == 1, "host-only explicit version");
        Check(!none.HasStaticSite(NativeTraceInstrumentationV1.Boundary),
            "none never pretends to contain a static boundary");

        var boundaries = new NativeTraceInstrumentationV1(
            NativeTraceInstrumentationV1.BuildMode.Boundaries);
        Check(boundaries.HasStaticSite(NativeTraceInstrumentationV1.Boundary) &&
            !boundaries.HasStaticSite(NativeTraceInstrumentationV1.Call),
            "AOT boundary ceiling distinct from function coverage");
        Check(boundaries.Configure(NativeTraceInstrumentationV1.Boundary, true) == 1 &&
            boundaries.Enabled(NativeTraceInstrumentationV1.Boundary) &&
            !boundaries.Enabled(NativeTraceInstrumentationV1.Call),
            "only precompiled boundaries activate");
        Deny<ArgumentOutOfRangeException>(() =>
            boundaries.Configure(NativeTraceInstrumentationV1.Call, true),
            "uncompiled function probe denied");
        Deny<ArgumentOutOfRangeException>(() => boundaries.Configure(-1, true),
            "invalid mask rejected");
        Check(boundaries.Version == 1, "denied configuration does not advance epoch");
        boundaries.Configure(0, true);
        Check(!boundaries.Enabled(NativeTraceInstrumentationV1.Boundary),
            "disable takes effect immediately");

        var adaptive = new NativeTraceInstrumentationV1(
            NativeTraceInstrumentationV1.BuildMode.Adaptive);
        Check(adaptive.CanRecompile &&
            !adaptive.HasStaticSite(NativeTraceInstrumentationV1.Call),
            "adaptive JIT mode is not proof that AOT sites exist");
        adaptive.Configure(NativeTraceInstrumentationV1.Boundary |
            NativeTraceInstrumentationV1.Call, true);
        var baselineHits = 0;
        var aotHits = 0;
        var jitHits = 0;
        Func<long,long> baseline = x => { Interlocked.Increment(ref baselineHits); return x + 1; };
        Func<long,long> aot = x => { Interlocked.Increment(ref aotHits); return x + 1; };
        Func<long,long> jit = x => { Interlocked.Increment(ref jitHits); return x + 1; };
        var id = Identity();
        var slot = new NativeTraceVersionedDispatchV1<Func<long,long>>(
            adaptive, id, 37, baseline, hasPrecompiledAotProbes: true,
            hasNativeJitProvider: true, hardRealtime: false,
            maxLiveGenerations: 3, maxPublications: 3);
        var aotCandidate = new NativeTraceVersionedDispatchV1<Func<long,long>>.Candidate(
            id, 2, NativeTraceInstrumentationV1.Boundary, aot, true);
        var jitCandidate = new NativeTraceVersionedDispatchV1<Func<long,long>>.Candidate(
            id, 3, NativeTraceInstrumentationV1.Call, jit, false);
        using var older = slot.Pin();
        Check(older.Executable(41) == 42 && slot.ActiveGeneration == 1,
            "baseline executable pinned");
        Check(slot.Publish(aotCandidate, false, 37, false, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Unauthorized,
            "untrusted host request cannot install AOT probes");
        Check(slot.Publish(aotCandidate, false, 37, true, false) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Unauthorized,
            "unverified build-bound signature refused");
        Check(slot.Publish(aotCandidate, false, 36, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.StaleGeneration,
            "supervisor epoch fenced");
        Check(slot.Publish(aotCandidate, false, 37, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Accepted,
            "precompiled AOT replacement published");
        using var aotLease = slot.Pin();
        Check(aotLease.Generation == 2 && aotLease.Executable(41) == 42,
            "new call uses AOT traced version");
        Check(older.Generation == 1 && older.Executable(41) == 42,
            "suspended old invocation keeps immutable AOT target");
        Check(slot.Publish(aotCandidate, false, 37, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.StaleGeneration,
            "replay old version denied");
        Check(slot.Publish(new(id with { OwnershipSha256 = new string('f',64) }, 3,
             NativeTraceInstrumentationV1.Call, jit, false), true, 37, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Incompatible,
            "borrow/ownership mismatch denied");
        Check(slot.Publish(new(Identity("other-tenant"), 3,
             NativeTraceInstrumentationV1.Call, jit, false), true, 37, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Incompatible,
            "foreign tenant denied");
        Check(slot.Publish(jitCandidate, true, 37, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Accepted,
            "authenticated native JIT variant published");
        using var deepLease = slot.Pin();
        Check(deepLease.Generation == 3 && deepLease.Executable(41) == 42,
            "deep variant activated without changing user result");
        var migrated = false;
        var carrier = new Thread(() => migrated = deepLease.Executable(41) == 42);
        carrier.Start(); carrier.Join();
        Check(migrated && deepLease.Generation == 3,
            "logical lease survives carrier migration without AsyncLocal/TLS");
        slot.Disable();
        using var disabled = slot.Pin();
        Check(disabled.Generation == 1 && disabled.Executable(41) == 42 &&
            deepLease.Executable(41) == 42,
            "new calls use baseline; in-flight JIT scope still completes");
        Check(baselineHits == 3 && aotHits == 1 && jitHits == 3,
            "exact number of real invocations on each version");

        var ahead = new NativeTraceVersionedDispatchV1<Func<long,long>>.Candidate(
            id, 4, NativeTraceInstrumentationV1.Call, jit, false);
        Check(slot.Publish(ahead, true, 37, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.ResourceLimit,
            "retained-generation cap blocks unbounded JIT churn");
        aotLease.Dispose();
        deepLease.Dispose();
        // Baseline remains pinned; retired variants can now be reclaimed.
        Check(slot.RetainedGenerations == 1, "generation leases released and reclaimed");
        Check(slot.Publish(ahead, true, 37, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Accepted,
            "budget permits new generation after reclaim");
        Check(slot.PublicationAttempts == 3, "bounded accepted generation budget");
        Check(slot.Publish(new(id, 5, NativeTraceInstrumentationV1.Call, jit, false),
             true, 37, true, true) ==
             NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.ResourceLimit,
             "publication budget exhausted despite freeing old generation");
        slot.Retire();
        Check(slot.Publish(new(id, 6, NativeTraceInstrumentationV1.Call, jit, false),
             true, 37, true, true) ==
             NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Retired,
             "retired actor generation cannot resurrect code");
        Deny<InvalidOperationException>(() => slot.Pin(), "retire fences new calls");
        Check(older.Executable(41) == 42 && disabled.Executable(41) == 42,
            "existing leases keep working through retirement");

        var rt = new NativeTraceVersionedDispatchV1<Func<long,long>>(
            adaptive, id, 10, baseline, true, true, true);
        Check(rt.Publish(jitCandidate, true, 10, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Unsupported,
            "hard-real-time profile refuses runtime native JIT");
        var noJit = new NativeTraceVersionedDispatchV1<Func<long,long>>(
            boundaries, id, 10, baseline, true, false, false);
        Check(noJit.Publish(jitCandidate, true, 10, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Unsupported,
            "full AOT without native provider refuses deep compilation");
        Check(noJit.Publish(aotCandidate, false, 10, true, true) ==
            NativeTraceVersionedDispatchV1<Func<long,long>>.PublishResult.Unauthorized,
            "active probe mask must be enabled by host policy");
        Check(!Identity(" ").Valid(), "blank tenant identity denied");
        Check(!(id with { TypedIrSha256 = new string('A',64) }).Valid(),
            "noncanonical typed IR fingerprint denied");
        Console.WriteLine("PASS NET_NATIVE_AOT_TRACE_VERSIONED_DISPATCH_V1:" + checks);
    }
}
