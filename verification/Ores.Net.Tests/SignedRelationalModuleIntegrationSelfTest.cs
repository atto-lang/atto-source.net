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
        var codes = program.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(ins => ins.OpCode.Code)).ToArray();
        if (!codes.Contains(Code.Clt) || !codes.Contains(Code.Cgt) ||
            codes.Contains(Code.Clt_Un) || codes.Contains(Code.Cgt_Un))
            throw new Exception("Relational module CIL lost signed comparisons");
        bool invertedGreater = codes.Zip(codes.Skip(1), (a, b) =>
            a == Code.Cgt && b == Code.Ldc_I4_0).Any();
        bool invertedLess = codes.Zip(codes.Skip(1), (a, b) =>
            a == Code.Clt && b == Code.Ldc_I4_0).Any();
        if (!invertedGreater || !invertedLess)
            throw new Exception("Relational module CIL omitted <= or >= inversion");
    }

    private static void ExpectRejected(Func<SourceModuleCompilation> compile, string label)
    {
        try { _ = compile(); }
        catch (FormatException) { return; }
        throw new Exception("Invalid relational module admitted: " + label);
    }
}
