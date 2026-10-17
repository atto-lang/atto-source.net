using System.Text;
using Ores.Net.Runtime;

if (args.Length == 1 && args[0] == "--worker")
{
    try
    {
        var settings = WorkerRuntimeSettings.FromEnvironment(
            Environment.GetEnvironmentVariable);
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        return WorkerRuntime.RunText(
            Oreslang.Generated.OresProgram.Invoke,
            input,
            output,
            Console.Error,
            settings: settings);
    }
    catch (Exception ex) when (
        ex is FormatException or ArgumentOutOfRangeException)
    {
        Console.Error.WriteLine(
            "oreslang Lambda worker: invalid runtime settings");
        return WorkerRuntime.ProtocolFailureExitCode;
    }
}

if (args.Length == 2 && args[0] == "--invoke")
{
    try
    {
        var response = Oreslang.Generated.OresProgram.Invoke(args[1]);
        if (response is null)
            throw new InvalidOperationException(
                "Lambda entry returned null");
        if (new UTF8Encoding(false, true).GetByteCount(response) >
            WorkerProtocol.MaxPayloadBytes)
            throw new InvalidOperationException(
                "Lambda response exceeds 1 MiB");
        Console.Out.Write(response);
        return 0;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Oreslang Lambda invocation failed");
        return 70;
    }
}

Console.Error.WriteLine(
    "usage: oreslang-lambda-app --worker | " +
    "oreslang-lambda-app --invoke <utf8-event>");
return 2;
