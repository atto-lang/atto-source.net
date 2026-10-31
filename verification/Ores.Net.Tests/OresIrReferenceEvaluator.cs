using Ores.Net.Compiler;

internal static class OresIrReferenceEvaluator
{
    /// <summary>
    /// Managed verification-only interpreter for the i64 SSA subset.
    /// The released Native AOT compiler does NOT run generated guest code.
    /// Host OverflowException is normalized immediately to the Oreslang
    /// Throw/ArithmeticOverflowError ABI and never escapes this boundary.
    /// </summary>
    public static GuestI64EffectResult EvaluateEffect(VerifiedIrFunction verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        try
        {
            var values = new long[verified.Instructions.Count];
            foreach (var operation in verified.Instructions)
            {
                long left = operation.Left.HasValue ? values[operation.Left.Value] : 0;
                long right = operation.Right.HasValue ? values[operation.Right.Value] : 0;
                values[operation.ResultId] = operation.Opcode switch
                {
                    IrOpcode.ConstantI64 => operation.Constant!.Value,
                    IrOpcode.CheckedAdd => checked(left + right),
                    IrOpcode.CheckedSubtract => checked(left - right),
                    IrOpcode.CheckedMultiply => checked(left * right),
                    IrOpcode.CheckedDivide => DivideChecked(left, right),
                    IrOpcode.CheckedAbs => AbsChecked(left),
                    IrOpcode.SignedRemainderLiteral => right == -1 ? 0 : left % right,
                    _ => throw new FormatException("Unverified IR opcode")
                };
            }
            return GuestI64EffectResult.Return(values[verified.ReturnId]);
        }
        catch (OverflowException)
        {
            return GuestI64EffectResult.ArithmeticOverflow();
        }
    }

    public static long Evaluate(VerifiedIrFunction verified)
    {
        var result = EvaluateEffect(verified);
        if (result.IsReturn) return result.Value;
        throw GuestEffectTransportException.ArithmeticOverflow("OresIR evaluation");
    }

    private static long AbsChecked(long value)
    {
        if (value == long.MinValue)
            throw new OverflowException("Signed abs overflow");
        return value < 0 ? -value : value;
    }

    private static long DivideChecked(long left, long right)
    {
        if (right == 0) throw new DivideByZeroException();
        if (left == long.MinValue && right == -1)
            throw new OverflowException("Signed division overflow");
        return left / right;
    }
}
