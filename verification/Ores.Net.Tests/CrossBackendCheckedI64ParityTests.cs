using Ores.Net.Compiler;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

static class CrossBackendCheckedI64ParityTests
{
    private const string Header = "// oreslang:checked-i64-v1\n";
    private static readonly string[] WaveSourceSha256 =
    {
        "2d186d0c400c23c6fcdf1df963580351eacea82250097a1cd083a1cc308b40f5",
        "73e5fc703df0b0d3af6898c156ff105ec28e8dfc639f2a02ccca0f1cd3c69dd8",
        "2ca62fbe11d05cb219a4be255669842994c279e0e0a5208fbc3d3a0e4c047d29",
        "efb740b31d72bfdc1eecc8a7eb87a7b84c8961f444fbbcc08a142ca54cf7c97b",
        "391467d7baf7b84b339a63e55be2395d9a8a4d92b5719f249df428b34bac7ccd",
        "d1fa2dc614cd61c442ab1bfa5683fac50cc9d72139e0dcdf0a67b3610d427bd6",
        "0322dc2a7d48fed12d917aaaf2a50c025b77402a2b21fdbb87224fece59b44b0",
        "3483cb15d4b2f2c24f90ba65e55e886b59eaea2190aa9c18f8ed10badba04e76",
    };


    public static void Run()
    {
        SignedLiteralRemainder();
        foreach (var (name, expression, expected) in new (string, string, long)[]
        {
            ("precedence", "2 + 3 * 4", 14L),
            ("parentheses", "(2 + 3) * 4", 20L),
            ("negative", "-7 + 3", -4L),
            ("max", "9223372036854775807", long.MaxValue),
            ("min", "-9223372036854775808", long.MinValue),
            ("zero", "0", 0L),
            ("signed multiplication", "-7 * -6", 42L),
            ("wave/grouped precedence", "(2 + 3) * 8 + 2", 42L),
            ("wave/double negation", "-(-42)", 42L),
            ("wave/left associative subtraction", "(100 - 16) - 42", 42L),
            ("wave/signed multiplication", "-7 * -6", 42L),
            ("wave/negative multiplication chain", "(7 - 14) * -6", 42L),
            ("wave/i64 max minus large", "9223372036854775807 - 9223372036854775765", 42L),
            ("wave/i64 min plus max", "-9223372036854775808 + 9223372036854775807 + 43", 42L),
            ("wave/nested grouping", "2 * (3 + 4) * 3", 42L),
        })
        {
            string source;
            if (name.StartsWith("wave/", StringComparison.Ordinal))
            {
                var id = name switch
                {
                    "wave/grouped precedence" => 34,
                    "wave/double negation" => 35,
                    "wave/left associative subtraction" => 36,
                    "wave/signed multiplication" => 37,
                    "wave/negative multiplication chain" => 38,
                    "wave/i64 max minus large" => 39,
                    "wave/i64 min plus max" => 40,
                    "wave/nested grouping" => 41,
                    _ => throw new InvalidOperationException("unknown wave scalar case")
                };
                source = "// " + id +
                    ": candidate source-identical checked-i64 scalar program.\n" +
                    "// Expected integer result 42; guest execution on each backend is unverified.\n" +
                    "pub fnc main(): int { return " + expression + "; }\n";
                var actualSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
                    .ToLowerInvariant();
                if (actualSha != WaveSourceSha256[id - 34])
                    throw new Exception("FAIL: Oreslang source bytes differ from shared parity case " + id);
            }
            else
            {
                source = Header + "define pub fnc main(): int { return " + expression + "; }";
            }
            var ast = OresParser.Parse(source);
            var ir = OresIrLowering.Lower(ast);
            var actual = OresIrReferenceEvaluator.Evaluate(ir);
            if (actual != expected)
                throw new Exception($"FAILED: cross-backend checked-i64 {name}: expected {expected}, got {actual}");
            // Independently verify emitted CIL as well as typed OresIR.
            // This remains a CIL interpreter receipt, not NativeAOT execution.
            if (name.StartsWith("wave/", StringComparison.Ordinal))
            {
                var folder = Path.Combine(Path.GetTempPath(),
                    "oresnet-wave-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var binary = Path.Combine(folder, "OresGenerated.dll");
                    CilEmitter.EmitLibrary(ir, binary);
                    using var assembly = AssemblyDefinition.ReadAssembly(binary);
                    var method = assembly.MainModule.Types
                        .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                        .Methods.Single(m => m.Name == "Run");
                    var actualCil = CilReferenceEvaluator.Evaluate(method);
                    if (actualCil != expected)
                        throw new Exception($"FAILED: emitted CIL {name}: expected {expected}, got {actualCil}");
                }
                finally
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                }
            }

        }

