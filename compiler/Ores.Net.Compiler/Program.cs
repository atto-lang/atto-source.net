using Ores.Net.Compiler;
using System.Text;
using System.Globalization;
using Ores.Net.Runtime;

// The bootstrap CLR capability is a deliberate compiler invocation grant,
// not an authority inferred from host type names or environment variables.
// It is valid only for scalar check/compile, never module/structured/facts paths.
bool mathI64Granted = args.Length >= 4 &&
    args[^2] == "--capability" && args[^1] == "interop.dotnet.math-i64";
if (mathI64Granted) args = args[..^2];
var dotnetPolicy = mathI64Granted
    ? DotnetHostImportPolicy.MathI64V0
    : DotnetHostImportPolicy.DenyAll;

// Reject invalid shapes before indexing args[3]. Previously "compile file.ores"
// passed validation then crashed with IndexOutOfRangeException instead of usage.
bool validCheck = args.Length == 2 && args[0] == "check";
bool validAsyncRun = args.Length == 2 && args[0] == "run-async";
bool validDeclaredReceiveSyntax = args.Length == 2 &&
    args[0] == "check-declared-receive-syntax";
// FULL_GUEST_AOT_TODO (platform-conformance #321): These commands emit
// verified scalar/structured CIL or managed-source artifacts, not yet a
// closed-world native guest executable for arbitrary AttoLang programs.
// Keep them intact; a strict build must close module/method/generic/actor,
// exception/cleanup and suspension edges before NativeAOT publish, and forbid
// interpreter/Reflection.Emit/runtime codegen fallback in the sealed profile.
// ZERO_TRACING_GC_TODO: CIL and .NET NativeAOT still use CLR-managed object
// lifetimes by default. NativeAOT != no GC. Lower guest class/actor data into
// actor-owned unmanaged regions with checked 32-bit offsets and generation
// authority; prove deterministic disposal at return, cancellation, restart,
// FFI pins and hot swap, plus transitive native/PInvoke allocation closure.
bool validCompile = args.Length == 4 && args[0] == "compile" && args[2] == "--out";
bool validTaskCheck = args.Length == 2 && args[0] == "check-task-i64";
bool validTaskCompile = args.Length == 4 && args[0] == "compile-task-i64" && args[2] == "--out";
// Opt-in verification lane only: never silently broaden shipping scalar check/compile.
// Separate opt-in nominal enum guest/compiler slice; shipping scalar and
// structured entrypoints retain their fail-closed ADT admission boundaries.
bool validEnumCheck = args.Length == 2 && args[0] == "check-enum-v1";
bool validEnumCompile = args.Length == 4 &&
    args[0] == "compile-enum-v1" && args[2] == "--out";
bool validStructuredCheck = args.Length == 2 && args[0] == "check-structured";
bool validStructuredCompile = args.Length == 4 &&
    args[0] == "compile-structured" && args[2] == "--out";
bool validEmitActorSource = args.Length == 4 && args[0] == "emit-actor-csharp" && args[2] == "--out";
// This separate mode only binds the verified external receive slice of the
// Counter v2 package; it never claims compiled async main or general actors.
bool validExternalReceive = args.Length == 4 && args[0] == "emit-external-counter-csharp" && args[2] == "--out";
bool validEmitSource = args.Length == 4 && args[2] == "--out" &&
    (args[0] == "emit-csharp" || args[0] == "emit-fsharp");
bool validStaticFacts = args.Length == 2 &&
    (args[0] == "static-facts" || args[0] == "static-facts-structured");
