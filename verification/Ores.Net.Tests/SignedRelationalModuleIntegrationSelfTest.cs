using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

// Independent source-graph/CIL integration oracle. Not a Native AOT guest run.
internal static class SignedRelationalModuleIntegrationSelfTest
{
    private static readonly string[] Files =
    {
        "atto-relational-arithmetic.atto",
        "atto-relational-limits.atto",
        "atto-relational-main.atto",
        "atto-relational-values.atto"
    };

    [ModuleInitializer]
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "atto-relational-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string filename in Files)
            {
                string source = Path.Combine(AppContext.BaseDirectory,
                    "module-fixtures", filename);
                File.Copy(source, Path.Combine(directory, filename));
            }
            string entry = Path.Combine(directory, "atto-relational-main.atto");
            var compilation = OresSourceModuleCompiler.Compile(entry, new[] { directory });
            if (compilation.Sources.Count != 4 ||
                compilation.Sources.Any(source => !source.Path.EndsWith(".atto",
                    StringComparison.Ordinal)))
                throw new Exception("Signed relational module compilation has a wrong source graph");
            foreach (var unit in compilation.Sources)
            {
                string filename = unit.Path.Split('/').Last();
                if (!Files.Contains(filename, StringComparer.Ordinal) ||
                    unit.Path != "0/" + filename)
                    throw new Exception("Noncanonical relational source identity: " + unit.Path);
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, filename));
                if (unit.Utf8Bytes != bytes.Length ||
                    unit.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)))
                    throw new Exception("Compiler source witness diverged from actual fixture bytes");
            }

            string cil = Path.Combine(directory, "OresGenerated.dll");
            CilEmitter.EmitLibrary(compilation.Module, cil);
            AssertCil(cil, 42);
            string baselineHash = compilation.GraphSha256;
            byte[] baselineCil = File.ReadAllBytes(cil);

            string arithmetic = Path.Combine(directory, "atto-relational-arithmetic.atto");
            string original = File.ReadAllText(arithmetic);
            if (original.Split("n * 2").Length != 2)
                throw new Exception("Relational transitive mutation anchor missing");
            File.WriteAllText(arithmetic, original.Replace("n * 2", "n * 2 + 1",
                StringComparison.Ordinal));
            var mutated = OresSourceModuleCompiler.Compile(entry, new[] { directory });
            if (mutated.GraphSha256 == baselineHash)
                throw new Exception("Transitive relational arithmetic mutation did not change source graph");
            string changedCil = Path.Combine(directory, "OresChanged.dll");
            CilEmitter.EmitLibrary(mutated.Module, changedCil);
            AssertCil(changedCil, 43);
            if (baselineCil.AsSpan().SequenceEqual(File.ReadAllBytes(changedCil)))
                throw new Exception("Transitive 42-to-43 mutation returned stale CIL");
            File.WriteAllText(arithmetic, original);

            // Exercise imported signed comparison calls on both true and false
            // branches, especially extrema where unsigned IL gives wrong answers.
            // Each case links a fresh .atto entrypoint and executes its emitted CIL.
            string probe = Path.Combine(directory, "atto-relational-probe.atto");
            foreach (var (method, lhs, rhs, expected) in new (string, long, long, bool)[] {
                ("at_least", long.MinValue, 0, false),
                ("at_least", 0, long.MinValue, true),
                ("at_least", long.MinValue, long.MinValue, true),
                ("at_least", long.MaxValue, long.MaxValue, true),
                ("at_least", long.MaxValue - 1, long.MaxValue, false),
                ("at_least", -7, -8, true),
                ("at_least", -8, -7, false),
                ("at_most", long.MinValue, 0, true),
                ("at_most", 0, long.MinValue, false),
                ("at_most", long.MaxValue, long.MaxValue, true),
                ("at_most", long.MaxValue, long.MaxValue - 1, false),
                ("at_most", long.MinValue, long.MinValue, true),
                ("at_most", -8, -7, true),
                ("at_most", -7, -8, false)
            })
            {
                string condition = FormattableString.Invariant(
                    $"limits.{method}({lhs},{rhs})");
                File.WriteAllText(probe,
                    "import module limits from \"./atto-relational-limits.atto\"; " +
                    "pub fnc main():int{if " + condition +
                    "{return 42;}return 0;}");
                var linked = OresSourceModuleCompiler.Compile(probe, new[] { directory });
                if (linked.Sources.Count != 2 ||
                    linked.Sources.Any(unit => !unit.Path.EndsWith(".atto",
                        StringComparison.Ordinal)))
                    throw new Exception("Signed relational probe imported an unexpected source");
                string probeCil = Path.Combine(directory, "AttoRelationalProbe.dll");
                File.Delete(probeCil);
                CilEmitter.EmitLibrary(linked.Module, probeCil);
                using var pe = AssemblyDefinition.ReadAssembly(probeCil);
                var methodDef = pe.MainModule.Types.Single(t => t.Name == "OresProgram")
                    .Methods.Single(m => m.Name == "Run");
                long actual = CfgCilTestInterpreter.Evaluate(methodDef);
                if (actual != (expected ? 42 : 0))
                    throw new Exception("Incorrect signed imported relational result: " +
                        condition + " => " + actual);
            }
            File.WriteAllText(probe,
                "import module limits from \"./atto-relational-limits.atto\"; " +
                "pub fnc main():int{if limits.at_most(true,1){return 42;}return 0;}");
            ExpectRejected(() => OresSourceModuleCompiler.Compile(probe,
                new[] { directory }), "imported bool/i64 comparison call");
            File.WriteAllText(probe,
                "import module limits from \"./atto-relational-limits.atto\"; " +
                "pub fnc main():int{if 1 >= 2 <= 3{return 42;}return 0;}");
            ExpectRejected(() => OresSourceModuleCompiler.Compile(probe,
                new[] { directory }), "chained relational result cannot become i64");
            File.WriteAllText(probe,
                "import module limits from \"./atto-relational-limits.atto\"; " +
                "pub fnc main():int{return limits.missing(42);}");
            ExpectRejected(() => OresSourceModuleCompiler.Compile(probe,
                new[] { directory }), "unknown qualified imported callable");

            string limits = Path.Combine(directory, "atto-relational-limits.atto");
            string unmodified = File.ReadAllText(limits);
            File.WriteAllText(limits, unmodified.Replace("value >= bound",
                "value >= true", StringComparison.Ordinal));
            ExpectRejected(() => OresSourceModuleCompiler.Compile(entry,
                new[] { directory }), "module relational bool/int mismatch");
            File.WriteAllText(limits, unmodified);

            string program = File.ReadAllText(entry);
            File.WriteAllText(entry, program.Replace("./atto-relational-limits.atto",
                "../outside.atto", StringComparison.Ordinal));
            ExpectRejected(() => OresSourceModuleCompiler.Compile(entry,
                new[] { directory }), "module import escaped source root");
            File.WriteAllText(entry, program);

            File.WriteAllBytes(arithmetic, new byte[] { 0xff, 0xfe });
            ExpectRejected(() => OresSourceModuleCompiler.Compile(entry,
                new[] { directory }), "module import invalid UTF-8");

            Console.WriteLine("PASS signed relational four-file .atto graph: typed CIL 42/43, IL opcode shape, hashes, denied imports and types (NOT native)");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertCil(string path, long expected)
    {
        using var image = AssemblyDefinition.ReadAssembly(path);
        var program = image.MainModule.Types.Single(t => t.Name == "OresProgram");
        var run = program.Methods.Single(m => m.Name == "Run");
        if (CfgCilTestInterpreter.Evaluate(run) != expected)
            throw new Exception("Signed relational linked CIL result mismatch: " + expected);
        var methods = program.Methods.Where(m => m.HasBody)
            .Select(m => m.Body.Instructions.Select(ins => ins.OpCode.Code).ToArray())
            .ToArray();
        if (methods.SelectMany(codes => codes).Any(code =>
            code is Code.Clt_Un or Code.Cgt_Un))
            throw new Exception("Unsigned comparison opcode in relational module CIL");
        if (!methods.SelectMany(codes => codes).Contains(Code.Clt) ||
            !methods.SelectMany(codes => codes).Contains(Code.Cgt))
            throw new Exception("Relational module CIL lost signed comparisons");
        // A match may not cross MethodDef boundaries: every inversion is a
        // contiguous signed comparison, ldc.i4.0, ceq within one method.
        bool invertedGreater = methods.Any(codes => Inverts(codes, Code.Cgt));
        bool invertedLess = methods.Any(codes => Inverts(codes, Code.Clt));
        if (!invertedGreater || !invertedLess)
            throw new Exception("Relational module CIL omitted <= or >= inversion");

        static bool Inverts(Code[] codes, Code comparison)
        {
            for (int i = 0; i + 2 < codes.Length; i++)
                if (codes[i] == comparison && codes[i + 1] == Code.Ldc_I4_0 &&
                    codes[i + 2] == Code.Ceq) return true;
            return false;
        }
    }

    private static void ExpectRejected(Func<SourceModuleCompilation> compile, string label)
    {
        try { _ = compile(); }
        catch (FormatException) { return; }
        throw new Exception("Invalid relational module admitted: " + label);
    }
}
