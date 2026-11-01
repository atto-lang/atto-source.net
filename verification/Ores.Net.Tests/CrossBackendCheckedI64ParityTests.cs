using Ores.Net.Compiler;
using Mono.Cecil;

static class CrossBackendCheckedI64ParityTests
{
    private const string Header = "// oreslang:checked-i64-v1\n";

    public static void Run()
    {
        RejectRemovedModulo();
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

    private static void RejectRemovedModulo()
    {
        // All backends must reject a binary '%' token at the source boundary,
        // not reinterpret it as a backend-specific scalar remainder. These
        // cases used to be accepted in the .NET bootstrap.
        foreach (string expression in new[]
        {
            "-7 % 3", "7 % -3", "-7 % -3", "8 % 3 * 2",
            "20 % 6 % 3", "-9223372036854775808 % -1",
            "-9223372036854775808 % -9223372036854775808",
            "9223372036854775807 % -9223372036854775808",
            "7 % 0", "7 % (1+1)", "7 % -(1+1)",
            "7 % -9223372036854775809", "7 %% 2", "7 % 2 % 0"
        })
        {
            try
            {
                _ = OresParser.Parse(Header + "pub fnc main():int{return " + expression + ";}");
            }
            catch (FormatException ex) when (
                ex.Message.Contains("Removed Oreslang operator '%'", StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("Removed modulo source was admitted: " + expression);
        }

        // Operators inside comments remain lexically invisible.
        if (OresParser.Parse(Header + "fnc main():int{return 7;} // 7 % 2") is not Literal { Value: 7 })
            throw new Exception("Modulo token in comment affected source semantics");
        if (OresParser.Parse(Header + "fnc main():int{return 7 /* % */ + 2;}") is not Binary { Operator: '+' })
            throw new Exception("Modulo token in block comment affected source semantics");

        // The public IR lowering path must reject a synthetic '%' AST too.
        bool syntheticRejected = false;
        try
        {
            _ = OresIrLowering.Lower(new Binary('%', new Literal(7), new Literal(3)));
        }
        catch (FormatException)
        {
            syntheticRejected = true;
        }
        if (!syntheticRejected)
            throw new Exception("Removed modulo synthetic AST escaped source admission");

        // Keep the legacy enum discriminant reserved rather than repurposed,
        // but every forged SignedRemainderLiteral must be denied even with
        // a constant nonzero divisor, before CIL/NativeAOT emission.
        foreach (long divisor in new long[] { 0, 3, -1 })
        {
            var operations = new[]
            {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 7),
                new IrInstruction(1, IrOpcode.ConstantI64, IrValueType.I64, Constant: divisor),
                new IrInstruction(2, IrOpcode.SignedRemainderLiteral, IrValueType.I64,
                    Left: 0, Right: 1)
            };
            try
            {
                _ = OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64, operations, 2));
            }
            catch (FormatException ex) when (
                ex.Message.Contains("Removed Oreslang operator '%'", StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("Forged removed modulo IR escaped verifier gate");
        }
    }
}
