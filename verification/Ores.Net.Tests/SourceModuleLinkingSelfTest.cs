using System.Runtime.CompilerServices;
using Mono.Cecil;
using Ores.Net.Compiler;

internal static class SourceModuleLinkingSelfTest
{
    [ModuleInitializer]
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ores-modules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string outside = root + "-outside.ores";
        try
        {
            Write("main.ores", "import module left from \"./left\"; import module right from \"./right\"; pub fnc main():int { return left.value(10) + right.value(11); }");
            Write("left.ores", "import fnc twice from \"./base\"; define module left as pub fnc value(int n) -> int { return twice(n); } end");
            Write("right.ores", "define module right as pub fnc value(int n):int { return n * 2; } fnc hidden():int { return 99; } end");
            Write("base.ores", "pub fnc twice(int n):int { return n * 2; }");
            var compiled = Compile();
            Require(compiled.Sources.Count == 4, "complete reachable graph");
            Require(compiled.Module.Helpers.Count == 4, "distinct scoped methods including private helpers");
            Require(compiled.Module.Helpers.Select(m => m.Name).Distinct().Count() == 4, "canonical method names do not collide");
            string assembly = Path.Combine(root, "OresGenerated.dll");
            CilEmitter.EmitLibrary(compiled.Module, assembly);
            string relocated = Path.Combine(root, "relocated");
            Directory.CreateDirectory(relocated);
            foreach (string file in new[] { "main.ores", "left.ores", "right.ores", "base.ores" })
                File.Copy(Path.Combine(root, file), Path.Combine(relocated, file));
            var movedGraph = OresSourceModuleCompiler.Compile(Path.Combine(relocated, "main.ores"), new[] { relocated });
            Require(movedGraph.GraphSha256 == compiled.GraphSha256, "graph identity does not depend on machine directory");
            string movedAssembly = Path.Combine(relocated, "OresGenerated.dll");
            CilEmitter.EmitLibrary(movedGraph.Module, movedAssembly);
            Require(File.ReadAllBytes(assembly).AsSpan().SequenceEqual(File.ReadAllBytes(movedAssembly)), "relocated graph emits identical CIL");
            using (var image = AssemblyDefinition.ReadAssembly(assembly))
            {
                var program = image.MainModule.Types.Single(t => t.Name == "OresProgram");
                Require(CfgCilTestInterpreter.Evaluate(program.Methods.Single(m => m.Name == "Run")) == 42, "linked CIL executes imported helpers");
                Require(program.Methods.Count == 5, "CIL has main and independent imported methods");
            }
            // Single-quoted local specifiers use the same graph, type, and source-root policy.
            // This probes the existing legacy .ores module-linking entrypoint; no new
            // guest feature is implemented by the host-path parser.
            Write("main.ores", "import module left from './left'; import module right from \"./right\"; pub fnc main():int { return left.value(10) + right.value(11); }");
            var singleQuoteImports = Compile();
            Require(singleQuoteImports.Sources.Count == 4, "single-quoted import loads same bounded source graph");
            CilEmitter.EmitLibrary(singleQuoteImports.Module, assembly);
            using (var image = AssemblyDefinition.ReadAssembly(assembly))
                Require(CfgCilTestInterpreter.Evaluate(image.MainModule.Types.Single(t => t.Name == "OresProgram").Methods.Single(m => m.Name == "Run")) == 42,
                    "mixed single/double quote import source produces real linked CIL result");
            Write("main.ores", "import module left from './le\\u0066t'; fnc main():int{return left.value(1);}");
            Reject(Compile, "escaped single-quoted import must be rejected", "unescaped local source string");
            File.WriteAllText(outside, "pub fnc first():int { return 1; }");
            Write("main.ores", "import module left from '../" + Path.GetFileName(outside) + "'; fnc main():int{return left.value(1);}");
            Reject(Compile, "single-quoted path traversal must remain rejected", "escapes explicit source roots");
            Write("main.ores", "import module left from \"./left\"; import module right from \"./right\"; pub fnc main():int { return left.value(10) + right.value(11); }");
            string hash = compiled.GraphSha256;
            Write("base.ores", "pub fnc twice(int n):int { return n * 2; } // changed source bytes");
            Require(Compile().GraphSha256 != hash, "transitive source bytes affect graph identity");
            Require(Compile().GraphSha256 == Compile().GraphSha256, "stable graph identity");
            Write("main.ores", "import module right from \"./right\"; import module left from \"./left\"; pub fnc main():int { return left.value(10) + right.value(11); }");
            var reordered = Compile();
            CilEmitter.EmitLibrary(reordered.Module, assembly);
            using (var image = AssemblyDefinition.ReadAssembly(assembly))
                Require(CfgCilTestInterpreter.Evaluate(image.MainModule.Types.Single(t => t.Name == "OresProgram").Methods.Single(m => m.Name == "Run")) == 42,
                    "reordered imports preserve scoped call resolution");
            Write("main.ores", "import module right from \"./right\"; fnc main():int { return right.hidden(); }");
            Reject(Compile, "private callable cannot cross module scope");
            Write("main.ores", "import module wrong from \"./right\"; fnc main():int { return wrong.value(1); }");
            Reject(Compile, "module name must match declaration");
            Write("main.ores", "import module right from \"./right\"; fnc main():int { val int right = 2; return right.value(1); }");
            Reject(Compile, "local does not silently become an imported namespace");
            Write("main.ores", "import fnc twice from \"./base\"; fnc main():int { return twice(true); }");
            Reject(Compile, "cross-file argument types are checked");
            Write("main.ores", "import fnc twice from \"./base\"; import fnc twice from \"./base\"; fnc main():int { return twice(21); }");
            Reject(Compile, "duplicate import bindings");
            Write("base.ores", "import fnc first from \"./main\"; pub fnc twice(int n):int { return n; }");
            Write("main.ores", "import fnc twice from \"./base\"; pub fnc first():int { return 1; } fnc main():int { return twice(21); }");
            Reject(Compile, "cycles do not bypass recursive ABI gate");
            File.WriteAllText(outside, "pub fnc first():int { return 1; }");
            Write("main.ores", "import fnc first from \"../" + Path.GetFileName(outside) + "\"; fnc main():int { return first(); }");
            Reject(Compile, "source root escape", "escapes explicit source roots");
            Write("main.ores", "fnc main():int { return 42; }");
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root, "main.ores"), new[] { root, root + Path.DirectorySeparatorChar }), "duplicate normalized roots");
            Write("main.ores", "import fnc twice from \"./base\"; fnc main():int { return twice(21); }");
            File.WriteAllBytes(Path.Combine(root, "base.ores"), new byte[] { 0xff, 0xfe });
            Reject(Compile, "invalid UTF-8 source cannot enter source identity graph", "strict UTF-8");
            Directory.CreateDirectory(Path.Combine(root, "dependency"));
            Write("dependency/.oreslangc.cfg.toml", "schema_version = \"1\"");
            Write("dependency/helper.ores", "pub fnc twice(int n):int { return n * 2; }");
            Write("main.ores", "import fnc twice from \"./dependency/helper\"; fnc main():int { return twice(21); }");
            Reject(Compile, "nested dependency needs explicit authority");
            Require(OresSourceModuleCompiler.Compile(Path.Combine(root, "main.ores"),
                new[] { root, Path.Combine(root, "dependency") }).Sources.Count == 2, "explicit dependency root admitted");
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(Path.Combine(root, "dependency/.oreslangc.cfg.toml"));
                File.CreateSymbolicLink(Path.Combine(root, "dependency/.oreslangc.cfg.toml"), Path.Combine(root, "dependency/missing.cfg"));
                Reject(Compile, "dangling package marker retains dependency boundary", "explicit dependency source root");
                File.CreateSymbolicLink(Path.Combine(root, "alias.ores"), Path.Combine(root, "dependency/helper.ores"));
                Write("main.ores", "import fnc twice from \"./alias\"; fnc main():int { return twice(21); }");
                Reject(Compile, "reparse aliases rejected");
            }
            Console.WriteLine("PASS source module graph, scoped imports, typed calls, CIL, source identity and fail-closed boundaries");
        }
        finally { Directory.Delete(root, true); File.Delete(outside); }

        void Write(string path, string source) => File.WriteAllText(Path.Combine(root, path), source);
        SourceModuleCompilation Compile() => OresSourceModuleCompiler.Compile(Path.Combine(root, "main.ores"), new[] { root });
    }

    private static void Require(bool value, string why)
    { if (!value) throw new Exception("Source module linking: " + why); }
    private static void Reject(Func<SourceModuleCompilation> compile, string why, string? diagnostic = null)
    {
        try { _ = compile(); }
        catch (Exception failure) when (failure is FormatException or IOException)
        {
            if (diagnostic is not null && !failure.Message.Contains(diagnostic, StringComparison.Ordinal))
                throw new Exception("Wrong rejection for " + why + ": " + failure.Message);
            return;
        }
        throw new Exception("Expected module rejection: " + why);
    }
}
