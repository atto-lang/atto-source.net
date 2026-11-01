using Ores.Net.Compiler;
using Mono.Cecil;

static class CrossBackendCheckedI64ParityTests
{
    private const string Header = "// oreslang:checked-i64-v1\n";

    public static void Run()
    {
        SignedLiteralRemainder();
        OresllmScalarIntersectionCases();
        foreach (var (name, expression, expected) in new (string, string, long)[]
        {
            ("precedence", "2 + 3 * 4", 14L),
            ("parentheses", "(2 + 3) * 4", 20L),
            ("negative", "-7 + 3", -4L),
            ("max", "9223372036854775807", long.MaxValue),
            ("min", "-9223372036854775808", long.MinValue),
            ("zero", "0", 0L),
            ("signed multiplication", "-7 * -6", 42L),
        })
        {
            var ast = OresParser.Parse(Header + "define pub fnc main(): int { return " + expression + "; }");
            var ir = OresIrLowering.Lower(ast);
            var actual = OresIrReferenceEvaluator.Evaluate(ir);
            if (actual != expected)
                throw new Exception($"FAILED: cross-backend checked-i64 {name}: expected {expected}, got {actual}");
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
    private static void OresllmScalarIntersectionCases()
    {
        // Actual .NET parser -> OresIR -> CIL reference interpreter, NOT NativeAOT.
        // These 28 expressions match contracts/oresllm-parity/v1/scalar.json.
        foreach (var (expression, expected) in new (string, long)[]
        {
            ("0", 0L),
            ("42", 42L),
            ("2 + 3 * 4", 14L),
            ("(2 + 3) * 4", 20L),
            ("-7 + 3", -4L),
            ("-7 * -6", 42L),
            ("9223372036854775807", long.MaxValue),
            ("-9223372036854775808", long.MinValue),
            ("9223372036854775807 - 1", long.MaxValue - 1L),
            ("-9223372036854775808 + 1", long.MinValue + 1L),
            ("1 - 9", -8L),
            ("(-5) * (3 + 4)", -35L),
            ("(17 - 2) * 3", 45L),
            ("0 - (4 * 5)", -20L),
            ("1 + (2 * (3 + 4))", 15L),
            ("-(-5)", 5L),
            ("0 * 9223372036854775807", 0L),
        })
        {
            var source = Header + "pub fnc main():int{return " + expression + ";}";
            var ast = OresParser.Parse(source);
            var ir = OresIrLowering.Lower(ast);
            long fromIr = OresIrReferenceEvaluator.Evaluate(ir);
            if (fromIr != expected)
                throw new Exception($"OresLLM i64 managed IR mismatch: {expression}: {fromIr} vs {expected}");
            var folder = Path.Combine(Path.GetTempPath(), "oresnet-oresllm-" +
                Guid.NewGuid().ToString("N"));
            var file = Path.Combine(folder, "OresGenerated.dll");
            try
            {
                CilEmitter.EmitLibrary(ir, file);
                using var assembly = AssemblyDefinition.ReadAssembly(file);
                var method = assembly.MainModule.Types
                    .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(f => f.Name == "Run");
                long fromCil = CilReferenceEvaluator.Evaluate(method);
                if (fromCil != expected)
                    throw new Exception($"OresLLM i64 CIL mismatch: {expression}: {fromCil} vs {expected}");
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        foreach (string expression in new[]
        {
            "9223372036854775807 + 1",
            "-9223372036854775808 - 1",
            "9223372036854775807 * 2",
            "-(-9223372036854775808)",
            "(9223372036854775807 + 1) * 0",
            "0 - (9223372036854775807 + 1)",
        })
        {
            var ast = OresParser.Parse(Header + "pub fnc main():int{return " + expression + ";}");
            var ir = OresIrLowering.Lower(ast);
            try { _ = OresIrReferenceEvaluator.Evaluate(ir); }
            catch (OverflowException) { continue; }
            throw new Exception("OresLLM checked overflow admitted: " + expression);
        }

        foreach (string expression in new[]
        {
            "unknown_value", "7 / 2", "5 ^ 3", "5 ^^ 3", "1__0"
        })
        {
            try { _ = OresParser.Parse(Header + "pub fnc main():int{return " + expression + ";}"); }
            catch (FormatException) { continue; }
            throw new Exception("OresLLM unsupported expression admitted: " + expression);
        }
    }