// Explicit source-only preview. Never accept actor pipes in normal check/compile.
bool validActorPipePlan = args.Length == 2 && args[0] == "plan-actor-pipe";
bool moduleCommand = args.Length >= 1 && args[0] is "check-modules" or "compile-modules";
if ((mathI64Granted && !validCheck && !validCompile) ||
    (!validCheck && !validAsyncRun && !validEnumCheck && !validEnumCompile && !validDeclaredReceiveSyntax && !validCompile && !validStructuredCheck && !validStructuredCompile &&
     !validStaticFacts && !validTaskCheck && !validTaskCompile && !validActorPipePlan && !validEmitActorSource && !validExternalReceive && !validEmitSource && !moduleCommand))
{
    Console.Error.WriteLine("usage: oresnet check-enum-v1 FILE.atto | oresnet compile-enum-v1 FILE.atto --out path/OresGenerated.dll | oresnet check input.atto|input.ores | oresnet run-async input.ores | oresnet compile input.atto|input.ores --out path/OresGenerated.dll | oresnet check-task-i64 input.ores | oresnet compile-task-i64 input.ores --out path/OresGenerated.dll | oresnet check-structured input.atto|input.ores | oresnet compile-structured input.atto|input.ores --out path/OresGenerated.dll | oresnet emit-csharp input.atto|input.ores --out path/OresProgram.cs | oresnet emit-fsharp input.atto|input.ores --out path/OresProgram.fs | oresnet static-facts input.atto|input.ores | oresnet static-facts-structured input.atto|input.ores | oresnet plan-actor-pipe input.atto (typed plan only, not executable actors) | oresnet emit-actor-csharp Counter.atto --out path/Actor_Counter.g.cs | oresnet emit-external-counter-csharp ROOT --out path/DeclaredActor_Counter.g.cs | oresnet check-declared-receive-syntax receive.atto (syntax only, NOT guest execution)");
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
    if (validExternalReceive)
    {
        if (!string.Equals(Path.GetExtension(args[3]), ".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("External receive source output requires .cs");
        string root = Path.GetFullPath(args[1]);
        string src = Path.Combine(root, "src");
        string counter = Path.Combine(src, "counter");
        // Never follow package/actor source symlinks while certifying a
        // single locally materialized, SHA-verified canonical source corpus.
        foreach (string directory in new[] { root, src, counter })
        {
            if (!Directory.Exists(directory) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new FormatException("External receive package directory must be real");
        }
        string manifestPath = Path.Combine(root, ".atto.pkg.toml");
        if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("External receive manifest cannot be a symlink");
        const string expectedManifest = "schema_version = \"1\"\n[project]\nname = \"counter-v2\"\nversion = \"0.2.0\"\nroot = \".\"\n[source]\nroots = [\"src\"]\n";
        if (ReadBoundedSource(manifestPath, rejectBom: true) != expectedManifest)
            throw new FormatException("External receive v2 requires the canonical package manifest");
        var sourceUnits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in new[] { "actor.atto", "contract.atto",
                     "method.atto", "receive.atto", "state.atto" })
        {
            string path = Path.Combine(counter, file);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new FormatException("External receive source cannot be a symlink");
            sourceUnits.Add("src/counter/" + file, ReadBoundedSource(path, rejectBom: true));
        }
        var verifiedReceive = OresParser.InspectExternalCounterV2(sourceUnits);
        if (verifiedReceive.Namespace != "counter")
            throw new FormatException("External receive namespace does not match source root");
        WriteAtomicSource(args[3], DeclaredActorCounterV2Emitter.EmitCSharp(verifiedReceive));
        Console.WriteLine("VERIFIED_EXTERNAL_RECEIVE_V2 actor_handler_only=true async_main_not_compiled=true");
        return 0;
    }
    if (validStaticFacts &&
        !string.Equals(Path.GetExtension(args[1]), ".atto", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Path.GetExtension(args[1]), ".ores", StringComparison.OrdinalIgnoreCase))
        throw new FormatException("Static-facts export requires a .atto or .ores source file");
    var source = ReadBoundedSource(args[1], rejectBom: validStaticFacts);
    if (validTaskCheck || validTaskCompile)
    {
        var taskProgram = TaskI64Source.Check(source);
        if (validTaskCompile)
        {
            if (!string.Equals(Path.GetFileName(args[3]), "OresGenerated.dll", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Generated assembly must be named OresGenerated.dll");
            CilEmitter.EmitTaskI64Library(taskProgram, args[3]);
            Console.WriteLine("Emitted closed Task-i64 producer CIL; requires TaskI64 native host");
        }
        else Console.WriteLine("Closed root Task-i64 profile and verified producer IR: OK");
        return 0;
    }
    if (validDeclaredReceiveSyntax)
    {
        if (!string.Equals(Path.GetExtension(args[1]), ".atto",
                StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Declared receive syntax check requires a .atto source file");
        var admitted = OresParser.InspectDeclaredActorReceive(source);
        Console.WriteLine(
            $"NET_DECLARED_COUNTER_RECEIVE_SYNTAX_PASS|namespace={admitted.Namespace}" +
            $"|actor={admitted.ActorName}|field={admitted.StateField}" +
            $"|stop={admitted.StopOnMailValue}|guest_not_executed");
        return 0;
    }
    if (validActorPipePlan)
    {
        string extension = Path.GetExtension(args[1]);
        if (!string.Equals(extension, ".atto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".ores", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Actor pipe plan requires an .atto or legacy .ores source file");
        var plan = OresStructuredParser.PlanActorPipeV1(source);
        // Full-AOT: no reflection-based JsonSerializer (IL2026/IL3050). Write the same
        // compact object, in the same field order, with Utf8JsonWriter.
        var planJson = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(planJson))
        {
            json.WriteStartObject();
            json.WriteString("status", ActorPipeGraphV1.Evidence);
            json.WriteBoolean("executable_actor_guest", false);
            json.WriteBoolean("native_aot_verified", false);
            json.WriteNumber("stage_count", plan.Stages.Count);
            json.WriteStartObject("edge");
            json.WriteString("source", plan.Edge.ProducerBinding);
            json.WriteString("destination", plan.Edge.ConsumerBinding);
            json.WriteEndObject();
            json.WriteNumber("message", plan.InputMessage);
            json.WriteEndObject();
        }
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(planJson.ToArray()));
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
    if (validEnumCheck || validEnumCompile)
    {
        if (!string.Equals(Path.GetExtension(args[1]), ".atto",
            StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Nominal enum v1 requires canonical .atto source");
        var module = OresNominalEnumV1.LowerModule(source);
        if (validEnumCompile)
        {
            if (!string.Equals(Path.GetFileName(args[3]), "OresGenerated.dll",
                StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Generated assembly must be named OresGenerated.dll");
            CilEmitter.EmitLibrary(module, args[3]);
            Console.WriteLine("Emitted verified nominal enum v1 CIL: " +
                Path.GetFullPath(args[3]));
        }
        else Console.WriteLine("Verified nominal enum v1 source and CFG: OK");
        return 0;
    }
    if (validEmitActorSource)
    {
        if (!string.Equals(Path.GetExtension(args[3]), ".cs",
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Actor C# source output requires a .cs file");
        var actorMethod = OresParser.InspectSynchronousActorIncrement(source);
        var generated = ActorIncrementSourceEmitter.EmitCSharp(actorMethod);
        WriteAtomicSource(args[3], generated);
        Console.WriteLine($"Emitted experimental static actor C# from verified increment IR: {Path.GetFullPath(args[3])}");
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
    var expression = OresParser.Parse(source, dotnetPolicy);
    // Both check and compile require successfully verified typed IR.
    // Production CIL must not bypass SSA/type validation.
    var verified = OresIrLowering.Lower(expression, policy: dotnetPolicy);
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
