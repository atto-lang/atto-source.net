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
        "939b28fccd280d8a3690ebd4d874f263a9ddfe0c87d420d93b9bd684ffe34074",
        "873112bfda242442d29a8b2ba9f74bc494db44e4e66ac51484ad886786bb525b",
        "2ee8ae2204d348d177a995e1086543fbc9fcff3d66c80827d40468f2953a9157",
        "171928949cd43a61f2072f311f94e27f697968848d07b1b90affc9e061d3c41e",
        "61cf977237ae559e2a759efde340e31b62f95a244702b94f8641ff24931ebecf",
        "a1da8a0e27b1a72a6371447868cd75d192f3b0da18122c53ac48cfc848f2320f",
        "ea84b5d71bbb8a7356fe87b70c04a0518da28c53fb2e271d2b6429bbe6a44581",
        "ba3cd5f0d90f7b6f9c4776561f5b7149dfd2c7d420492fe98df3f249a3fb9922",
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
            ("wave/left associative product", "2 * 3 * 7", 42L),
            ("wave/paired negative differences", "(0 - 7) * (0 - 6)", 42L),
            ("wave/grouped zero difference", "(21 + 21) + (0 - 0)", 42L),
            ("wave/six times seven", "6 * 7", 42L),
            ("wave/parenthesized negative product", "(-7) * (-6)", 42L),
            ("wave/max self cancellation", "9223372036854775807 - 9223372036854775807 + 42", 42L),
            ("wave/min self cancellation", "-9223372036854775808 - (-9223372036854775808) + 42", 42L),
            ("wave/unit multiplication group", "(100 - 58) * (0 + 1)", 42L),
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
                    "wave/left associative product" => 50,
                    "wave/paired negative differences" => 51,
                    "wave/grouped zero difference" => 52,
                    "wave/six times seven" => 53,
                    "wave/parenthesized negative product" => 54,
                    "wave/max self cancellation" => 55,
                    "wave/min self cancellation" => 56,
                    "wave/unit multiplication group" => 57,
                    _ => throw new InvalidOperationException("unknown wave scalar case")
                };
                source = "// " + id +
                    ": candidate source-identical checked-i64 scalar program.\n" +
                    "// Expected integer result 42; guest execution on each backend is unverified.\n" +
                    "pub fnc main(): int { return " + expression + "; }\n";
                var actualSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
                    .ToLowerInvariant();
                if (actualSha != WaveSourceSha256[id >= 50 ? id - 42 : id - 34])
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