        foreach (var (name, expression) in new (string, string)[]
        {
            ("add overflow", "9223372036854775807 + 1"),
            ("subtract overflow", "-9223372036854775808 - 1"),
            ("multiply overflow", "9223372036854775807 * 2"),
            ("unary negate minimum", "-(-9223372036854775808)"),
        })
        {
            try
            {
                var ast = OresParser.Parse(Header + "define pub fnc main(): int { return " + expression + "; }");
                var ir = OresIrLowering.Lower(ast);
                _ = OresIrReferenceEvaluator.Evaluate(ir);
            }
            catch (OverflowException)
            {
                continue;
            }
            throw new Exception("FAILED: cross-backend checked-i64 overflow did not trap: " + name);
        }

        try
        {
            _ = OresParser.Parse(Header + "define pub fnc main(): int { return 7 / 2; }");
        }
        catch (FormatException ex) when (ex.Message.Contains("float result lowering", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("FAILED: int/int division was admitted without float lowering");
    }

    private static void SignedLiteralRemainder()
    {
        // Exercise parser -> verified OresIR -> emitted CIL bytes -> an
        // independent bounded CIL interpreter (not a Native AOT execution).
        foreach (var (expression, expected) in new (string, long)[]
        {
            ("-7 % 3", -1),
            ("7 % -3", 1),
            ("-7 % -3", -1),
            ("8 % 3 * 2", 4),
            ("20 % 6 % 3", 2),
            ("-9223372036854775808 % -1", 0),
            ("-9223372036854775808 % -9223372036854775808", 0),
            ("9223372036854775807 % -9223372036854775808", long.MaxValue)
        })
        {
            var ast = OresParser.Parse(Header + "pub fnc main():int{return " + expression + ";}");
            var verified = OresIrLowering.Lower(ast);
            if (OresIrReferenceEvaluator.Evaluate(verified) != expected)
                throw new Exception("Remainder managed reference mismatch: " + expression);
            var folder = Path.Combine(Path.GetTempPath(), "oresnet-remainder-" +
                Guid.NewGuid().ToString("N"));
            var file = Path.Combine(folder, "OresGenerated.dll");
            try
            {
                CilEmitter.EmitLibrary(verified, file);
                using var assembly = AssemblyDefinition.ReadAssembly(file);
                var method = assembly.MainModule.Types
                    .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(f => f.Name == "Run");
                long actual = CilReferenceEvaluator.Evaluate(method);
                if (actual != expected)
                    throw new Exception($"Remainder CIL mismatch: {expression}: {actual} vs {expected}");
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        foreach (string expression in new[]
        {
            "7 % 0", "7 % (1+1)", "7 % -(1+1)",
            "7 % -9223372036854775809"
        })
        {
            try
            {
                _ = OresParser.Parse(Header + "pub fnc main():int{return " + expression + ";}");
            }
            catch (FormatException)
            {
                continue;
            }
            throw new Exception("Compiler admitted unsupported remainder: " + expression);
        }

        // The IR is untrusted; a source parser's admission does not grant
        // externally constructed IR the right to use arbitrary divisors.
        foreach (long divisor in new long[] { 0, -1 })
        {
            var operations = new[]
            {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 7),
                divisor == 0
                    ? new IrInstruction(1, IrOpcode.ConstantI64, IrValueType.I64, Constant: 0)
                    : new IrInstruction(1, IrOpcode.CheckedSubtract, IrValueType.I64,
                        Left: 0, Right: 0),
                new IrInstruction(2, IrOpcode.SignedRemainderLiteral, IrValueType.I64,
                    Left: 0, Right: 1)
            };
            try
            {
                _ = OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64, operations, 2));
            }
            catch (FormatException)
            {
                continue;
            }
            throw new Exception("Untrusted remainder IR escaped literal divisor gate");
        }
    }
}
