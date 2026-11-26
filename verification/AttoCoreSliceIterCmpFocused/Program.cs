using System.Security.Cryptography;
using Mono.Cecil;
using Ores.Net.Compiler;

internal static class AttoCoreSliceIterCmpFocused
{
    private static int assertions;

    private static void Check(bool condition, string context)
    {
        if (!condition) throw new InvalidOperationException("CORE_SIC_CIL: " + context);
        assertions++;
    }

    private static void Reject(Func<SourceModuleCompilation> badSource, string context)
    {
        try
        {
            _ = badSource();
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            assertions++;
            return;
        }
        throw new InvalidOperationException("CORE_SIC_CIL: import was not rejected: " + context);
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "atto-core-sic-focused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sourcePath = Path.Combine("core", "tests", "slice_iter_cmp_smoke.atto");
            string originalSource = File.ReadAllText(sourcePath);
            string entryPath = Path.Combine(root, "main.atto");
            File.WriteAllText(entryPath, originalSource);
            SourceModuleCompilation compiled =
                OresSourceModuleCompiler.Compile(entryPath, new[] { root });
            Check(compiled.Sources.Count == 4,
                "exactly entry and three compiler-owned Atto core sources");
            foreach (string stem in new[] { "slice", "iter", "cmp" })
            {
                string identity = "@atto.core/" + stem + ".atto";
                var matches = compiled.Sources.Where(x => x.Path == identity).ToArray();
                Check(matches.Length == 1, identity + " canonical unique identity");
                byte[] bytes = File.ReadAllBytes(Path.Combine("core", stem + ".atto"));
                byte[] embeddedBytes = File.ReadAllBytes(
                    Path.Combine("library", "atto.core", stem + ".atto"));
                Check(bytes.AsSpan().SequenceEqual(embeddedBytes),
                    stem + " source and embedded resource provenance");
                string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
                Check(matches[0].Sha256 == digest, identity + " pinned source SHA-256");
            }

            string dll = Path.Combine(root, "AttoCoreSliceIterCmpFocused.dll");
            CilEmitter.EmitLibrary(compiled.Module, dll);
            using (var assembly = AssemblyDefinition.ReadAssembly(dll))
            {
                var type = assembly.MainModule.Types.Single(t => t.Name == "OresProgram");
                var main = type.Methods.Single(m => m.Name == "Run");
                Check(CfgCilTestInterpreter.Evaluate(main) == 42,
                    "real compiler-owned Atto slice/iter/cmp CIL guest returns 42");
            }

            foreach (string unsafePath in new[]
                { "atto.core/../slice", "atto.core/slice.ores",
                  "atto.core/missing", "atto.core//iter" })
            {
                string tampered = originalSource.Replace("atto.core/slice", unsafePath,
                    StringComparison.Ordinal);
                Check(!string.Equals(originalSource, tampered, StringComparison.Ordinal),
                    "negative source actually changed");
                File.WriteAllText(entryPath, tampered);
                Reject(() => OresSourceModuleCompiler.Compile(entryPath, new[] { root }),
                    unsafePath + " must fail closed");
            }
            Console.WriteLine("ATTO_NET_CORE_SIC_VERIFIED_CIL_EXECUTION=TRUE");
            Console.WriteLine("ATTO_NET_CORE_SIC_ASSERTIONS=" + assertions);
            return 0;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
