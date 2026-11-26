using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Ores.Net.Compiler;

/// <summary>
/// Shared core/alloc/std v1 guest corpus (tests/atto_libraries, identical in
/// every atto-source.* compiler). Each guest is linked by the real module
/// compiler, emitted as CIL, loaded and executed. Units this backend does not
/// implement yet must fail closed with an explicit "not implemented" error and
/// are reported as UNSUPPORTED, never as passes.
/// </summary>
internal static class AttoLibrariesV1SelfTest
{
    private static readonly HashSet<string> Required = new(StringComparer.Ordinal)
    {
        "core_num.atto", "core_cmp.atto", "core_option_result.atto", "core_mem.atto",
        "alloc_arena.atto", "std_io.atto", "std_path_posix.atto", "std_path_windows.atto",
        "aliases.atto", "neg_unknown_core.atto", "neg_unknown_std.atto", "neg_traversal.atto",
        "neg_nested.atto", "neg_private.atto", "neg_dotted_selector.atto", "neg_ores_selector.atto",
        "net_core_int_slice.atto"
    };
    private static readonly Dictionary<string, string> KnownUnsupported = new(StringComparer.Ordinal)
    {
        ["core_num.atto"] = "atto.core/option",
        ["core_mem.atto"] = "atto.core/option",
        ["core_option_result.atto"] = "atto.core/option",
        ["alloc_arena.atto"] = "atto.alloc/arena",
        ["std_io.atto"] = "atto.std/io",
        ["std_path_posix.atto"] = "atto.std/path",
        ["std_path_windows.atto"] = "atto.std/path"
    };

    private static bool ExplicitUnsupported(string name, string error)
    {
        if (!KnownUnsupported.TryGetValue(name, out string? unit)) return false;
        string layer = unit.Split('/')[0];
        string expected = $"compiler-owned {layer} module '{unit}' is not implemented by the .NET backend yet";
        if (layer == "atto.std") expected += " (unsupported compiler-owned AttoLang library namespace)";
        return string.Equals(error, expected, StringComparison.Ordinal);
    }

    private static readonly Dictionary<string, string> PinnedLayeredCore = new(StringComparer.Ordinal)
    {
        ["@atto.core/num.atto"] = "42b0adc66e34c44918538e71b3b6de935ee2c48e0b047ac443191db35614e1e3",
        ["@atto.core/cmp.atto"] = "bf5c656df7e539e077f6bd7cfbe4f80d0b4980c539b57e11c79414cb05501d3a",
        ["@atto.core/mem.atto"] = "8450235b9d7194f44910423738dfb395ab15753860e78bb7abee36d51b094fe4"
    };

    private static void VerifyLayeredCoreWitnesses(string name, SourceModuleCompilation compiled)
    {
        // These three required guests exercise all registered layered resources.
        string[] required = name switch
        {
            "aliases.atto" => new[] { "@atto.core/num.atto", "@atto.core/cmp.atto" },
            "core_cmp.atto" => new[] { "@atto.core/cmp.atto" },
            "net_core_int_slice.atto" => new[] { "@atto.core/num.atto", "@atto.core/cmp.atto", "@atto.core/mem.atto" },
            _ => Array.Empty<string>()
        };
        foreach (string identity in required)
        {
            var witnesses = compiled.Sources.Where(w => w.Path == identity).ToArray();
            if (witnesses.Length != 1 || witnesses[0].Sha256 != PinnedLayeredCore[identity])
                throw new Exception($"FAILED: {name}: missing or drifted layered source identity {identity}");
        }
    }

    private static void RequireCorpus(IEnumerable<string> cases)
    {
        var names = cases.Select(p => Path.GetFileName(p)).ToHashSet(StringComparer.Ordinal);
        if (!Required.IsSubsetOf(names))
            throw new Exception("FAILED: missing library cases " + string.Join(", ", Required.Except(names).Order()));
    }

    private static void GuardControls()
    {
        RequireCorpus(Required);
        foreach (var missing in Required)
        {
            bool denied = false;
            try { RequireCorpus(Required.Where(n => n != missing)); }
            catch (Exception error) when (error.Message.StartsWith("FAILED: missing library cases", StringComparison.Ordinal)) { denied = true; }
            if (!denied) throw new Exception("FAILED: missing-case control " + missing);
        }
        string explicitError = "compiler-owned atto.std module 'atto.std/io' is not implemented by the .NET backend yet (unsupported compiler-owned AttoLang library namespace)";
        if (!ExplicitUnsupported("std_io.atto", explicitError) ||
            ExplicitUnsupported("std_io.atto", "arbitrary parser failure") ||
            ExplicitUnsupported("aliases.atto", explicitError) ||
            ExplicitUnsupported("std_path_posix.atto", explicitError) ||
            ExplicitUnsupported("std_io.atto", explicitError + " unrelated failure"))
            throw new Exception("FAILED: unsupported classification controls");
    }

