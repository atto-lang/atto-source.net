using Ores.Net.Compiler;
using System.Text;

// Reject invalid shapes before indexing args[3]. Previously "compile file.ores"
// passed validation then crashed with IndexOutOfRangeException instead of usage.
bool validCheck = args.Length == 2 && args[0] == "check";
bool validCompile = args.Length == 4 && args[0] == "compile" && args[2] == "--out";
bool validLambdaCheck = args.Length == 2 && args[0] == "check-lambda";
bool validLambdaCompile =
    args.Length == 4 && args[0] == "compile-lambda" && args[2] == "--out";
if (!validCheck && !validCompile && !validLambdaCheck && !validLambdaCompile)
{
    Console.Error.WriteLine(
        "usage: oresnet check input.ores | " +
        "oresnet compile input.ores --out path/OresGenerated.dll | " +
        "oresnet check-lambda input.ores | " +
        "oresnet compile-lambda input.ores --out path/OresGenerated.dll");
    return 2;
}

try
{
    var source = ReadBoundedSource(args[1]);

    if (validLambdaCheck || validLambdaCompile)
    {
        var lambda = OresLambdaTextParser.Parse(source);
        if (validLambdaCompile)
        {
            RequireGeneratedAssemblyName(args[3]);
            LambdaTextCilEmitter.EmitLibrary(lambda, args[3]);
            Console.WriteLine(
                $"Emitted Lambda CIL: {Path.GetFullPath(args[3])}");
        }
        else
        {
            Console.WriteLine("Oreslang String Lambda subset syntax: OK");
        }
        return 0;
    }

    var expression = OresParser.Parse(source);
    // Both scalar check and compile require successfully verified typed IR.
    // Production CIL must not bypass SSA/type validation.
    var verified = OresIrLowering.Lower(expression);
    if (validCompile)
    {
        RequireGeneratedAssemblyName(args[3]);
        CilEmitter.EmitLibrary(verified, args[3]);
        Console.WriteLine($"Emitted CIL: {Path.GetFullPath(args[3])}");
    }
    else Console.WriteLine("Oreslang subset syntax and typed OresIR: OK");
    return 0;
}
catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"oresnet: {ex.Message}");
    return 1;
}


static void RequireGeneratedAssemblyName(string path)
{
    if (!string.Equals(
            Path.GetFileName(path),
            "OresGenerated.dll",
            StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException(
            "Generated assembly must be named OresGenerated.dll");
}


// Bound disk reads before allocation or parsing. File.ReadAllText first loads
// the entire input and would allow a huge file to exhaust compiler memory.
static string ReadBoundedSource(string path)
{
    const int maxBytes = 1024 * 1024;
    var buffer = new byte[maxBytes + 1];
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    int read = 0;
    while (read < buffer.Length)
    {
        var n = stream.Read(buffer, read, buffer.Length - read);
        if (n == 0) break;
        read += n;
    }
    if (read > maxBytes)
        throw new FormatException("Source exceeds 1 MiB bootstrap limit");
    var start = read >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf ? 3 : 0;
    return new UTF8Encoding(false, true).GetString(buffer, start, read - start);
}
