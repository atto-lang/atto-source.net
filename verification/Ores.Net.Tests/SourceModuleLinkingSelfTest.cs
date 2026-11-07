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
            // Canonical source files use the SAME real typed linker and CIL
            // emitter, not a parallel snapshot validator or a fallback parser.
            Write("main.atto",
                "import module left from \"./left.atto\"; import module right from \"./right.atto\"; " +
                "pub fnc main():int { return left.value(10) + right.value(11); }");
            Write("left.atto",
                "import fnc twice from \"./base.atto\"; " +
                "define module left as pub fnc value(int n)->int{return twice(n);} end");
            Write("right.atto",
                "define module right as pub fnc value(int n):int{return n * 2;} end");
            Write("base.atto","pub fnc twice(int n):int{return n * 2;}");
            var canonical = OresSourceModuleCompiler.Compile(
                Path.Combine(root,"main.atto"), new[] { root });
            Require(canonical.Sources.Count == 4, "canonical .atto graph has four real parsed units");
            Require(canonical.Sources.All(x => x.Path.EndsWith(".atto", StringComparison.Ordinal)),
                "canonical imports never rewrite into legacy .ores files");
            string attoAssembly = Path.Combine(root, "AttoGenerated.dll");
            CilEmitter.EmitLibrary(canonical.Module, attoAssembly);
            using (var image = AssemblyDefinition.ReadAssembly(attoAssembly))
            {
                var program = image.MainModule.Types.Single(t => t.Name == "OresProgram");
                Require(CfgCilTestInterpreter.Evaluate(program.Methods.Single(m => m.Name == "Run")) == 42,
                    "canonical .atto imported methods execute as typed CIL");
            }
            var attoHash = canonical.GraphSha256;
            Write("base.atto", "pub fnc twice(int n):int{return n * 2;} // atto body mutation");
            var changedAtto = OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root});
            Require(changedAtto.GraphSha256 != attoHash,"mutated transitive .atto bytes change graph identity");
            // Changing a source comment proves only the graph digest changes.
            // Also mutate executable semantics and insist the REAL typed CIL
            // emitter/interpreter reflects the new transitive module behavior.
            Write("base.atto", "pub fnc twice(int n):int{return n * 3;}");
            var changedBehavior = OresSourceModuleCompiler.Compile(
                Path.Combine(root,"main.atto"),new[]{root});
            Require(changedBehavior.GraphSha256 != attoHash,
                "transitive semantic mutation must change graph identity");
            string changedAssembly = Path.Combine(root, "AttoSemanticMutation.dll");
            CilEmitter.EmitLibrary(changedBehavior.Module, changedAssembly);
            using (var changedImage = AssemblyDefinition.ReadAssembly(changedAssembly))
            {
                var changedProgram = changedImage.MainModule.Types.Single(t => t.Name == "OresProgram");
                Require(CfgCilTestInterpreter.Evaluate(
                    changedProgram.Methods.Single(m => m.Name == "Run")) == 52,
                    "real linked CIL must reflect mutated imported helper rather than stale result 42");
            }
            Require(!File.ReadAllBytes(attoAssembly).AsSpan().SequenceEqual(
                File.ReadAllBytes(changedAssembly)),
                "compiler must not return stale CIL when imported module behavior changes");
            Write("base.atto", "pub fnc twice(int n):int{return n * 2;}");
            Write("main.atto", "import fnc twice from \"./base.txt\"; fnc main():int{return twice(21);}");
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                "unknown source extension stays denied", "canonical .atto / legacy .ores");
            Write("main.atto", "import fnc twice from \"./missing.atto\"; fnc main():int{return twice(21);}");
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                "missing canonical source stays denied");
            Write("main.atto", "import fnc twice from \"./base.atto\"; fnc main():int{return twice(21);}");
            File.WriteAllBytes(Path.Combine(root,"base.atto"),new byte[]{0xff,0xfe});
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                "invalid canonical UTF-8 stays denied", "strict UTF-8");
            // Canonical source graph negative tests must go through the real
            // compiler linker; no source-text substring-only policy oracle.
            Write("base.atto", "pub fnc twice(int n):int{return n * 2;}");
            Write("main.atto",
                "import fnc twice from \"./base.atto\"; import fnc twice from \"./base.atto\"; " +
                "fnc main():int{return twice(21);}");
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                "canonical duplicate imported callable");
            Write("base.atto","fnc hidden(int n):int{return n;}");
            Write("main.atto",
                "import fnc hidden from \"./base.atto\"; fnc main():int{return hidden(42);}");
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                "canonical private callable cannot escape module");
            Write("base.atto","pub fnc twice(int n):int{return n * 2;}");
            Write("main.atto",
                "import fnc twice from \"atto.core/numeric\"; fnc main():int{return twice(21);}");
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                "canonical registry import cannot silently fall back to local files",
                "explicit local");
            Write("legacy-local.ores","pub fnc twice(int n):int{return n * 2;}");
            Write("main.atto",
                "import fnc twice from \"./legacy-local.ores\"; fnc main():int{return twice(21);}");
            var mixed = OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root});
            Require(mixed.Sources.Count==2 && mixed.Sources.Any(x=>x.Path.EndsWith("legacy-local.ores")),
                "explicit legacy .ores import from canonical .atto graph admitted only by intent");
            string mixedAssembly = Path.Combine(root,"AttoMixedLegacy.dll");
            CilEmitter.EmitLibrary(mixed.Module, mixedAssembly);
            using (var mixedImage = AssemblyDefinition.ReadAssembly(mixedAssembly))
                Require(CfgCilTestInterpreter.Evaluate(
                    mixedImage.MainModule.Types.Single(t => t.Name == "OresProgram")
                        .Methods.Single(m => m.Name == "Run"))==42,
                    "explicit mixed .atto→.ores import must execute through typed CIL");
            Write("main.atto",
                "import fnc twice from \"./base\"; fnc main():int{return twice(21);}");
            Write("base.ores","pub fnc twice(int n):int{return n * 2;}");
            var suffix = OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root});
            Require(suffix.Sources.Any(x=>x.Path.EndsWith("base.ores")) &&
                    !suffix.Sources.Any(x=>x.Path.EndsWith("base.atto")),
                "unsuffixed import retains legacy .ores identity, never silent canonical fallback");
            string suffixAssembly = Path.Combine(root,"AttoUnsuffixedLegacy.dll");
            CilEmitter.EmitLibrary(suffix.Module, suffixAssembly);
            using (var suffixImage = AssemblyDefinition.ReadAssembly(suffixAssembly))
                Require(CfgCilTestInterpreter.Evaluate(
                    suffixImage.MainModule.Types.Single(t => t.Name == "OresProgram")
                        .Methods.Single(m => m.Name == "Run"))==42,
                    "legacy extension fallback must be executable and preserve called semantics");
            string externalAtto = root+"-outside.atto";
            try
            {
                File.WriteAllText(externalAtto,"pub fnc twice(int n):int{return n;}");
                Write("main.atto",
                    "import fnc twice from \"../"+Path.GetFileName(externalAtto)+
                    "\"; fnc main():int{return twice(42);}");
                Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                    "canonical .atto traversal beyond source root", "escapes explicit source roots");
            }
            finally { File.Delete(externalAtto); }
            string dependencyAtto = Path.Combine(root,"atto-dep");
            Directory.CreateDirectory(dependencyAtto);
            Write("atto-dep/.ores.pkg.toml","schema_version=\"1\"");
            Write("atto-dep/helper.atto","pub fnc twice(int n):int{return n*2;}");
            Write("main.atto",
                "import fnc twice from \"./atto-dep/helper.atto\"; fnc main():int{return twice(21);}");
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                "nested canonical package requires explicit dependency root");
            Require(OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),
                new[]{root,dependencyAtto}).Sources.Count==2,
                "explicit atto package dependency root admitted");
            Write("main.atto",
                "import fnc twice from \"./base.atto\"; fnc main():int{return twice(21);}");
            File.WriteAllBytes(Path.Combine(root,"base.atto"),new byte[]{0xef,0xbb,0xbf});
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                "canonical BOM source is not admitted", "BOM");
            File.WriteAllBytes(Path.Combine(root,"base.atto"),new byte[1_048_577]);
            Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                "canonical oversized source is not admitted", "1 MiB");
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(Path.Combine(root,"base.atto"));
                File.CreateSymbolicLink(Path.Combine(root,"base.atto"),
                    Path.Combine(root,"legacy-local.ores"));
                Reject(() => OresSourceModuleCompiler.Compile(Path.Combine(root,"main.atto"),new[]{root}),
                    "canonical .atto symlink import must not redirect to legacy source");
            }
            Console.WriteLine("PASS canonical .atto negative imports, nested package authority, witness and path guards");
            Console.WriteLine("PASS AttoLang .atto real module-linking, typed CIL execution, source identity and negatives");
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
