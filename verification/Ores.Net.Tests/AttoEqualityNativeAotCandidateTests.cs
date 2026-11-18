using Mono.Cecil;
using Ores.Net.Compiler;

// This test exercises the real structured parser -> typed verified CFG ->
// deterministic build-time CIL bytes and a CIL evaluator. It is NOT a proof
// of published NativeAOT execution: see verification/assert-atto-equality-native-aot.sh.
internal static class AttoEqualityNativeAotCandidateTests
{
    private static void Expect(long expected, string source, string label)
    {
        var program = OresStructuredCfgLowering.Lower(source);
        if (CfgReferenceEvaluator.Evaluate(program) != expected)
            throw new Exception("Atto equality typed CFG result mismatch: " + label);

        var folder = Path.Combine(Path.GetTempPath(),
            "atto-equality-cil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(program, path);
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            var method = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
            if (CfgCilTestInterpreter.Evaluate(method) != expected)
                throw new Exception("Atto equality emitted CIL result mismatch: " + label);
            var again = Path.Combine(folder, "OresGenerated.2.dll");
            CilEmitter.EmitLibrary(program, again);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(again)))
                throw new Exception("Atto equality CIL output nondeterministic: " + label);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void Reject(string source, string label)
    {
        try { _ = OresStructuredCfgLowering.Lower(source); }
        catch (FormatException) { return; }
        throw new Exception("Atto equality unsupported syntax was accepted: " + label);
    }

    public static void Run()
    {
        Expect(42, "fnc main():int{if 7 !eq 8 {return 42;}return 0;}", "atomic !eq");
        Expect(42, "fnc main():int{if 7 neq 8 {return 42;}return 0;}", "canonical neq");
        Expect(42, "fnc main():int{if 7 eq 7 {return 42;}return 0;}", "canonical eq");
        Expect(42, "fnc main():int{if 7 + 1 !eq 7 {return 42;}return 0;}", "precedence");
        Expect(42, "fnc main():int{if 7 /* comment */ !eq 8 {return 42;}return 0;}", "comments");
        Expect(42, "fnc main():int{/* 7 !eq 7 */if 7 !eq 8{return 42;}return 0;}", "inert operator in comment");
        Expect(42, "fnc main():int{if 7 != 8 {return 42;}return 0;}", "legacy != compatibility");

        foreach (var (expression, label) in new[] {
            ("7 ! eq 8", "split alias"),
            ("7 !eqvalue 8", "operator prefix of identifier"),
            ("7 neqvalue 8", "keyword prefix of identifier"),
            ("7 eqvalue 8", "equality prefix of identifier"),
            ("7 !eq true", "mixed bool/int"),
            ("7 eq true", "mixed bool/int eq"),
            ("7 !eq 7.0", "float without explicit typed numeric lowering"),
            ("7 !eq", "missing right operand"),
            ("7 !eq 8 !eq 9", "bool result used as int operand"),
        })
        {
            Reject("fnc main():int{if " + expression + "{return 1;}return 0;}", label);
        }

        // Shipping scalar mode must not silently accept this structured-only
        // operator and emit a fake integer-only executable.
        try { _ = OresParser.Parse("fnc main():int{return 7 !eq 8;}"); }
        catch (FormatException) { return; }
        throw new Exception("Shipping scalar parser wrongly admitted structured !eq");
    }
}
