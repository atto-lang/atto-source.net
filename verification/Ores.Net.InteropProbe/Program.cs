await HostTaskObservationTests.Run();
await RootTaskI64Tests.Run();
Console.WriteLine("NET_ROOT_TASK_I64_RUNTIME_EXECUTED; full_interop_verified=false");
Console.WriteLine("NET_HOST_OBSERVER_I01_SUBSET_EXECUTED; race_iterations=128; guest_verified=false");
