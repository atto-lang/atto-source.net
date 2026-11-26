namespace Ores.Net.Runtime;

/// <summary>
/// Mirrors the fail-closed policy in Java ActorHeapAdmission:
/// actor-private native bytes are not proof of zero *guest-tracing*,
/// whole-process zero GC, or protected tenant isolation.
/// This is an admission gate, not a compiler lowering witness.
/// </summary>
public static class ActorHeapAdmissionV1
{
    public enum Backing
    {
        JvmObjectHeap,
        JvmDirectByteBuffer,
        NativeImageJavaIsolate,
        ExplicitNativeRegion,
        LlvmNativeArena
    }

    /// <summary>
    /// A descriptive proof checklist, deliberately NOT an authority token.
    /// Callers can set every flag to true; this never enables strict mode.
    /// </summary>
    public readonly record struct OwnershipProof(
        bool AllGuestAllocationsLowered,
        bool CheckedUniqueOwnershipAndBorrows,
        bool DeterministicDropOnAllExits,
        bool NoImplicitStrongOwnershipCycles,
        bool ActorAndAsyncQuiescenceFenced,
        bool NativeAndFfiLeasesFenced,
        bool PhysicalReleaseIsDeterministic,
        bool NoTracedForeignHeapReferences)
    {
        public bool Complete =>
            AllGuestAllocationsLowered &&
            CheckedUniqueOwnershipAndBorrows &&
            DeterministicDropOnAllExits &&
            NoImplicitStrongOwnershipCycles &&
            ActorAndAsyncQuiescenceFenced &&
            NativeAndFfiLeasesFenced &&
            PhysicalReleaseIsDeterministic &&
            NoTracedForeignHeapReferences;
    }

    /// <summary>
    /// Parity with Java: no public proof value can activate strict guest
    /// tracing elimination. Even NativeAOT/LLVM arenas need an unforgeable
    /// compiler/runtime lowering witness and all-exit/drop guarantees.
    /// </summary>
    public static void RequireZeroGuestTracing(
        Backing backing, OwnershipProof proof)
    {
        if (!Enum.IsDefined(backing))
            throw new ArgumentOutOfRangeException(nameof(backing));
        if (backing is not (Backing.ExplicitNativeRegion or Backing.LlvmNativeArena))
            throw new InvalidOperationException(
                "A managed or isolate heap cannot establish strict zero guest tracing");
        if (!proof.Complete)
            throw new InvalidOperationException(
                "Strict ownership requires checked borrows, native drop on every exit, " +
                "cycle policy, async/FFI quiescence and explicit native release");
        throw new InvalidOperationException(
            "Strict zero-guest-tracing is unavailable without an unforgeable " +
            "compiler/runtime object-lowering witness");
    }

    /// <summary>
    /// Host policy only. A .NET actor execution pool is not a tenant
    /// isolate, OS process sandbox, or protected VM memory boundary.
    /// </summary>
    public static void RequireSupportedTenantIsolationMode(string requestedMode)
    {
        ArgumentNullException.ThrowIfNull(requestedMode);
        switch (requestedMode.Trim().ToLowerInvariant())
        {
            case "trusted-inprocess":
                return;
            case "vm-isolate":
            case "dedicated-graalvm-isolate":
            case "sandboxed-process":
            case "wasmtime":
                throw new InvalidOperationException(
                    "A .NET actor arena or untrusted worker pool is not a " +
                    "dedicated protected tenant isolate/process");
            default:
                throw new ArgumentException(
                    "Unknown actor tenant isolation mode", nameof(requestedMode));
        }
    }

    /// <summary>
    /// Java/GraalVM supports a hybrid/ownership-first runtime policy only.
    /// .NET NativeAOT also has managed metadata, scheduler and runtime code,
    /// so a native guest payload arena does not certify whole-guest zero GC.
    /// </summary>
    public static void RequireSupportedManagedMode(string requestedMode)
    {
        ArgumentNullException.ThrowIfNull(requestedMode);
        switch (requestedMode.Trim().ToLowerInvariant())
        {
            case "hybrid":
            case "ownership-first":
                return;
            case "strict-no-gc":
            case "zero-gc":
            case "manual-only":
                throw new InvalidOperationException(
                    "Strict zero-GC is not certified for the .NET actor runtime; " +
                    "typed native guest payloads do not establish whole-guest zero GC");
            default:
                throw new ArgumentException(
                    "Unknown actor managed-heap policy", nameof(requestedMode));
        }
    }
}
