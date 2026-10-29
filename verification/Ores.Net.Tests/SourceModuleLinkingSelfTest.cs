using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
            // Link the exact canonical .ores bytes, not a C# core-library rewrite.
            // First test the identical source through the original relative
            // linker, then independently through the compiler-owned registry.
            string canonicalPath = Path.GetFullPath("library/ores.core/numeric.ores");
            byte[] coreBytes = File.ReadAllBytes(canonicalPath);
            Require(Convert.ToHexStringLower(SHA256.HashData(coreBytes)) ==
                "3d1d7b77068dd02b3d60b30b72f29a4537a933134ca79e1e81df096dc56f5f33",
                "canonical numeric source matches common-source v1");
            File.WriteAllBytes(Path.Combine(root, "numeric.ores"), coreBytes);
            Write("main.ores", "import module numeric from \"./numeric\"; pub fnc main():int { " +
                "return numeric.clamp_int(100, 0, 20) + numeric.max_int(11, 7) + numeric.min_int(11, 17); }");
            var nativeCore = Compile();
            Require(nativeCore.Sources.Count == 2, "native core is in executable source graph");
            Require(nativeCore.Sources.Single(u => u.Path == "0/numeric.ores").Sha256 ==
                "3d1d7b77068dd02b3d60b30b72f29a4537a933134ca79e1e81df096dc56f5f33",
                "native .ores digest is in graph witness");
            Require(nativeCore.Module.Helpers.Count == 3,
                "numeric functions have three independent compiled CIL methods");
            string coreAssembly = Path.Combine(root, "NativeCoreGenerated.dll");
            CilEmitter.EmitLibrary(nativeCore.Module, coreAssembly);
            using (var image = AssemblyDefinition.ReadAssembly(coreAssembly))
            {
                var program = image.MainModule.Types.Single(t => t.Name == "OresProgram");
                Require(CfgCilTestInterpreter.Evaluate(program.Methods.Single(m => m.Name == "Run")) == 42,
                    "unmodified canonical numeric Oreslang executes as linked CIL");
            }
            Write("main.ores", "import module numeric from \"./numeric\"; pub fnc main():int { " +
                "return numeric.max_int(-11, 7); }");
            var signedCore = Compile();
            CilEmitter.EmitLibrary(signedCore.Module, coreAssembly);
            using (var image = AssemblyDefinition.ReadAssembly(coreAssembly))
                Require(CfgCilTestInterpreter.Evaluate(image.MainModule.Types.Single(t => t.Name == "OresProgram")
                    .Methods.Single(m => m.Name == "Run")) == 7,
                    "signed greater-than uses signed CIL comparison");
            // The compiler-owned virtual module must resolve without a
            // dependency root or any writable project-owned source file.
            Directory.CreateDirectory(Path.Combine(root, "ores.core"));
            Write("ores.core/numeric.ores", "define module numeric as " +
                "pub fnc min_int(int a, int b):int { return 999; } end");
            Write("main.ores", "import module numeric from \"ores.core/numeric\"; " +
                "pub fnc main():int { return numeric.clamp_int(100, 0, 20) + " +
                "numeric.max_int(11, 7) + numeric.min_int(11, 17); }");
            var builtinCore = Compile();
            Require(builtinCore.Sources.Count == 2, "compiler-owned library enters graph once");
            Require(builtinCore.Sources.Single(u => u.Path == "@ores.core/numeric.ores").Sha256 ==
                "3d1d7b77068dd02b3d60b30b72f29a4537a933134ca79e1e81df096dc56f5f33",
                "closed core registry always supplies exact packaged Oreslang bytes");
            CilEmitter.EmitLibrary(builtinCore.Module, coreAssembly);
            byte[] canonicalAssemblyBytes = File.ReadAllBytes(coreAssembly);
            using (var image = AssemblyDefinition.ReadAssembly(coreAssembly))
                Require(CfgCilTestInterpreter.Evaluate(image.MainModule.Types.Single(t => t.Name == "OresProgram")
                    .Methods.Single(m => m.Name == "Run")) == 42,
                    "project-owned same-name file cannot shadow compiler-owned module");

            // An identical canonical import in another absolute project path
            // must keep the same graph witness and deterministic static CIL.
            // An attacker-controlled local file with the module's name in
            // either root must not alter the compiler-owned import.
            string relocatedCore = Path.Combine(root, "canonical-relocated");
            Directory.CreateDirectory(Path.Combine(relocatedCore, "ores.core"));
            File.WriteAllText(Path.Combine(relocatedCore, "main.ores"),
                File.ReadAllText(Path.Combine(root, "main.ores")));
            File.WriteAllText(Path.Combine(relocatedCore, "ores.core", "numeric.ores"),
                "define module numeric as pub fnc clamp_int(int a, int b, int c):int " +
                "{ return 999; } end");
            var sameCore = OresSourceModuleCompiler.Compile(
                Path.Combine(relocatedCore, "main.ores"), new[] { relocatedCore });
            Require(sameCore.GraphSha256 == builtinCore.GraphSha256,
                "compiler-owned numeric graph identity is independent of project root and shadow files");
            string relocatedCoreAssembly = Path.Combine(relocatedCore, "NativeCoreGenerated.dll");
            CilEmitter.EmitLibrary(sameCore.Module, relocatedCoreAssembly);
            Require(canonicalAssemblyBytes.AsSpan().SequenceEqual(
                File.ReadAllBytes(relocatedCoreAssembly)),
                "canonical module emits byte-identical CIL across physical project roots");
            Write("main.ores", "import module numeric from \"ores.core/numeric\"; " +
                "pub fnc main():int { return numeric.min_int(-9223372036854775808, 9223372036854775807); }");
            var minBound = Compile();
            CilEmitter.EmitLibrary(minBound.Module, coreAssembly);
            using (var image = AssemblyDefinition.ReadAssembly(coreAssembly))
                Require(CfgCilTestInterpreter.Evaluate(image.MainModule.Types.Single(t => t.Name == "OresProgram")
                    .Methods.Single(m => m.Name == "Run")) == long.MinValue,
                    "compiler-owned core preserves signed int64 minimum");
            Write("main.ores", "import module numeric from \"ores.core/numeric\"; " +
                "pub fnc main():int { return numeric.max_int(9007199254740993, 9007199254740992); }");
            var precision = Compile();
            CilEmitter.EmitLibrary(precision.Module, coreAssembly);
            using (var image = AssemblyDefinition.ReadAssembly(coreAssembly))
                Require(CfgCilTestInterpreter.Evaluate(image.MainModule.Types.Single(t => t.Name == "OresProgram")
                    .Methods.Single(m => m.Name == "Run")) == 9007199254740993L,
                    "canonical core preserves integer precision above 2^53");
            // Canonical Oreslang comparison semantics must be the same in
            // compile-time constants and typed runtime CFG/CIL lowering.
            Write("main.ores", "import module numeric from \"ores.core/numeric\"; " +
                "pub fnc main():int { const bool greater = 7 > -11; " +
                "if greater; do return numeric.max_int(-11, 7); fi return 0; }");
            var foldedGreater = Compile();
            CilEmitter.EmitLibrary(foldedGreater.Module, coreAssembly);
            using (var image = AssemblyDefinition.ReadAssembly(coreAssembly))
                Require(CfgCilTestInterpreter.Evaluate(image.MainModule.Types.Single(t => t.Name == "OresProgram")
                    .Methods.Single(m => m.Name == "Run")) == 7,
                    "signed greater-than constant folding agrees with native .ores CIL");
            Write("main.ores", "import module numeric from \"ores.core/numeric\"; " +
                "pub fnc main():int { if 2 > 5; do return 999; fi " +
                "return numeric.min_int(-11, 7); }");
            var runtimeGreaterFalse = Compile();
            CilEmitter.EmitLibrary(runtimeGreaterFalse.Module, coreAssembly);
            using (var image = AssemblyDefinition.ReadAssembly(coreAssembly))
                Require(CfgCilTestInterpreter.Evaluate(image.MainModule.Types.Single(t => t.Name == "OresProgram")
                    .Methods.Single(m => m.Name == "Run")) == -11,
                    "false signed greater-than branch and fallthrough execute correctly");
            Write("main.ores", "import module numeric from \"ores.core/missing\"; fnc main():int { return 1; }");
            Reject(Compile, "unknown compiler-owned library fails closed", "Unknown compiler-owned");
            Write("main.ores", "import module numeric from \"ores.core/numeric.ores\"; fnc main():int { return 1; }");
            Reject(Compile, "alternate canonical core alias fails closed", "Unknown compiler-owned");
            Write("main.ores", "import module numeric from \"ores.core/numeric\"; " +
                "import module numeric from \"ores.core/numeric\"; fnc main():int { return 1; }");
            Reject(Compile, "duplicate compiler-owned binding fails closed", "Duplicate import binding");
            File.Delete(Path.Combine(root, "numeric.ores"));
            Write("main.ores", "import module left from \"./left\"; import module right from \"./right\"; " +
                "pub fnc main():int { return left.value(10) + right.value(11); }");
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
                // Two distinct Linux filenames can produce the same identity
                // when literal '\' is normalized to '/'.
                Directory.CreateDirectory(Path.Combine(root, "identity"));
                Write("identity/entry.ores", "pub fnc main():int { return 42; }");
                Write("identity\\entry.ores", "pub fnc main():int { return 41; }");
                string nestedName = Path.Combine(root, "identity", "entry.ores");
                string backslashName = Path.Combine(root, "identity\\entry.ores");
                Require(File.Exists(nestedName) && File.Exists(backslashName),
                    "both Unix source identity collision files exist");
                Require(Path.GetRelativePath(root, nestedName).Replace('\\', '/') ==
                        Path.GetRelativePath(root, backslashName).Replace('\\', '/'),
                    "identity normalization would collide without admission");
                Reject(() => OresSourceModuleCompiler.Compile(backslashName, new[] { root }),
                    "literal backslash filename cannot alias source identity",
                    "Unsupported source path identity character");
                string controlName = Path.Combine(root, "identity\ncontrol.ores");
                File.WriteAllText(controlName, "pub fnc main():int { return 41; }");
                Reject(() => OresSourceModuleCompiler.Compile(controlName, new[] { root }),
                    "newline filename cannot break source graph witness framing",
                    "Unsupported source path identity character");

                // A real leaf directory below a symlinked parent must not be
                // accepted as an explicit source root. Checking only the
                // leaf's ReparsePoint attribute would miss this alias.
                Directory.CreateDirectory(Path.Combine(root, "safe-root", "inner"));
                Write("safe-root/inner/entry.ores", "pub fnc main():int { return 42; }");
                Directory.CreateSymbolicLink(Path.Combine(root, "aliased-parent"),
                    Path.Combine(root, "safe-root"));
                string aliasedRoot = Path.Combine(root, "aliased-parent", "inner");
                Reject(() => OresSourceModuleCompiler.Compile(
                    Path.Combine(aliasedRoot, "entry.ores"), new[] { aliasedRoot }),
                    "symlinked ancestor of an explicit source root", "Symlink/reparse");

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
