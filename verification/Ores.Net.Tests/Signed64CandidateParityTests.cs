using Ores.Net.Compiler;
using Mono.Cecil;

// Executable *typed OresIR + emitted CIL interpreter* tests, not NativeAOT guest proof.
internal static class Signed64CandidateParityTests
{
    public static void Run()
    {
        var samples = new (string Name, string Source)[]
        {
        ("40-wide-i64-difference", "// 40: wide-i64-difference\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return 9007199254740993 - 9007199254740951; }\n"),
        ("41-min-i64-successor", "// 41: min-i64-successor\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return (-9223372036854775807 - 1) + 9223372036854775807 + 43; }\n"),
        ("42-max-i64-predecessor", "// 42: max-i64-predecessor\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return 9223372036854775807 - (9223372036854775807 - 42); }\n"),
        ("43-wide-checked-product", "// 43: wide-checked-product\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return 3037000499 * 3037000499 - 9223372030926248959; }\n"),
        ("44-double-negative-product", "// 44: double-negative-product\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return (1 - 43) * -1; }\n"),
        ("45-grouped-negation", "// 45: grouped-negation\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return -(-42); }\n"),
        ("46-named-bitxor", "// 46: named-bitxor\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return Integer.bitXor(40, 2); }\n"),
        ("47-mantissa-precision-product", "// 47: mantissa-precision-product\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return (9007199254740993 - 9007199254740992) * 42; }\n"),
        ("48-max-i64-delta", "// 48: max-i64-delta\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return 9223372036854775807 - 9223372036854775765; }\n"),
        ("49-signed-boundary-product", "// 49: signed-boundary-product\n// Candidate scalar exactness fixture; no compiler-backed execution claimed.\npub fnc main(): int { return (9223372036854775807 - 9223372036854775800) * 6; }\n")
        };
        if (samples.Length != 10)
            throw new Exception("Expected ten source-identical signed-i64 candidates");
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ores-net-parity-i64-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var (name, source) = samples[i];
                var ast = OresParser.Parse(source);
                var typed = OresIrLowering.Lower(ast);
                if (OresIrReferenceEvaluator.Evaluate(typed) != 42L)
                    throw new Exception("Source typed OresIR result mismatch: " + name);
                var artifact = System.IO.Path.Combine(path, "candidate-" + i + ".dll");
                CilEmitter.EmitLibrary(typed, artifact);
                using var assembly = AssemblyDefinition.ReadAssembly(artifact);
                var method = assembly.MainModule.Types
                    .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(m => m.Name == "Run");
                if (CilReferenceEvaluator.Evaluate(method) != 42L)
                    throw new Exception("Emitted CIL result mismatch: " + name);
            }
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
        Console.WriteLine("candidate i64: 10 source/typed-IR/emitted-CIL checks; NOT NativeAOT guest proof");
    }
}
