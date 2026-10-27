using Ores.Net.Compiler;
using System.Text;
using System.Globalization;
using Ores.Net.Runtime;

// Reject invalid shapes before indexing args[3]. Previously "compile file.ores"
// passed validation then crashed with IndexOutOfRangeException instead of usage.
bool validCheck = args.Length == 2 && args[0] == "check";
bool validAsyncRun = args.Length == 2 && args[0] == "run-async";
bool validCompile = args.Length == 4 && args[0] == "compile" && args[2] == "--out";
bool validStaticFacts = args.Length == 2 &&
    (args[0] == "static-facts" || args[0] == "static-facts-structured");
if (!validCheck && !validCompile && !validStaticFacts && !validAsyncRun)
{
    Console.Error.WriteLine("usage: oresnet check input.ores | oresnet run-async input.ores | oresnet compile input.ores --out path/OresGenerated.dll | oresnet static-facts input.ores | oresnet static-facts-structured input.ores");
    return 2;
}

try
{
    if (validStaticFacts &&
        !string.Equals(Path.GetExtension(args[1]), ".ores", StringComparison.OrdinalIgnoreCase))
        throw new FormatException("Static-facts export requires a .ores source file");
    var source = ReadBoundedSource(args[1], rejectBom: validStaticFacts);
    if (validStaticFacts)
    {
        // The production command uses the exact verified scalar IR gate.
        // The structured command is a separately labelled parser-only
        // experiment and is NOT a Native AOT or executable CFG proof.
        Console.WriteLine(args[0] == "static-facts"
            ? StaticFactsV0Exporter.FromCompiledScalarSource(source)
            : StaticFactsV0Exporter.FromStructuredSource(source));
        return 0;
    }
    if (validAsyncRun)
    {
        // Explicit interpreter-only NativeAOT source slice. No generated CIL or
        // arbitrary guest callbacks; never route this through scalar Run().
        var guest = OresAsyncSourceSlice.Parse(source);
        await using var scope = new OresTaskScope(maxProducers: 1, maxObservers: 2);
        var producer = scope.Start(_ => ValueTask.FromResult(guest.LiteralValue));
        var future = scope.FromTask(producer);
        var observed = await future.ObserveAsync();
        var retired = await producer.Retirement;
        if (observed != guest.LiteralValue || retired.ProducerId != producer.ProducerId ||
            retired.TerminalState != "succeeded" || scope.ProducerInvocations != 1 ||
            scope.ActiveProducers != 0 || scope.ActiveObservers != 0)
            throw new FormatException("Async source producer/observer retirement invariant failed");
        Console.WriteLine(observed.ToString(CultureInfo.InvariantCulture));
        return 0;
    }
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
static string ReadBoundedSource(string path, bool rejectBom = false)
{
    const int maxBytes = 1024 * 1024;
    var buffer = new byte[maxBytes + 1];
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    // The advisory digest must describe the bytes actually admitted to
    // parsing, not a concurrent writer's mix of two file generations.
    var beforeWrite = rejectBom ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    int read = 0;
    while (read < buffer.Length)
    {
        var n = stream.Read(buffer, read, buffer.Length - read);
        if (n == 0) break;
        read += n;
    }
    if (read > maxBytes)
        throw new FormatException("Source exceeds 1 MiB bootstrap limit");
    if (rejectBom)
    {
        if (!stream.CanSeek || stream.Length != read)
            throw new FormatException("Static-facts source length changed during snapshot");
        var second = new byte[read];
        stream.Seek(0, SeekOrigin.Begin);
        var again = 0;
        while (again < second.Length)
        {
            var n = stream.Read(second, again, second.Length - again);
            if (n == 0) break;
            again += n;
        }
        if (again != read || !buffer.AsSpan(0, read).SequenceEqual(second) ||
            stream.Length != read || File.GetLastWriteTimeUtc(path) != beforeWrite)
            throw new FormatException("Static-facts source changed during snapshot");
    }
    var start = read >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf ? 3 : 0;
    // Never sign source *after* silently discarding bytes from an input.
    if (rejectBom && start != 0)
        throw new FormatException("Static-facts export rejects BOM to preserve exact byte identity");
    return new UTF8Encoding(false, true).GetString(buffer, start, read - start);
}
