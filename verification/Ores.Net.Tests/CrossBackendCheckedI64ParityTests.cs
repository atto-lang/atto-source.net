using Ores.Net.Compiler;
using Mono.Cecil;

static class CrossBackendCheckedI64ParityTests
{
    private const string Header = "// oreslang:checked-i64-v1\n";

    public static void Run()
    {
        PercentOperatorIsRejectedAndInternalRemainderRemainsBounded();
        foreach (var (name, expression, expected) in new (string, string, long)[]
        {
            ("precedence", "2 + 3 * 4", 14L),
            ("parentheses", "(2 + 3) * 4", 20L),
            ("negative", "-7 + 3", -4L),
            ("max", "9223372036854775807", long.MaxValue),
            ("min", "-9223372036854775808", long.MinValue),
            ("zero", "0", 0L),
            ("signed multiplication", "-7 * -6", 42L),
            ("math abs negative", "Math.abs(-42)", 42L),
            ("math abs zero", "Math.abs(0)", 0L),
            ("math abs positive", "Math.abs(42)", 42L),
            ("math abs maximum", "Math.abs(9223372036854775807)", long.MaxValue),
            ("math neg negative", "Math.neg(-42)", 42L),
            ("math neg zero", "Math.neg(0)", 0L),
            ("math neg positive", "Math.neg(42)", -42L),
            ("math nested", "Math.abs(Math.neg(42))", 42L),
        })
        {
            var ast = OresParser.Parse(Header + "define pub fnc main(): int { return " + expression + "; }");
            var ir = OresIrLowering.Lower(ast);
            var actual = OresIrReferenceEvaluator.Evaluate(ir);
            if (actual != expected)
                throw new Exception($"FAILED: cross-backend checked-i64 {name}: expected {expected}, got {actual}");
            if (name.StartsWith("math ", StringComparison.Ordinal))
                RequireEmittedCilResult(ir, expected, name);
        }

        foreach (var (name, expression) in new (string, string)[]
        {
            ("add overflow", "9223372036854775807 + 1"),
            ("subtract overflow", "-9223372036854775808 - 1"),
            ("multiply overflow", "9223372036854775807 * 2"),
            ("unary negate minimum", "-(-9223372036854775808)"),
            ("math abs minimum", "Math.abs(-9223372036854775808)"),
            ("math neg minimum", "Math.neg(-9223372036854775808)"),
        })
        {
            var ast = OresParser.Parse(
                Header + "define pub fnc main(): int { return " + expression + "; }");
            var ir = OresIrLowering.Lower(ast);
            RequireOverflowEffect(OresIrReferenceEvaluator.EvaluateEffect(ir),
                name + " OresIR");

            var folder = Path.Combine(Path.GetTempPath(), "oresnet-overflow-" +
                Guid.NewGuid().ToString("N"));
            var file = Path.Combine(folder, "OresGenerated.dll");
            try
            {
                CilEmitter.EmitLibrary(ir, file);
                using var assembly = AssemblyDefinition.ReadAssembly(file);
                var method = assembly.MainModule.Types
                    .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(f => f.Name == "Run");
                RequireOverflowEffect(CilReferenceEvaluator.EvaluateEffect(method),
                    name + " emitted CIL");
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
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

    private static void RequireEmittedCilResult(
        VerifiedIrFunction ir, long expected, string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "oresnet-math-cil-" +
            Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "OresGenerated.dll");
        try
        {
            CilEmitter.EmitLibrary(ir, file);
            using var assembly = AssemblyDefinition.ReadAssembly(file);
            var method = assembly.MainModule.Types
                .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            var actual = CilReferenceEvaluator.Evaluate(method);
            if (actual != expected)
                throw new Exception(
                    $"FAILED: {name} emitted CIL: expected {expected}, got {actual}");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static void RequireOverflowEffect(
        GuestI64EffectResult result, string where)
    {
        if (result.Status.Kind != GuestEffectKind.Throw ||
            result.Status.Code !=
                (uint)NumericGuestErrorCode.ArithmeticOverflowError)
            throw new Exception(
                $"FAILED: {where}: expected Throw/ArithmeticOverflowError(65537), " +
                $"got {result.Status.Kind}/{result.Status.Code}");
    }

    private static void PercentOperatorIsRejectedAndInternalRemainderRemainsBounded()
    {
        // Guest source syntax never admits '%'; future named Mod(a,b) lowering
        // may reuse the bounded internal remainder opcode, but no host/CIL
        // remainder expression is a substitute for the language API.
        foreach (string expression in new[]
        {
            "-7 % 3", "7 % -3", "-7 % -3", "8 % 3 * 2",
            "20 % 6 % 3", "-9223372036854775808 % -1",
            "-9223372036854775808 % -9223372036854775808",
            "9223372036854775807 % -9223372036854775808",
            "7 % 0", "7 % (1+1)", "7 % -(1+1)",
            "7 % -9223372036854775809"
        })
        {
            try
            {
                _ = OresParser.Parse(Header +
                    "pub fnc main():int{return " + expression + ";}");
                throw new Exception("Compiler admitted removed % source operator: " + expression);
            }
            catch (FormatException ex)
            {
                if (!ex.Message.Contains("The % operator is forbidden", StringComparison.Ordinal))
                    throw new Exception("Unexpected % rejection diagnostic: " + ex.Message);
            }
        }

        // Retain a bounded verifier/CIL regression for the internal operation,
        // which has no Oreslang '%' source spelling and no claimed Mod API yet.
        var internalIr = OresIrVerifier.Verify(new IrFunction(
            "main", IrValueType.I64, new[]
            {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: -7),
                new IrInstruction(1, IrOpcode.ConstantI64, IrValueType.I64, Constant: 3),
                new IrInstruction(2, IrOpcode.SignedRemainderLiteral, IrValueType.I64,
                    Left: 0, Right: 1)
            }, 2));
        if (OresIrReferenceEvaluator.Evaluate(internalIr) != -1)
            throw new Exception("Internal remainder reference drift");
        var folder = Path.Combine(Path.GetTempPath(), "oresnet-internal-rem-" +
            Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "OresGenerated.dll");
        try
        {
            CilEmitter.EmitLibrary(internalIr, file);
            using var assembly = AssemblyDefinition.ReadAssembly(file);
            var method = assembly.MainModule.Types
                .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            if (CilReferenceEvaluator.Evaluate(method) != -1)
                throw new Exception("Internal remainder CIL drift");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
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
