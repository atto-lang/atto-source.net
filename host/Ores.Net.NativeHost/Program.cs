using Ores.Net.Runtime;

// Generated CIL is a static NativeAOT link-time reference in both modes.
if (args.Length == 0)
    return RuntimeHost.Execute(Oreslang.Generated.OresProgram.Run);

if (args.Length == 1 && args[0] == "--worker")
{
    try
    {
        var settings = WorkerRuntimeSettings.FromEnvironment(
            Environment.GetEnvironmentVariable);
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        return WorkerRuntime.Run(
            Oreslang.Generated.OresProgram.Run,
            input,
            output,
            Console.Error,
            settings: settings);
    }
    catch (Exception ex) when (
        ex is FormatException or ArgumentOutOfRangeException)
    {
        Console.Error.WriteLine("oreslang worker: invalid runtime settings");
        return WorkerRuntime.ProtocolFailureExitCode;
    }
}

Console.Error.WriteLine("usage: oreslang-app [--worker]");
return 2;
