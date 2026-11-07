using Ores.Net.Compiler;
using Mono.Cecil;

static class CrossBackendCheckedI64ParityTests
{
    private const string Header = "// oreslang:checked-i64-v1\n";

    public static void Run()
    {
        RemovedPercentSourceOperator();
        ReservedSourceOperatorsAndIr();
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

    private static void ReservedSourceOperatorsAndIr()
    {
        // Actual shipping scalar/structured lexers must agree before they
        // construct any typed SSA or CIL. Include both caret spellings.
        var denied = new (string Expr, string Diagnostic)[]
        {
            ("7%3", "noncanonical '%' source operator"),
            ("-7%-3", "noncanonical '%' source operator"),
            ("(8%3)*2", "noncanonical '%' source operator"),
            ("7 % (1+1)", "noncanonical '%' source operator"),
            ("-9223372036854775808%-1", "noncanonical '%' source operator"),
            ("7%0", "noncanonical '%' source operator"),
            ("7^3", "Reserved AttoLang operator '^'"),
            ("7 ^ 3", "Reserved AttoLang operator '^'"),
            ("-7^3", "Reserved AttoLang operator '^'"),
            ("7^(-3)", "Reserved AttoLang operator '^'"),
            ("(7^3)", "Reserved AttoLang operator '^'"),
            ("7^3^1", "Reserved AttoLang operator '^'"),
            ("7^ ^3", "Reserved AttoLang operator '^'"),
            ("9223372036854775807^1", "Reserved AttoLang operator '^'"),
            ("7^^3", "Reserved AttoLang operator '^^'"),
            ("7 ^^ 3", "Reserved AttoLang operator '^^'"),
            ("-7^^3", "Reserved AttoLang operator '^^'"),
            ("7^^(1+2)", "Reserved AttoLang operator '^^'"),
            ("7^^3^^1", "Reserved AttoLang operator '^^'"),
            ("-9223372036854775808^^-1", "Reserved AttoLang operator '^^'"),
            ("(7^^3)", "Reserved AttoLang operator '^^'"),
            ("7 ^^ -3", "Reserved AttoLang operator '^^'"),
            ("20%6%3", "noncanonical '%' source operator"),
            ("7%-(1+1)", "noncanonical '%' source operator")
        };
        foreach (var (expression, expected) in denied)
        {
            string source = "fnc main():int{return " + expression + ";}";
            foreach (var (frontend, parse) in new (string, Action)[]
            {
                ("scalar", () => { _ = OresParser.Parse(source); }),
                ("structured", () => { _ = OresStructuredParser.Parse(source); })
            })
            {
                try { parse(); }
                catch (FormatException error) when
                    (error.Message.Contains(expected, StringComparison.Ordinal) &&
                     error.Message.Contains("offset", StringComparison.Ordinal))
                { continue; }
                throw new Exception($"FAILED: {frontend} admitted reserved operator in {expression}");
            }
        }

        // A pre-lexical substring gate would reject these legal sources.
        foreach (string source in new[]
        {
            "fnc main():int{return 40+2;} // % ^ ^^\n",
            "fnc main():int{return 40 /* % /* ^ ^^ */ % */ +2;}",
            "fnc main():int{return 40+2;} // ^ ^^\u2028"
        })
        {
            if (OresIrReferenceEvaluator.Evaluate(OresIrLowering.Lower(OresParser.Parse(source))) != 42)
                throw new Exception("FAILED: reserved-operator scalar comment control");
            if (OresStructuredParser.Parse(source).Body.Count != 1)
                throw new Exception("FAILED: reserved-operator structured comment control");
        }

        foreach (char op in new[] { '%', '^' })
        {
            try { _ = OresIrLowering.Lower(new Binary(op, new Literal(7), new Literal(3))); }
            catch (FormatException) { continue; }
            throw new Exception($"FAILED: forged AST {op} reached verified OresIR");
        }

        // The old verifier accepted a nonzero literal RHS. Reject that as
        // well as zero and -1 before any CIL/native code can be emitted.
        foreach (long divisor in new long[] { 0, 3, -1 })
        {
            var instructions = new[]
            {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 7),
                new IrInstruction(1, IrOpcode.ConstantI64, IrValueType.I64, Constant: divisor),
                new IrInstruction(2, IrOpcode.SignedRemainderLiteral, IrValueType.I64, Left: 0, Right: 1)
            };
            try { _ = OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64, instructions, 2)); }
            catch (FormatException error) when
                (error.Message.Contains("Removed Oreslang operator '%'", StringComparison.Ordinal))
            { continue; }
            throw new Exception("FAILED: retired IR opcode accepted divisor " + divisor);
        }
    }

    private static void RemovedPercentSourceOperator()
    {
        // The canonical Oreslang grammar has no percent operator. Reject
        // every historic spelling *before* producing OresIR or CIL.
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
                _ = OresParser.Parse(Header + "pub fnc main():int{return " + expression + ";}");
            }
            catch (FormatException ex) when (ex.Message.Contains("noncanonical '%' source operator", StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("Noncanonical percent source was admitted: " + expression);
        }
        var positive = OresIrLowering.Lower(OresParser.Parse(
            Header + "pub fnc main():int{return 7*6;}"));
        if (OresIrReferenceEvaluator.Evaluate(positive) != 42)
            throw new Exception("Canonical multiply baseline regressed");
        // '%' remains valid text inside lexical comments, never as an
        // executable operator. Guard against a crude pre-lexical filter.
        var commentProgram = OresIrLowering.Lower(OresParser.Parse(
            Header + "pub fnc main():int{return 42;} // % comment only"));
        if (OresIrReferenceEvaluator.Evaluate(commentProgram) != 42)
            throw new Exception("Percent in a comment corrupted parsing");

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
