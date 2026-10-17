using System.Security;
using Ores.Net.Runtime;

static class IsolationPolicySelfTest
{
    public static void Run()
    {
        var trusted = IsolationPolicy.Admit(
            WorkloadTrust.Trusted,
            IsolationKind.SameProcess);
        Require(trusted.NativeAotCompatible, "trusted same-process NativeAOT admitted");
        Require(!trusted.SecurityBoundary, "same process is not mislabeled as security isolation");

        RejectSecurity(
            () => IsolationPolicy.Admit(
                WorkloadTrust.SemiTrusted,
                IsolationKind.SameProcess),
            "semi-trusted code cannot run without a security boundary");
        RejectSecurity(
            () => IsolationPolicy.Admit(
                WorkloadTrust.Untrusted,
                IsolationKind.SameProcess),
            "untrusted code cannot run without a security boundary");

        RejectPlatform(
            () => IsolationPolicy.Admit(
                WorkloadTrust.Trusted,
                IsolationKind.AssemblyLoadContextDevelopmentOnly,
                strictNativeAotRelease: true),
            "strict NativeAOT release rejects AssemblyLoadContext");

        var alc = IsolationPolicy.Admit(
            WorkloadTrust.Trusted,
            IsolationKind.AssemblyLoadContextDevelopmentOnly,
            strictNativeAotRelease: false);
        Require(!alc.SecurityBoundary && !alc.NativeAotCompatible,
            "development ALC is unload/version isolation only");
        RejectSecurity(
            () => IsolationPolicy.Admit(
                WorkloadTrust.Untrusted,
                IsolationKind.AssemblyLoadContextDevelopmentOnly,
                strictNativeAotRelease: false),
            "ALC cannot sandbox untrusted code");

        var semi = IsolationPolicy.Admit(
            WorkloadTrust.SemiTrusted,
            IsolationKind.WorkerProcess);
        Require(semi.SecurityBoundary && semi.NativeAotCompatible && semi.RequiresIpc,
            "worker process is the CIL/NativeAOT security boundary");
        Require(!semi.RequiresOsSandbox,
            "semi-trusted process does not automatically claim hostile-code OS sandboxing");

        var hostile = IsolationPolicy.Admit(
            WorkloadTrust.Untrusted,
            IsolationKind.WorkerProcess);
        Require(hostile.SecurityBoundary && hostile.RequiresOsSandbox,
            "untrusted NativeAOT worker also requires OS capability sandboxing");

        var wasm = IsolationPolicy.Admit(
            WorkloadTrust.Untrusted,
            IsolationKind.WasmSandbox);
        Require(wasm.SecurityBoundary && !wasm.NativeAotCompatible &&
                wasm.ArtifactKind == "wasm32-wasi",
            "untrusted portable path is a separate WASM artifact, not CIL-in-ALC");
    }

    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("FAILED: " + why);
    }

    private static void RejectSecurity(Action action, string why)
    {
        try { action(); }
        catch (SecurityException) { return; }
        throw new Exception("Expected SecurityException: " + why);
    }

    private static void RejectPlatform(Action action, string why)
    {
        try { action(); }
        catch (PlatformNotSupportedException) { return; }
        throw new Exception("Expected PlatformNotSupportedException: " + why);
    }
}
