// Real .NET structured-module-compiler import admission, NOT NativeAOT guest.
// Until a verified compiler-owned std graph exists, compiler sources must
// fail closed instead of loading attacker-owned lookalikes from project roots.
using Ores.Net.Compiler;

internal static class Program
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "atto-net-std-import-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string stdDir = Path.Combine(root, "std");
            Directory.CreateDirectory(stdDir);
            File.WriteAllText(Path.Combine(stdDir, "path.atto"),
                "define module path as pub fnc value(int n):int{return 999;} end");
            File.WriteAllText(Path.Combine(stdDir, "sync.atto"),
                "define module sync as pub fnc value(int n):int{return 999;} end");
            File.WriteAllText(Path.Combine(stdDir, "telemetry.atto"),
                "define module Telemetry as pub fnc value(int n):int{return 999;} end");
            string main = Path.Combine(root, "main.atto");
            string[] reserved = [
                "std/path", "std/path.atto", "std/sync",
                "std/telemetry", "std/unknown", "atto.std/path",
                "atto.std/sync.atto", "ores.std/telemetry",
                "ores.std/path.atto"
            ];
            foreach (string specifier in reserved)
            {
                File.WriteAllText(main,
                    "import module path from \"" + specifier +
                    "\"; pub fnc main():int{return 42;}");
                try
                {
                    _ = OresSourceModuleCompiler.Compile(main, [root]);
                }
                catch (FormatException ex) when (
                    ex.Message.Contains(
                        "unsupported compiler-owned AttoLang library namespace",
                        StringComparison.Ordinal))
                {
                    continue;
                }
                throw new InvalidOperationException(
                    "Unlinked compiler-owned import was accepted or misdiagnosed: " + specifier);
            }

            // A local module is a different, explicitly relative namespace.
            // Preserve functioning native CIL graph compilation while
            // denying "std/path" project-owned shadow modules.
            File.WriteAllText(Path.Combine(root, "local.atto"),
                "define module local as pub fnc value(int n):int{return n;} end");
            File.WriteAllText(main,
                "import module local from \"./local\";" +
                " pub fnc main():int{return local.value(42);}");
            var localGraph = OresSourceModuleCompiler.Compile(main, [root]);
            if (localGraph.Sources.Count != 2 ||
                string.IsNullOrWhiteSpace(localGraph.GraphSha256))
            {
                throw new InvalidOperationException(
                    "Ordinary native structured CIL module graph did not compile");
            }

            Console.WriteLine("NET_STD_COMPILER_IMPORT_DENIAL=EXECUTED" +
                "|reserved=9|local_module_graph=COMPILED" +
                "|std_guest=NOT_EXECUTED|native_aot=NOT_EXECUTED");
            return 0;
        }
        finally
        {
            Directory.Delete(root, recursive:true);
        }
    }
}
