// Host-only runtime lifecycle coverage. Not guest-language suspension proof.
await RootTaskI64Tests.Run().WaitAsync(TimeSpan.FromSeconds(120));
Console.WriteLine("ROOT_TASK_LIFECYCLE_HOST_PASS");
