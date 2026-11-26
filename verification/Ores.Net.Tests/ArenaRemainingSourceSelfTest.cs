using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

// Bounded source-level CIL parity only. Canonical alloc/arena still rejects
// until all required Option-based operations are implemented.
internal static class ArenaRemainingSourceSelfTest
{
    [ModuleInitializer]
    internal static void Run()
    {
        byte[] arena = File.ReadAllBytes("stdlib/alloc/arena.atto");
        byte[] fixture = File.ReadAllBytes("contracts/alloc-arena-remaining-v1/guest.atto");
        Require(Blob(arena) == "7c5ba15789cd6dcc1ada54980bb5ba1e7047ef01", "arena source drift");
        Require(Blob(fixture) == "c64314a4f5cb31ac26db5845db3e4ae2d4fb5576", "canonical fixture drift");
        string root = Path.Combine(Path.GetTempPath(), "atto-arena-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "arena.atto"), arena);
            string canonical = Encoding.UTF8.GetString(fixture);
            const string selector = "\"atto.alloc/arena\"";
            Require(canonical.Split(selector, StringSplitOptions.None).Length == 2, "one canonical selector required");
            Execute(canonical.Replace(selector, "\"./arena\"", StringComparison.Ordinal), "projected");
            long[] points = { long.MinValue, long.MinValue + 1, -1024, -2, -1, 0, 1, 2, 16, 64, 1024, long.MaxValue - 1, long.MaxValue };
            int count = 0;
            foreach (long used in points)
            {
                var source = new StringBuilder("import module arena from \"./arena\"; pub fnc main():int {\n");
                foreach (long cap in points)
                {
                    long expected = used < 0 || cap < 0 || used >= cap ? 0 : checked(cap - used);
                    source.Append("if arena.remaining(").Append(Literal(used)).Append(",").Append(Literal(cap))
                        .Append(") != ").Append(Literal(expected)).Append(" then return ").Append(++count).Append("; fi\n");
                }
                source.Append("return 42; }");
                // Failure codes must never equal the success value.
                Execute(source.ToString().Replace("then return 42; fi", "then return 170; fi", StringComparison.Ordinal), "matrix-" + count);
            }
            Require(count == 169, "boundary matrix incomplete");
            Console.WriteLine("ATTO_ARENA_REMAINING_CIL: 169 boundary pairs, projected shared guest; canonical selector NOT_EXECUTED");

            void Execute(string source, string name)
            {
                string entry = Path.Combine(root, name + ".atto");
                File.WriteAllText(entry, source);
                var compiled = OresSourceModuleCompiler.Compile(entry, new[] { root });
                string expectedHash = Convert.ToHexStringLower(SHA256.HashData(arena));
                Require(compiled.Sources.Count(s => s.Sha256 == expectedHash) == 1, "actual source identity missing");
                string dll = Path.Combine(root, name + ".dll");
                CilEmitter.EmitLibrary(compiled.Module, dll);
                var context = new AssemblyLoadContext("arena-" + name, isCollectible: true);
                try
                {
                    using var stream = File.OpenRead(dll);
                    var program = context.LoadFromStream(stream).GetTypes().Single(t => t.Name == "OresProgram");
                    var run = program.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    Require(run is not null && Convert.ToInt64(run.Invoke(null, null), CultureInfo.InvariantCulture) == 42,
                        "actual compiled CLR guest failed: " + name);
                }
                finally { context.Unload(); }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Literal(long value) => value == long.MinValue
        ? "(0 - 9223372036854775807 - 1)" : value.ToString(CultureInfo.InvariantCulture);
    private static string Blob(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(
        Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0").Concat(bytes).ToArray()));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAILED: " + message);
    }
}
