namespace Ores.Net.Compiler;

public static partial class OresParser
{
    /// <summary>Explicit closed unsigned source profile using the existing lexer,
    /// scalar expression grammar and independently verified SSA. No signed fallback.</summary>
    public static VerifiedIrFunction LowerUnsigned(string source)
    {
        var tokens = Tokenize(source);
        // Bound work before building expression nodes; lexer has its own byte/token cap.
        if (tokens.Count > OresIrVerifier.MaxInstructions)
            throw new FormatException("Unsigned source token budget exceeded");
        var root = new Reader(tokens, DotnetHostImportPolicy.DenyAll, source, null, unsignedScalar: true).ParseProgram();
        var pending = new Stack<(Expression Node, bool Finish)>();
        var values = new Stack<int>();
        var ops = new List<IrInstruction>();
        pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            if (item.Node is Binary binary && !item.Finish)
            {
                pending.Push((binary, true));
                pending.Push((binary.Right, false));
                pending.Push((binary.Left, false));
                continue;
            }
            if (ops.Count == OresIrVerifier.MaxInstructions)
                throw new FormatException("Unsigned source IR budget exceeded");
            int id = ops.Count;
            if (item.Node is UnsignedLiteral literal)
                ops.Add(new IrInstruction(id, IrOpcode.ConstantU64, IrValueType.U64) { UnsignedConstant = literal.Value });
            else if (item.Node is Binary operation && item.Finish)
            {
                int right = values.Pop(), left = values.Pop();
                var opcode = operation.Operator switch
                {
                    '+' => IrOpcode.CheckedAddU64,
                    '-' => IrOpcode.CheckedSubtractU64,
                    '*' => IrOpcode.CheckedMultiplyU64,
                    '/' => IrOpcode.CheckedDivideU64,
                    _ => throw new FormatException("Unsupported unsigned operation")
                };
                ops.Add(new IrInstruction(id, opcode, IrValueType.U64, Left: left, Right: right));
            }
            else throw new FormatException("Unsupported unsigned AST node");
            values.Push(id);
        }
        if (values.Count != 1) throw new FormatException("Malformed unsigned source expression");
        return OresIrVerifier.VerifyUnsigned(new IrFunction("main", IrValueType.U64, ops, values.Pop()));
    }
}
