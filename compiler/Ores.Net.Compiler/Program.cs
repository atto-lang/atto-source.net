using Ores.Net.Compiler;
using System.Text;

// Reject invalid shapes before indexing args[3]. Previously "compile file.ores"
// passed validation then crashed with IndexOutOfRangeException instead of usage.
bool validCheck = args.Length == 2 && args[0] == "check";
bool validCompile = args.Length == 4 && args[0] == "compile" && args[2] == "--out";
// Opt-in verification lane only: never silently broaden shipping scalar check/compile.
bool validStructuredCheck = args.Length == 2 && args[0] == "check-structured";
bool validStructuredCompile = args.Length == 4 &&
    args[0] == "compile-structured" && args[2] == "--out";
bool validEmitSource = args.Length == 4 && args[2] == "--out" &&
    (args[0] == "emit-csharp" || args[0] == "emit-fsharp");
bool validStaticFacts = args.Length == 2 &&
    (args[0] == "static-facts" || args[0] == "static-facts-structured");
// Explicit source-only preview. Never accept actor pipes in normal check/compile.
bool validActorPipePlan = args.Length == 2 && args[0] == "plan-actor-pipe";
bool moduleCommand = args.Length >= 1 && args[0] is "check-modules" or "compile-modules";
if (!validCheck && !validCompile && !validStructuredCheck && !validStructuredCompile && !validStaticFacts && !validActorPipePlan && !validEmitSource && !moduleCommand)
{
    Console.Error.WriteLine("usage: oresnet check input.atto|input.ores | oresnet compile input.atto|input.ores --out path/OresGenerated.dll | oresnet check-structured input.atto|input.ores | oresnet compile-structured input.atto|input.ores --out path/OresGenerated.dll | oresnet emit-csharp input.atto|input.ores --out path/OresProgram.cs | oresnet emit-fsharp input.atto|input.ores --out path/OresProgram.fs | oresnet static-facts input.atto|input.ores | oresnet static-facts-structured input.atto|input.ores");
    return 2;
}

try
{
    if (moduleCommand)
    {
        if (args.Length < 4 || args.Length % 2 != 0)
            throw new ArgumentException("usage: oresnet check-modules INPUT --root ROOT [--dependency-root ROOT] | compile-modules INPUT --root ROOT [--dependency-root ROOT] --out OresGenerated.dll");
        string? root = null, output = null;
        var dependencies = new List<string>();
        for (int i = 2; i < args.Length; i += 2)
        {
            if (args[i] == "--root" && root is null) root = args[i + 1];
            else if (args[i] == "--dependency-root") dependencies.Add(args[i + 1]);
            else if (args[i] == "--out" && output is null) output = args[i + 1];
            else throw new ArgumentException("Unknown or duplicate module option");
        }
        if (root is null || (args[0] == "compile-modules") != (output is not null))
            throw new ArgumentException("Module command requires one --root and compile-modules requires --out");
        if (output is not null && Path.GetFileName(output) != "OresGenerated.dll")
            throw new ArgumentException("Generated assembly must be named OresGenerated.dll");
        var compilation = OresSourceModuleCompiler.Compile(args[1], new[] { root }.Concat(dependencies).ToArray());
        if (output is not null) CilEmitter.EmitLibrary(compilation.Module, output);
        using var witness = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(witness))
        {
            json.WriteStartObject();
            json.WriteString("scope", "verified-int-bool-modules-not-full-Spin");
            json.WriteString("graph_sha256", compilation.GraphSha256);
            json.WriteStartArray("sources");
            foreach (var unit in compilation.Sources)
            {
                json.WriteStartObject(); json.WriteString("path", unit.Path);
                json.WriteString("sha256", unit.Sha256); json.WriteNumber("utf8_bytes", unit.Utf8Bytes);
                json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        Console.WriteLine(Encoding.UTF8.GetString(witness.ToArray()));
        return 0;
    }
    if (validStaticFacts &&
        !string.Equals(Path.GetExtension(args[1]), ".atto", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Path.GetExtension(args[1]), ".ores", StringComparison.OrdinalIgnoreCase))
        throw new FormatException("Static-facts export requires a .atto or .ores source file");
    var source = ReadBoundedSource(args[1], rejectBom: validStaticFacts);
    if (validActorPipePlan)
    {
        string extension = Path.GetExtension(args[1]);
        if (!string.Equals(extension, ".atto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".ores", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Actor pipe plan requires an .atto or legacy .ores source file");
        var plan = OresStructuredParser.PlanActorPipeV1(source);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            status = ActorPipeGraphV1.Evidence,
            executable_actor_guest = false,
            native_aot_verified = false,
            stage_count = plan.Stages.Count,
            edge = new { source = plan.Edge.ProducerBinding,
                destination = plan.Edge.ConsumerBinding },
            message = plan.InputMessage
        }));
        return 0;
    }
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
    if (validStructuredCheck || validStructuredCompile)
    {
        // Experimental typed source -> verified CFG -> deterministic CIL.
        // Never enter the shipping scalar path or accept unsupported effects.
        var verifiedCfg = OresStructuredCfgLowering.LowerModule(source);
        if (validStructuredCompile)
        {
            if (!string.Equals(Path.GetFileName(args[3]), "OresGenerated.dll",
                StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Generated assembly must be named OresGenerated.dll");
            CilEmitter.EmitLibrary(verifiedCfg, args[3]);
            Console.WriteLine($"Emitted experimental structured CIL: {Path.GetFullPath(args[3])}");
        }
        else Console.WriteLine("Experimental structured source and verified CFG: OK");
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
    else if (validEmitSource)
    {
        var target = args[0] == "emit-csharp"
            ? ManagedSourceTarget.CSharp : ManagedSourceTarget.FSharp;
        var extension = target == ManagedSourceTarget.CSharp ? ".cs" : ".fs";
        if (!string.Equals(Path.GetExtension(args[3]), extension,
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Managed source output requires {extension} extension");
        var generatedSource = ManagedSourceEmitter.Emit(verified, target);
        WriteAtomicSource(args[3], generatedSource);
        Console.WriteLine($"Emitted {target} from verified OresIR: {Path.GetFullPath(args[3])}");
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


// Publish a complete UTF-8 (no BOM) source file only after admission succeeds.
// Temporary files are not discoverable as successful compiler outputs.
static void WriteAtomicSource(string path, string content)
{
    if (Encoding.UTF8.GetByteCount(content) > 2 * 1024 * 1024)
        throw new FormatException("Generated source exceeds 2 MiB limit");
    var fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        File.WriteAllText(tempPath, content, new UTF8Encoding(false));
        File.Move(tempPath, fullPath, overwrite: true);
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
}
