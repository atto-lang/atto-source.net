using Ores.Net.Runtime;

internal static class ActorHeapAdmissionParitySelfTest
{
    public static void Run()
    {
        // Java ActorHeapAdmission.requireSupportedJvmMode accepts hybrid and
        // ownership-first, but never asserts a managed/JIT/NativeAOT runtime
        // is globally free from tracing or managed control-plane allocations.
        foreach (string mode in new[] { "hybrid", "ownership-first", "  OwNeRsHiP-FiRsT  " })
            ActorHeapAdmissionV1.RequireSupportedManagedMode(mode);
        ActorHeapAdmissionV1.RequireSupportedTenantIsolationMode(
            " trusted-INPROCESS ");

        // Real runtime construction must also reject unsupported security
        // claims BEFORE dispatcher and budget initialisation.
        using (var hybrid = new ActorRuntime(new ActorRuntimeOptions
               {
                   GcMode = "hybrid",
                   TenantIsolationMode = "trusted-inprocess"
               }))
            Require(hybrid.LiveActors == 0 && hybrid.StartedCarrierThreads == 0 &&
                    hybrid.NativeReservedBytes == 0,
                "valid hybrid mode starts with no actors, carriers or native slabs");
        using (var ownershipFirst = new ActorRuntime(new ActorRuntimeOptions
               {
                   GcMode = " ownership-FIRST ",
                   TenantIsolationMode = "trusted-inprocess"
               }))
            Require(ownershipFirst.StartedCarrierThreads == 0,
                "ownership-first startup does not launch actor carriers");

        foreach (string unsupported in new[]
                 {
                     "strict-no-gc", "zero-gc", "manual-only",
                     " Strict-No-Gc "
                 })
        {
            RejectInvalid(() =>
                ActorHeapAdmissionV1.RequireSupportedManagedMode(unsupported),
                "strict GC claim rejected by .NET admission policy");
            RejectInvalid(() => _ = new ActorRuntime(
                    new ActorRuntimeOptions { GcMode = unsupported }),
                "unsupported GC claim rejected before .NET actor runtime startup");
        }

        foreach (string unsupported in new[]
                 {
                     "vm-isolate", "dedicated-graalvm-isolate",
                     "sandboxed-process", "wasmtime", " Wasmtime "
                 })
        {
            RejectInvalid(() =>
                ActorHeapAdmissionV1.RequireSupportedTenantIsolationMode(
                    unsupported),
                "tenant isolation must not be inferred from .NET actor arenas");
            RejectInvalid(() => _ = new ActorRuntime(
                    new ActorRuntimeOptions
                    {
                        TenantIsolationMode = unsupported
                    }),
                "unimplemented process/isolate claim rejected on runtime startup");
        }

        RejectArgument(() =>
            ActorHeapAdmissionV1.RequireSupportedManagedMode("jvm-epsilon"),
            "unknown managed mode must fail, not silently normalize");
        RejectArgument(() =>
            ActorHeapAdmissionV1.RequireSupportedTenantIsolationMode(
                "untrusted-worker"),
            "untrusted worker pool is not a recognized tenant sandbox");
        RejectArgument(() =>
            ActorHeapAdmissionV1.RequireSupportedManagedMode(""),
            "empty GC mode cannot default to a permissive value");
        RejectArgument(() =>
            ActorHeapAdmissionV1.RequireSupportedTenantIsolationMode(" "),
            "empty tenant isolation claim is invalid");
        RejectNull(() =>
            ActorHeapAdmissionV1.RequireSupportedManagedMode(null!),
            "null GC mode cannot pass admission");
        RejectNull(() =>
            ActorHeapAdmissionV1.RequireSupportedTenantIsolationMode(null!),
            "null isolation mode cannot pass admission");

        var allFalse = default(ActorHeapAdmissionV1.OwnershipProof);
        var forgedAllTrue = new ActorHeapAdmissionV1.OwnershipProof(
            AllGuestAllocationsLowered: true,
            CheckedUniqueOwnershipAndBorrows: true,
            DeterministicDropOnAllExits: true,
            NoImplicitStrongOwnershipCycles: true,
            ActorAndAsyncQuiescenceFenced: true,
            NativeAndFfiLeasesFenced: true,
            PhysicalReleaseIsDeterministic: true,
            NoTracedForeignHeapReferences: true);
        Require(!allFalse.Complete && forgedAllTrue.Complete,
            "proof completeness is merely data, not trust authority");

        foreach (var backing in new[]
                 {
                     ActorHeapAdmissionV1.Backing.JvmObjectHeap,
                     ActorHeapAdmissionV1.Backing.JvmDirectByteBuffer,
                     ActorHeapAdmissionV1.Backing.NativeImageJavaIsolate,
                     ActorHeapAdmissionV1.Backing.ExplicitNativeRegion,
                     ActorHeapAdmissionV1.Backing.LlvmNativeArena
                 })
        {
            RejectInvalid(() => ActorHeapAdmissionV1.RequireZeroGuestTracing(
                    backing, allFalse),
                "no backing can claim strict guest-tracing without native ownership proof");
            RejectInvalid(() => ActorHeapAdmissionV1.RequireZeroGuestTracing(
                    backing, forgedAllTrue),
                "forged all-true policy flags cannot mint a compiler witness");
        }

        RejectOutOfRange(() => ActorHeapAdmissionV1.RequireZeroGuestTracing(
                (ActorHeapAdmissionV1.Backing)999, forgedAllTrue),
            "unknown backing enum never grants strict mode");

        Console.WriteLine(
            "PASS actor-heap-java-parity: managed GC, tenant isolation and forged native no-GC claims fail closed (host only)");
    }

    private static void RejectInvalid(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("ActorHeapAdmissionParitySelfTest expected refusal: " + why);
    }

    private static void RejectArgument(Action action, string why)
    {
        try { action(); }
        catch (ArgumentException ex) when (ex is not ArgumentNullException)
        {
            return;
        }
        throw new Exception("ActorHeapAdmissionParitySelfTest expected invalid argument: " + why);
    }

    private static void RejectNull(Action action, string why)
    {
        try { action(); }
        catch (ArgumentNullException) { return; }
        throw new Exception("ActorHeapAdmissionParitySelfTest expected null rejection: " + why);
    }

    private static void RejectOutOfRange(Action action, string why)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("ActorHeapAdmissionParitySelfTest expected bounds refusal: " + why);
    }

    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception("ActorHeapAdmissionParitySelfTest: " + why);
    }
}
