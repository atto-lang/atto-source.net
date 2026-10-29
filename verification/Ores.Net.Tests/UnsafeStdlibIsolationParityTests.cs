using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

// Host-side G0 conformance only, not NativeAOT or native FFI execution proof.
internal static class UnsafeStdlibIsolationParityTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        var denied = new[]
        {
            "ores.std/unsafe", "ores.std/unsafe/ffi",
            "std/unsafe", "std/unsafe/native",
            "ores.std.unsafe.memory", "std.unsafe.ffi",
            "ores.std:unsafe", "std:unsafe.ffi",
            "std\\unsafe\\memory"
        };
        foreach (var path in denied)
        {
            Require(UnsafeStdlibImportAdmission.IsReserved(path),
                "unsafe path was not reserved: " + path);
        }
        foreach (var path in new[] {
            "std/unsafe_tools", "std/unsafe-math", "pkg:unsafe",
            "./unsafe.ores", "ores.std/safe", "std:unsafe_tools"
        })
        {
            Require(!UnsafeStdlibImportAdmission.IsReserved(path),
                "unrelated source falsely marked unsafe: " + path);
        }

        var sources = new[]
        {
            "unsafe fnc main():int{return 42;}",
            "fnc main():int{unsafe{return 42;}}",
            "fnc unsafe():int{return 42;}",
            "fnc main(unsafe:int):int{return 42;}",
            "fnc main():int{return std.unsafe.memory.read();}"
        };
        foreach (string source in sources)
        {
            foreach (Action<string> parse in new Action<string>[] {
                s => { _ = OresParser.Parse(s); },
                s => { _ = OresStructuredParser.Parse(s); }
            })
            {
                try { parse(source); }
                catch (FormatException ex) when (
                    ex.Message.Contains("unsafe boundary requires checked effects",
                                        StringComparison.Ordinal)) { continue; }
                throw new Exception("Unsafe source evaded .NET lexical admission: " + source);
            }
        }

        string root = Path.Combine(Path.GetTempPath(),
            "ores-unsafe-isolate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string entry = Path.Combine(root, "main.ores");
            foreach (string path in denied)
            {
                File.WriteAllText(entry,
                    "import module ffi from \"" + path.Replace('\\', '/') +
                    "\"; pub fnc main():int{return 42;}");
                try
                {
                    _ = OresSourceModuleCompiler.Compile(entry, new[] { root });
                }
                catch (FormatException ex) when (
                    ex.Message.Contains("unsafe standard-library import unavailable",
                                        StringComparison.Ordinal)) { continue; }
                throw new Exception("Unsafe import reached typed module linker: " + path);
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS unsafe stdlib G0 .NET import/lexical isolation (host tests only)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
