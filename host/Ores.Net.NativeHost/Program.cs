using Ores.Net.Runtime;

// Strict Native AOT: the generated OresProgram.Run CIL method is statically
// linked at publish. No reflection emission or dynamic assembly loading.
if (args.Length == 1 && args[0] == "--isolate-smoke")
{
    // This smoke mode intentionally targets examples/hello.ores (result 14).
    // It proves that the actual generated CIL is executed under different
    // tenant host contexts, NOT general stateful Oreslang actor lowering.
    var vm = new OresVmHost(maxTenants: 2);
    var a = vm.CreateIsolate("tenant-a", maxActors: 1);
    var b = vm.CreateIsolate("tenant-b", maxActors: 1);
    vm.Start(a); vm.Start(b);
    if (vm.InvokeGeneratedScalar(a, Oreslang.Generated.OresProgram.Run) != 14 ||
        vm.InvokeGeneratedScalar(b, Oreslang.Generated.OresProgram.Run) != 14 ||
        vm.InvokeGeneratedScalar(a, Oreslang.Generated.OresProgram.Run) != 28 ||
        vm.GuestSnapshot(a).Scalar != 28 ||
        vm.GuestSnapshot(b).Scalar != 14)
        throw new InvalidOperationException("Native AOT tenant entry state leaked");

    vm.BeginShutdown(a);
    try {
        vm.InvokeGeneratedScalar(a, Oreslang.Generated.OresProgram.Run);
        throw new InvalidOperationException("closed guest admission was allowed");
    } catch (InvalidOperationException e) when (e.Message.Contains("admission closed")) { }
    if (vm.InvokeGeneratedScalar(b, Oreslang.Generated.OresProgram.Run) != 28)
        throw new InvalidOperationException("neighbor tenant stopped making progress");
    vm.CompleteShutdown(a); vm.Destroy(a);
    var next = vm.CreateIsolate("tenant-a", maxActors: 1);
    vm.Start(next);
    if (vm.InvokeGeneratedScalar(next, Oreslang.Generated.OresProgram.Run) != 14)
        throw new InvalidOperationException("recreated tenant inherited stale scalar state");
    vm.BeginShutdown(next);vm.CompleteShutdown(next);vm.Destroy(next);
    vm.BeginShutdown(b);vm.CompleteShutdown(b);vm.Destroy(b);
    await Console.Out.WriteLineAsync("native-aot-isolate-smoke: 5 statically linked CIL guest calls passed");
    return 0;
}
if (args.Length != 0)
{
    await Console.Error.WriteLineAsync("unknown native host argument");
    return 2;
}
// Existing default application behavior is unchanged.
return await RuntimeHost.ExecuteAsync(Oreslang.Generated.OresProgram.Run);
