namespace Ores.Net.Runtime;

/// <summary>
/// Host-level isolation admission for Oreslang .NET.
///
/// This type intentionally distinguishes assembly/version isolation from a
/// security boundary. AssemblyLoadContext is never admitted as a sandbox.
/// </summary>
public enum WorkloadTrust
{
    Trusted,
    SemiTrusted,
    Untrusted
}

public enum IsolationKind
{
    /// <summary>Same NativeAOT process. Fastest; no security boundary.</summary>
    SameProcess,

    /// <summary>
    /// Managed development/plugin load context only. It may help dependency
    /// isolation/unloadability but is never a security boundary and is not a
    /// strict NativeAOT release mode.
    /// </summary>
    AssemblyLoadContextDevelopmentOnly,

    /// <summary>
    /// Separate Oreslang worker process. Production security boundary for CIL /
    /// NativeAOT execution; untrusted use additionally requires an OS sandbox.
    /// </summary>
    WorkerProcess,

    /// <summary>
    /// Execute a separately produced WASM artifact in a capability-limited
    /// WASM host. This does not execute arbitrary CIL inside a WASM sandbox.
    /// </summary>
    WasmSandbox
}

public sealed record IsolationAdmission(
    IsolationKind Kind,
    bool SecurityBoundary,
    bool NativeAotCompatible,
    bool RequiresIpc,
    bool RequiresOsSandbox,
    string ArtifactKind);

public static class IsolationPolicy
{
    public static IsolationAdmission Admit(
        WorkloadTrust trust,
        IsolationKind kind,
        bool strictNativeAotRelease = true)
    {
        return kind switch
        {
            IsolationKind.SameProcess => AdmitSameProcess(trust),
            IsolationKind.AssemblyLoadContextDevelopmentOnly =>
                AdmitAssemblyLoadContext(trust, strictNativeAotRelease),
            IsolationKind.WorkerProcess => AdmitWorkerProcess(trust),
            IsolationKind.WasmSandbox => AdmitWasm(trust),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static IsolationAdmission AdmitSameProcess(WorkloadTrust trust)
    {
        if (trust != WorkloadTrust.Trusted)
            throw new SecurityException(
                "semi-trusted or untrusted Oreslang code requires a security boundary");
        return new IsolationAdmission(
            IsolationKind.SameProcess,
            SecurityBoundary: false,
            NativeAotCompatible: true,
            RequiresIpc: false,
            RequiresOsSandbox: false,
            ArtifactKind: "native-aot");
    }

    private static IsolationAdmission AdmitAssemblyLoadContext(
        WorkloadTrust trust,
        bool strictNativeAotRelease)
    {
        if (strictNativeAotRelease)
            throw new PlatformNotSupportedException(
                "AssemblyLoadContext is development-only and is not admitted by strict NativeAOT release");
        if (trust != WorkloadTrust.Trusted)
            throw new SecurityException(
                "AssemblyLoadContext is not a security boundary");
        return new IsolationAdmission(
            IsolationKind.AssemblyLoadContextDevelopmentOnly,
            SecurityBoundary: false,
            NativeAotCompatible: false,
            RequiresIpc: false,
            RequiresOsSandbox: false,
            ArtifactKind: "managed-cil-development");
    }

    private static IsolationAdmission AdmitWorkerProcess(WorkloadTrust trust)
    {
        return new IsolationAdmission(
            IsolationKind.WorkerProcess,
            SecurityBoundary: true,
            NativeAotCompatible: true,
            RequiresIpc: true,
            RequiresOsSandbox: trust == WorkloadTrust.Untrusted,
            ArtifactKind: "native-aot");
    }

    private static IsolationAdmission AdmitWasm(WorkloadTrust trust)
    {
        return new IsolationAdmission(
            IsolationKind.WasmSandbox,
            SecurityBoundary: true,
            NativeAotCompatible: true,
            RequiresIpc: true,
            RequiresOsSandbox: false,
            ArtifactKind: "wasm32-wasi");
    }
}
