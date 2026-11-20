using Mono.Cecil;
using Ores.Net.Compiler;

// Shipping single-expression parser -> typed OresIR -> physical CIL metadata.
// This is NOT a NativeAOT executable receipt; the release lane must still
// publish on Windows/Linux/macOS and execute the generated native binary.
internal static class ScalarIdentityCastBootstrapTests
{
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("scalar-as-int: " + label);
    }

    public static void Run()
    {
        var positive = new (string Expression, long Expected)[]
        {
            ("1 as int", 1),
            ("-7 as int", -7),
            ("1 as int + 2 as int * 3 as int", 7),
            ("(2 + 3) as int * 4", 20),
            ("(-9223372036854775808) as int", long.MinValue),
            ("9223372036854775807 as int", long.MaxValue),
            ("((1 as int) as int) as int", 1),
            ("-(3 as int + 4 as int)", -7),
            ("(9 as int) - (2 as int)", 7),
            ("(3 as int) * (4 as int) + 5", 17)
        };
        var directory = Path.Combine(Path.GetTempPath(),
            "ores-net-scalar-cast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (int i = 0; i < positive.Length; i++)
            {
                var (expression, expected) = positive[i];
                var source = "pub fnc main():int{return " + expression + ";}";
                var ast = OresParser.Parse(source);
                var typed = OresIrLowering.Lower(ast);
                Require(OresIrReferenceEvaluator.Evaluate(typed) == expected,
                    "verified typed IR value " + expression);
                var output = Path.Combine(directory, "actual-" + i + ".dll");
                CilEmitter.EmitLibrary(typed, output);
                using var assembly = AssemblyDefinition.ReadAssembly(output);
                var entry = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(m => m.Name == "Run");
                Require(CilReferenceEvaluator.Evaluate(entry) == expected,
                    "emitted physical CIL result " + expression);
                // Identity lowering must not add hidden conversion opcodes or
                // change the arithmetic when the cast tokens are removed.
                var withoutCast = source.Replace(" as int", "", StringComparison.Ordinal);
                var direct = OresIrLowering.Lower(OresParser.Parse(withoutCast));
                Require(OresIrReferenceEvaluator.Evaluate(direct) == expected,
                    "identity has no conversion effects " + expression);
                // A true source identity must retain the *physical* CIL,
                // not just produce the same value under an IR interpreter.
                // The emitter is deterministic by contract and independent
                // of the target DLL path; bytes must match without casts.
                var directPath = Path.Combine(directory, "without-cast-" + i + ".dll");
                CilEmitter.EmitLibrary(direct, directPath);
                Require(File.ReadAllBytes(output).AsSpan().SequenceEqual(
                    File.ReadAllBytes(directPath)),
                    "identity cast unexpectedly changed emitted CIL " + expression);
            }
            // A checked identity proof must never mask eager i64 overflow,
            // in either the IR evaluator or the physically emitted CIL.
            var overflow = new[]
            {
                "9223372036854775807 as int + 1",
                "(-9223372036854775808) as int - 1",
                "(3037000500 as int) * (3037000500 as int)",
                "-((-9223372036854775808) as int)"
            };
            for (int i = 0; i < overflow.Length; i++)
            {
                var typed = OresIrLowering.Lower(OresParser.Parse(
                    "fnc main():int{return " + overflow[i] + ";}"));
                bool irTrapped = false;
                try { _ = OresIrReferenceEvaluator.Evaluate(typed); }
                catch (OverflowException) { irTrapped = true; }
                Require(irTrapped, "typed IR must not hide cast overflow " + overflow[i]);

                var output = Path.Combine(directory, "overflow-" + i + ".dll");
                CilEmitter.EmitLibrary(typed, output);
                using var assembly = AssemblyDefinition.ReadAssembly(output);
                var entry = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(m => m.Name == "Run");
                bool cilTrapped = false;
                try { _ = CilReferenceEvaluator.Evaluate(entry); }
                catch (OverflowException) { cilTrapped = true; }
                Require(cilTrapped, "physical CIL must not hide cast overflow " + overflow[i]);
            }

            int invalidIndex = 0;
            foreach (var expression in new[]
            {
                "1 as bool", "1 as string", "1 as? int", "1 as",
                "1 as float", "(1 as bool)", "1 as int as bool",
                "1 as int as? int", "1 as int + 2 as string",
                "1 as /* mixed */ bool"
            })
            {
                var invalidPath = Path.Combine(directory, "invalid-" + invalidIndex++ + ".dll");
                bool rejected = false;
                try
                {
                    var ast = OresParser.Parse("fnc main():int{return " + expression + ";}");
                    CilEmitter.EmitLibrary(OresIrLowering.Lower(ast), invalidPath);
                }
                catch (FormatException error) when (error.Message.Contains(
                    "Checked scalar cast requires exact int identity",
                    StringComparison.Ordinal)) { rejected = true; }
                Require(rejected, "wrong rejection for " + expression);
                Require(!File.Exists(invalidPath),
                    "invalid cast must not publish physical CIL: " + expression);
            }
            Require(OresIrReferenceEvaluator.Evaluate(OresIrLowering.Lower(
                    OresParser.Parse("fnc main():int{return 4;} // as bool"))) == 4,
                "comment must not be checked as executable syntax");
            Console.WriteLine("scalar-as-int: 10 typed-IR+CIL positives + byte-identical CIL, 4 paired overflow traps, 10 artifact-free diagnostic negatives, 1 comment control (no NativeAOT claim)");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
