using Ores.Net.Compiler;
using System.Text;

// Reject invalid shapes before indexing args[3]. Previously "compile file.ores"
// passed validation then crashed with IndexOutOfRangeException instead of usage.
bool validCheck = args.Length == 2 && args[0] == "check";
bool validCompile = args.Length == 4 && args[0] == "compile" && args[2] == "--out";
if (!validCheck && !validCompile)
{
    Console.Error.WriteLine("usage: oresnet check input.ores | oresnet compile input.ores --out path/OresGenerated.dll");
    return 2;
}

try
{
    var source = ReadBoundedSource(args[1]);
    var expression = OresParser.Parse(source);
    // Both check and compile require successfully verified typed IR.
    // Production CIL must not bypass SSA/type validation.
    var verified = OresIrLowering.Lower(expression);
    if (args[0] == "compile")
    {
        if (!string.Equals(Path.GetFileName(args[3]), "OresGenerated.dll",
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Generated assembly must be named OresGenerated.dll");
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
