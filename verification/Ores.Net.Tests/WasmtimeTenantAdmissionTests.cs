using Ores.Net.Runtime;

// Admission-only regression: this is deliberately NOT a Wasmtime execution test.
// It proves the Native AOT host cannot quietly run untrusted Wasm/CIL as
// trusted in-process CIL or through the obsolete native TenantVM spike.
internal static class WasmtimeTenantAdmissionTests
{
    internal static void Run()
    {
        var vm = new OresVmHost(maxTenants: 1);
        foreach (var mode in new[]
        {
            OresVmHost.IsolationMode.WasmtimeSandbox,
            OresVmHost.IsolationMode.NativeVmIsolate,
            (OresVmHost.IsolationMode)255
        })
        {
            bool refused = false;
            try
            {
                _ = vm.CreateIsolate("tenant-a", isolation: mode);
            }
            catch (InvalidOperationException e) when (
                e.Message.Contains("not wired", StringComparison.Ordinal))
            {
                refused = true;
            }

            if (!refused)
                throw new Exception("unimplemented tenant isolation mode was admitted: " + mode);
        }

        // Refused admission must not consume tenant IDs or the host quota.
        var trusted = vm.CreateTrustedTenantVm("tenant-a", maxActors: 1);
        vm.Start(trusted);
        vm.BeginShutdown(trusted);
        vm.CompleteShutdown(trusted);
        vm.Destroy(trusted);
    }
}
