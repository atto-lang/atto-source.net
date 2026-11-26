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
    // A rejection is not guest execution. Missing mandatory cases and
    // unexpected errors in otherwise unsupported guests must fail the suite.
    private static readonly HashSet<string> RequiredGuests = new(StringComparer.Ordinal)
    {
        "aliases.atto", "core_cmp.atto", "net_core_int_slice.atto"
    };
    private static readonly HashSet<string> RequiredRejections = new(StringComparer.Ordinal)
    {
        "neg_dotted_selector.atto", "neg_nested.atto", "neg_ores_selector.atto",
        "neg_private.atto", "neg_traversal.atto", "neg_unknown_core.atto", "neg_unknown_std.atto"
    };
    private static readonly Dictionary<string, string> KnownUnsupported = new(StringComparer.Ordinal)
    {
        ["alloc_arena.atto"] = "atto.alloc/arena",
        ["core_mem.atto"] = "atto.core/option",
        ["core_num.atto"] = "atto.core/option",
        ["core_option_result.atto"] = "atto.core/option",
        ["std_io.atto"] = "atto.std/io",
        ["std_path_posix.atto"] = "atto.std/path",
        ["std_path_windows.atto"] = "atto.std/path"
    };

    private static bool ExplicitlyUnsupported(string name, string error)
    {
        if (!KnownUnsupported.TryGetValue(name, out string? selector)) return false;
        string layer = selector[..selector.IndexOf('/')];
        string expected = $"compiler-owned {layer} module '{selector}' is not implemented by the .NET backend yet";
        if (layer == "atto.std") expected += " (unsupported compiler-owned AttoLang library namespace)";
        return string.Equals(error, expected, StringComparison.Ordinal);
    }

    private static void CheckEvidencePolicy()
    {
        const string known = "compiler-owned atto.std module 'atto.std/io' is not implemented by the .NET backend yet (unsupported compiler-owned AttoLang library namespace)";
        if (!ExplicitlyUnsupported("std_io.atto", known) ||
            ExplicitlyUnsupported("std_io.atto", "parser regression") ||
            ExplicitlyUnsupported("aliases.atto", known) ||
            ExplicitlyUnsupported("std_path_posix.atto", known) ||
            ExplicitlyUnsupported("std_io.atto", known + " unexpected failure"))
            throw new Exception("FAILED: unsupported library evidence policy is not fail closed");
    }

    [ModuleInitializer]
    internal static void Run()
    {
        CheckEvidencePolicy();
        string root = FindRepositoryRoot();
        string corpus = Path.Combine(root, "tests", "atto_libraries");
        string scratch = Path.Combine(Path.GetTempPath(), "atto-libs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        int guestPassed = 0, rejected = 0, unsupported = 0;
        var executedGuests = new HashSet<string>(StringComparer.Ordinal);
        var executedRejections = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<string>();
        try
        {
            var cases = Directory.GetFiles(corpus, "*.atto").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (cases.Length == 0) throw new Exception("FAILED: shared library corpus is missing");
            foreach (string path in cases)
            {
                string name = Path.GetFileName(path);
                var meta = Headers(path);
                if (RequiredGuests.Contains(name) && meta.Reject || RequiredRejections.Contains(name) && !meta.Reject)
                { failures.Add($"{name}: mandatory case changed outcome category"); continue; }
                SourceModuleCompilation? compiled = null;
                string? error = null;
                try { compiled = OresSourceModuleCompiler.Compile(path, new[] { corpus }); }
                catch (FormatException failure) { error = failure.Message; }

                if (meta.Reject)
                {
                    string? missing = meta.Errors.FirstOrDefault(e => error is null || !error.Contains(e, StringComparison.Ordinal));
                    if (error is null) failures.Add($"{name}: expected rejection, compiled");
                    else if (missing is not null) failures.Add($"{name}: diagnostic lacks '{missing}': {error}");
                    else { rejected++; executedRejections.Add(name); }
                    continue;
                }
                if (error is not null)
                {
                    if (ExplicitlyUnsupported(name, error))
                    {
                        unsupported++;
                        continue;
                    }
                    failures.Add($"{name}: compile failed: {error}");
                    continue;
                }
                // This harness currently verifies only scalar CIL returns.
                // Do not label stdout/target-OS cases as passing without exercising them.
                if (meta.Stdout is not null || meta.Flags.Count > 0)
                { failures.Add($"{name}: compiled but target flags/stdout execution not implemented by harness"); continue; }
                string assembly = Path.Combine(scratch, Path.GetFileNameWithoutExtension(name), "OresGenerated.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
                CilEmitter.EmitLibrary(compiled!.Module, assembly);
                long result = Execute(assembly);
                if (result != meta.Exit) failures.Add($"{name}: main returned {result}, expected {meta.Exit}");
                else { guestPassed++; executedGuests.Add(name); }
            }
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
        foreach (string name in RequiredGuests.Except(executedGuests))
            failures.Add($"{name}: required guest not executed successfully");
        foreach (string name in RequiredRejections.Except(executedRejections))
            failures.Add($"{name}: required rejection not verified");
        Console.WriteLine($"ATTO_LIBRARIES_V1 net guest_passed={guestPassed} rejected={rejected} unsupported={unsupported} failed={failures.Count}");
        if (failures.Count > 0) throw new Exception("FAILED: " + string.Join("; ", failures));
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

    private sealed record Meta(List<string> Flags, long Exit, string? Stdout, bool Reject, List<string> Errors);

    private static Meta Headers(string path)
    {
        var flags = new List<string>(); var errors = new List<string>();
        long exit = 0; string? stdout = null; bool reject = false;
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
