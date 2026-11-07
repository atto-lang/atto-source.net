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
            // The compiler-owned atto.core/identity resource is parsed and
            // typed-linked by the same real linker/CIL path as local sources.
            // Canonical and legacy names share one immutable bundled unit.
            var coreAliases = new[] {
                "atto.core/identity", "atto.core/identity.atto", "atto.core/identity.ores",
                "ores.core/identity", "ores.core/identity.atto", "ores.core/identity.ores"
            };
            foreach (var alias in coreAliases)
            {
                Write("core-entry.atto",
                    "import module identity from \"" + alias +
                    "\"; pub fnc main():int{return identity.passthrough(42);}");
                var core = OresSourceModuleCompiler.Compile(
                    Path.Combine(root, "core-entry.atto"), new[] { root });
                Require(core.Sources.Count == 2, "exactly two graph units for " + alias);
                Require(core.Sources.Count(x => x.Path == "@atto.core/identity.atto") == 1,
                    "single compiler-owned canonical source identity for " + alias);
                Require(core.Sources.Single(x => x.Path == "@atto.core/identity.atto").Utf8Bytes > 0,
                    "compiler-owned resource has a real source-byte witness");
                string coreAssembly = Path.Combine(root, "AttoCoreIdentity.dll");
                CilEmitter.EmitLibrary(core.Module, coreAssembly);
                using var coreImage = AssemblyDefinition.ReadAssembly(coreAssembly);
                Require(CfgCilTestInterpreter.Evaluate(
                    coreImage.MainModule.Types.Single(x => x.Name == "OresProgram")
                        .Methods.Single(x => x.Name == "Run")) == 42,
                    "compiler-owned source actually linked and executed via typed CIL");
            }
            Directory.CreateDirectory(Path.Combine(root, "atto.core"));
            File.WriteAllText(Path.Combine(root, "atto.core", "identity.atto"),
                "define module identity as pub fnc passthrough(int value):int{return 99;} end");
            Write("core-entry.atto",
                "import module identity from \"atto.core/identity\"; " +
                "pub fnc main():int{return identity.passthrough(42);}");
            var shadowDenied = OresSourceModuleCompiler.Compile(
                Path.Combine(root, "core-entry.atto"), new[] { root });
            Require(shadowDenied.Sources.All(x => x.Path != "0/atto.core/identity.atto"),
                "ambient Core path must never replace embedded resource");
            string shadowAssembly = Path.Combine(root, "AttoCoreShadowProof.dll");
            CilEmitter.EmitLibrary(shadowDenied.Module, shadowAssembly);
            using (var shadowImage = AssemblyDefinition.ReadAssembly(shadowAssembly))
                Require(CfgCilTestInterpreter.Evaluate(
                    shadowImage.MainModule.Types.Single(x => x.Name == "OresProgram")
                        .Methods.Single(x => x.Name == "Run")) == 42,
                    "project-local Core shadow did not override compiler source");
            foreach (var invalid in new[] {
                "atto.core/missing", "atto.core/identity/",
                "atto.core/../identity", "atto.alloc/identity",
                "atto.std/identity", "atto.runtime.actor/identity"
            })
            {
                Write("core-entry.atto",
                    "import module identity from \"" + invalid +
                    "\"; pub fnc main():int{return identity.passthrough(42);}");
                Reject(() => OresSourceModuleCompiler.Compile(
                    Path.Combine(root, "core-entry.atto"), new[] { root }),
                    "unsupported or malformed canonical namespace " + invalid);
            }
            Write("core-entry.atto",
                "import module counterfeit from \"atto.core/identity\"; " +
                "pub fnc main():int{return counterfeit.passthrough(42);}");
            Reject(() => OresSourceModuleCompiler.Compile(
                Path.Combine(root, "core-entry.atto"), new[] { root }),
                "compiler-owned declared module alias mismatch");
            Console.WriteLine("PASS stage-1 compiler-owned atto.core/identity embedded source, CIL execution and namespace-shadow negatives");
            // Canonical compiler-owned numeric.atto is linked unchanged,
            // including if/; do/fi and early returns. It is NOT rewritten
            // to host C# or silently replaced with built-in min/max methods.
            var numericCases = new (string Expression, long Result)[] {
                ("numeric.min_int(40,42)",40),
                ("numeric.min_int(42,40)",40),
                ("numeric.min_int(42,42)",42),
                ("numeric.max_int(40,42)",42),
                ("numeric.max_int(42,40)",42),
                ("numeric.max_int(42,42)",42),
                ("numeric.clamp_int(50,0,42)",42),
                ("numeric.clamp_int(-10,0,42)",0),
                ("numeric.clamp_int(20,0,42)",20),
                ("numeric.clamp_int(2,10,5)",10),
                ("numeric.clamp_int(5,5,5)",5),
                ("numeric.max_int(-9223372036854775807,9223372036854775807)",
                    9223372036854775807)
            };
            foreach (var (expression, expected) in numericCases)
            {
                Write("numeric-entry.atto",
                    "import module numeric from \"atto.core/numeric\"; " +
                    "pub fnc main():int{return " + expression + ";}");
                var linkedNumeric = OresSourceModuleCompiler.Compile(
                    Path.Combine(root, "numeric-entry.atto"), new[] { root });
                Require(linkedNumeric.Sources.Count == 2 &&
                    linkedNumeric.Sources.Count(x => x.Path=="@atto.core/numeric.atto")==1,
                    "compiler-owned Core numeric source identity is bound to graph");
                string executable = Path.Combine(root, "AttoCoreNumeric.dll");
                CilEmitter.EmitLibrary(linkedNumeric.Module, executable);
                using var image = AssemblyDefinition.ReadAssembly(executable);
                var actual = CfgCilTestInterpreter.Evaluate(
                    image.MainModule.Types.Single(x => x.Name=="OresProgram")
                        .Methods.Single(x => x.Name=="Run"));
                Require(actual==expected,
                    "canonical numeric guest result mismatch: " + expression);
            }
            foreach (string alias in new[] {
                "atto.core/numeric", "atto.core/numeric.atto",
                "atto.core/numeric.ores", "ores.core/numeric",
                "ores.core/numeric.atto", "ores.core/numeric.ores"
            })
            {
                Write("numeric-entry.atto", "import module numeric from \"" + alias +
                    "\"; pub fnc main():int{return numeric.max_int(40,42);}");
                var linked = OresSourceModuleCompiler.Compile(
                    Path.Combine(root, "numeric-entry.atto"), new[] { root });
                Require(linked.Sources.Single(x => x.Path=="@atto.core/numeric.atto").Utf8Bytes > 0,
                    "all canonical and legacy numeric imports bind bundled real source");
            }
            Directory.CreateDirectory(Path.Combine(root,"atto.core"));
            Write("atto.core/numeric.atto",
                "define module numeric as pub fnc max_int(int l,int r):int{return 99;} end");
            Write("numeric-entry.atto",
                "import module numeric from \"atto.core/numeric\"; " +
                "pub fnc main():int{return numeric.max_int(40,42);}");
            var numericShadow=OresSourceModuleCompiler.Compile(
                Path.Combine(root,"numeric-entry.atto"),new[]{root});
            Require(numericShadow.Sources.All(x=>x.Path!="0/atto.core/numeric.atto"),
                "project-local numeric shadow path cannot claim canonical source");
            CilEmitter.EmitLibrary(numericShadow.Module,Path.Combine(root,"NumericShadow.dll"));
            using (var shadowImage = AssemblyDefinition.ReadAssembly(Path.Combine(root,"NumericShadow.dll")))
                Require(CfgCilTestInterpreter.Evaluate(
                    shadowImage.MainModule.Types.Single(x=>x.Name=="OresProgram")
                        .Methods.Single(x=>x.Name=="Run"))==42,
                    "project numeric shadow did not alter compiled Core guest");
            foreach (string invalid in new[] {
                "atto.core/numeric/", "atto.core/../numeric",
                "atto.core/numeric//", "atto.core/unknown",
                "atto.alloc/numeric", "atto.std/numeric",
                "atto.runtime.actor/numeric"
            })
            {
                Write("numeric-entry.atto", "import module numeric from \"" + invalid +
                    "\"; pub fnc main():int{return numeric.max_int(40,42);}");
                Reject(()=>OresSourceModuleCompiler.Compile(
                    Path.Combine(root,"numeric-entry.atto"),new[]{root}),
                    "invalid compiler-owned numeric import "+invalid);
            }
            Write("numeric-entry.atto",
                "import module fake from \"atto.core/numeric\"; " +
                "pub fnc main():int{return fake.max_int(40,42);}");
            Reject(()=>OresSourceModuleCompiler.Compile(
                Path.Combine(root,"numeric-entry.atto"),new[]{root}),
                "numeric module alias mismatch must fail");
            Write("broken-do-fi.atto",
                "pub fnc main():int{if 1>0; do return 42; return 0;}");
            Reject(()=>OresSourceModuleCompiler.Compile(
                Path.Combine(root,"broken-do-fi.atto"),new[]{root}),
                "missing fi cannot escape typed block validation");
            // One canonical imported numeric .atto compile, three *real*
            // compiler-emitted helper MethodDefs, and 30,000 parameterized
            // CIL executions against an independent signed i64 reference.
            Write("numeric-entry.atto",
                "import module numeric from \"atto.core/numeric\"; " +
                "pub fnc main():int{return numeric.clamp_int(50,0,42);}");
            var numericBatch = OresSourceModuleCompiler.Compile(
                Path.Combine(root, "numeric-entry.atto"), new[] { root });
            string numericBatchFile = Path.Combine(root, "AttoCoreNumericBatch.dll");
            CilEmitter.EmitLibrary(numericBatch.Module, numericBatchFile);
            using (var numericImage = AssemblyDefinition.ReadAssembly(numericBatchFile))
            {
                var emitted = numericImage.MainModule.Types.Single(x => x.Name == "OresProgram");
                long[] edges = {
                    long.MinValue, long.MinValue+1, -43, -1, 0, 1, 42,
                    long.MaxValue-1, long.MaxValue
                };
                foreach (string operation in new[] { "min_int", "max_int", "clamp_int" })
                {
                    string canonicalMethodName = "helper_m_" +
                        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes("@atto.core/numeric.atto\0" + operation)));
                    var helper = emitted.Methods.Single(x => x.Name == canonicalMethodName);
                    Require(helper.IsPrivate && helper.IsStatic &&
                        helper.Parameters.Count == (operation == "clamp_int" ? 3 : 2),
                        "exact canonical numeric helper ABI missing: " + operation);
                    ulong rng = 0x6a09e667f3bcc909UL;
                    ulong NextNumericBits()
                    {
                        unchecked
                        {
                            ulong z = (rng += 0x9e3779b97f4a7c15UL);
                            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
                            z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
                            return z ^ (z >> 31);
                        }
                    }
                    ulong digest = 14695981039346656037UL;
                    for (int i = 0; i < 10_000; i++)
                    {
                        long a, b, c;
                        if (i < edges.Length * edges.Length * edges.Length)
                        {
                            a = edges[(i / (edges.Length * edges.Length)) % edges.Length];
                            b = edges[(i / edges.Length) % edges.Length];
                            c = edges[i % edges.Length];
                        }
                        else
                        {
                            a = unchecked((long)NextNumericBits());
                            b = unchecked((long)NextNumericBits());
                            c = unchecked((long)NextNumericBits());
                        }
                        long expected = operation switch
                        {
                            "min_int" => a < b ? a : b,
                            "max_int" => a > b ? a : b,
                            "clamp_int" => b > c ? b : (a < b ? b : (a > c ? c : a)),
                            _ => throw new FormatException("Invalid numeric differential operation")
                        };
                        var inputs = operation == "clamp_int"
                            ? new[] { a, b, c } : new[] { a, b };
                        long actual = CfgCilTestInterpreter.Evaluate(helper, inputs);
                        Require(actual == expected,
                            "Compiler-emitted CIL numeric differential mismatch for " +
                            operation + ", index " + i);
                        digest = unchecked((digest ^ (ulong)actual) * 1099511628211UL);
                    }
                    string checksum = digest.ToString("x16");
                    string expectedChecksum = operation switch
                    {
                        "min_int" => "a52c659779088c2b",
                        "max_int" => "bb3870b3c10fd615",
                        "clamp_int" => "e71b3e7766936548",
                        _ => throw new FormatException("Invalid numeric differential operation")
                    };
                    Require(checksum == expectedChecksum,
                        "Numeric CIL differential checksum disagrees with independent Forge oracle");
                }
            }
            Console.WriteLine("PASS compiler-emitted canonical numeric CIL: 30000 parameterized signed-i64 differential results");
            Console.WriteLine("PASS canonical numeric.atto compiler-owned 12 CIL results, six aliases, shadowing and negative imports");
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