    [ModuleInitializer]
    internal static void Run()
    {
        GuardControls();
        string root = FindRepositoryRoot();
        string corpus = Path.Combine(root, "tests", "atto_libraries");
        string scratch = Path.Combine(Path.GetTempPath(), "atto-libs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        int executed = 0, rejected = 0, unsupported = 0;
        var failures = new List<string>();
        try
        {
            var cases = Directory.GetFiles(corpus, "*.atto").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            RequireCorpus(cases);
            foreach (string path in cases)
            {
                string name = Path.GetFileName(path);
                var meta = Headers(path);
                if (Required.Contains(name) && meta.Reject != name.StartsWith("neg_", StringComparison.Ordinal))
                {
                    failures.Add($"{name}: invalid fixture polarity");
                    continue;
                }
                if (!meta.Reject && meta.Exit is null)
                {
                    failures.Add($"{name}: missing explicit expected exit");
                    continue;
                }
                SourceModuleCompilation? compiled = null;
                string? error = null;
                try { compiled = OresSourceModuleCompiler.Compile(path, new[] { corpus }); }
                catch (FormatException failure) { error = failure.Message; }

                if (meta.Reject)
                {
                    string? missing = meta.Errors.FirstOrDefault(e => error is null || !error.Contains(e, StringComparison.Ordinal));
                    if (error is null) failures.Add($"{name}: expected rejection, compiled");
                    else if (missing is not null) failures.Add($"{name}: diagnostic lacks '{missing}': {error}");
                    else if (string.IsNullOrWhiteSpace(error)) failures.Add($"{name}: rejection lacked diagnostic");
                    else { rejected++; Console.WriteLine($"REJECTED {name}"); }
                    continue;
                }
                if (error is not null)
                {
                    if (ExplicitUnsupported(name, error))
                    {
                        unsupported++;
                        Console.WriteLine($"UNSUPPORTED {name}: {error}");
                        continue;
                    }
                    failures.Add($"{name}: compile failed: {error}");
                    continue;
                }
                if (meta.Flags.Count > 0)
                {
                    failures.Add($"{name}: runner must implement required target flags before claiming execution");
                    continue;
                }
                VerifyLayeredCoreWitnesses(name, compiled!);
                string assembly = Path.Combine(scratch, Path.GetFileNameWithoutExtension(name), "OresGenerated.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
                CilEmitter.EmitLibrary(compiled!.Module, assembly);
                var originalOut = Console.Out;
                using var captured = new StringWriter();
                long result;
                try { Console.SetOut(captured); result = Execute(assembly); }
                finally { Console.SetOut(originalOut); }
                if (result != meta.Exit) failures.Add($"{name}: main returned {result}, expected {meta.Exit}");
                else if (meta.Stdout is not null && captured.ToString() != meta.Stdout)
                    failures.Add($"{name}: stdout mismatch");
                else { executed++; Console.WriteLine($"EXECUTED {name}"); }
            }
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
        Console.WriteLine($"ATTO_LIBRARIES_V1 net guest_passed={executed} executed={executed} rejected={rejected} unsupported={unsupported} failed={failures.Count}");
        if (failures.Count > 0) throw new Exception("FAILED: " + string.Join("; ", failures));
        if (executed < 3 || rejected < 7) throw new Exception($"FAILED: insufficient positive/rejection coverage {executed}/{rejected}");
    }

    private static long Execute(string assemblyPath)
    {
        var context = new AssemblyLoadContext("atto-libs-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            Assembly loaded = context.LoadFromStream(stream);
            Type program = loaded.GetTypes().Single(t => t.Name == "OresProgram");
            MethodInfo run = program.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new Exception("FAILED: generated OresProgram.Run is missing");
            return Convert.ToInt64(run.Invoke(null, null));
        }
        finally { context.Unload(); }
    }

    private sealed record Meta(List<string> Flags, long? Exit, string? Stdout, bool Reject, List<string> Errors);

    private static Meta Headers(string path)
    {
        var flags = new List<string>(); var errors = new List<string>();
        long? exit = null; string? stdout = null; bool reject = false;
        foreach (string line in File.ReadLines(path))
        {
            if (!line.StartsWith("//", StringComparison.Ordinal)) break;
            string body = line[2..].Trim();
            if (body == "expect-reject") reject = true;
            else if (body.StartsWith("flags:", StringComparison.Ordinal))
                flags.AddRange(body[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
            else if (body.StartsWith("expect-exit:", StringComparison.Ordinal)) exit = long.Parse(body[12..].Trim());
            else if (body.StartsWith("expect-stdout-json:", StringComparison.Ordinal))
                stdout = JsonSerializer.Deserialize<string>(body[19..].Trim());
            else if (body.StartsWith("expect-error:", StringComparison.Ordinal)) errors.Add(body[13..].Trim());
            else if (body.StartsWith("expect-error-net:", StringComparison.Ordinal)) errors.Add(body[17..].Trim());
        }
        return new Meta(flags, exit, stdout, reject, errors);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string origin in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(Path.GetFullPath(origin));
            for (int depth = 0; dir is not null && depth < 16; depth++, dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "tests", "atto_libraries")) &&
                    File.Exists(Path.Combine(dir.FullName, "global.json")))
                    return dir.FullName;
        }
        throw new Exception("FAILED: could not locate .NET repository root for library corpus");
    }
}
