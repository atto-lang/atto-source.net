using Ores.Net.Runtime;

// Generated CIL is a static NativeAOT link-time reference in both modes.
if (args.Length == 0)
    return RuntimeHost.Execute(Oreslang.Generated.OresProgram.Run);

if (args.Length == 1 && args[0] == "--worker")
{
    using var input = Console.OpenStandardInput();
    using var output = Console.OpenStandardOutput();
    return WorkerRuntime.Run(
        Oreslang.Generated.OresProgram.Run,
        input,
        output,
        Console.Error);
}

Console.Error.WriteLine("usage: oreslang-app [--worker]");
return 2;
