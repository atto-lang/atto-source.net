using Ores.Net.Compiler;

if (args.Length is not 2 and not 4 || args[0] is not ("compile" or "check") ||
    (args.Length == 4 && (args[0] != "compile" || args[2] != "--out")))
{
    Console.Error.WriteLine("usage: oresnet check input.ores | oresnet compile input.ores --out path/OresGenerated.dll");
    return 2;
}

try
{
    var source = File.ReadAllText(args[1]);
    var expression = OresParser.Parse(source);
    if (args[0] == "compile")
    {
        if (!string.Equals(Path.GetFileName(args[3]), "OresGenerated.dll",
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Generated assembly must be named OresGenerated.dll");
        CilEmitter.EmitLibrary(expression, args[3]);
        Console.WriteLine($"Emitted CIL: {Path.GetFullPath(args[3])}");
    }
    else Console.WriteLine("Oreslang subset syntax: OK");
    return 0;
}
catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"oresnet: {ex.Message}");
    return 1;
}
