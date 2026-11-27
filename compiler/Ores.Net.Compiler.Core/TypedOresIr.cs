namespace Ores.Net.Compiler;

/// <summary>Explicit value types in the first typed OresIR slice.
/// Future types must be added deliberately, not silently coerced into i64.</summary>
public enum IrValueType
{
    I64,
    Bool,
    // Method result only. Void must not be an SSA value, local, or parameter.
    Void,
    // Closed structured Option<int>. Locals and parameters use scalar pairs;
    // verified helper results use a private CIL value type at the call boundary.
    OptionI64,
    // Source-only bounded, immutable Array<int>: its elements are distinct
    // verified i64 SSA values, not CLR arrays or native guest pointers.
    FixedArrayI64,
    GuestError,
    ResultI64,
    ResultOptionI64,
    GuestText,
    ResultI64Text,
    ResultI64TextOrError,
    U64
}

public enum IrOpcode
{
    ConstantI64,
    CheckedAdd,
    CheckedSubtract,
    CheckedMultiply,
    CheckedDivide,
    BitXor,
    SignedRemainderLiteral,
    // Exact framework API signature: System.Math.Abs(System.Int64) -> System.Int64.
    DotnetMathAbsI64,
    DotnetMathMaxI64,
    // Named signed-64 Integer.rem/mod/pow: verified static calls into
    // Ores.Net.Runtime.StrictSigned64Arithmetic (appended; ordinals above stay stable).
    StrictRem,
    StrictMod,
    StrictPow,
    ConstantU64,
    CheckedAddU64,
    CheckedSubtractU64,
    CheckedMultiplyU64,
    CheckedDivideU64
}

/// <summary>
/// A value definition has exactly one result ID. Operands refer to previously
/// defined IDs; there are no mutable phi/branch edges in the bootstrap subset.
/// </summary>
public sealed record IrInstruction(
    int ResultId,
    IrOpcode Opcode,
    IrValueType Type,
    int? Left = null,
    int? Right = null,
    long? Constant = null)
{
    public ulong? UnsignedConstant { get; init; }
}

/// <summary>Untrusted compiler IR. Never lower an IrFunction without verification.</summary>
public sealed record IrFunction(
    string Name,
    IrValueType ResultType,
    IReadOnlyList<IrInstruction> Instructions,
    int ReturnId);

/// <summary>
/// Frozen validated IR, safe from caller mutations to an original instruction
/// list. Construction is only permitted through OresIrVerifier.Verify.
/// </summary>
public sealed class VerifiedIrFunction
{
    internal VerifiedIrFunction(
        string name, IrValueType resultType,
        IReadOnlyList<IrInstruction> instructions, int returnId)
    {
        Name = name;
        ResultType = resultType;
        Instructions = instructions;
        ReturnId = returnId;
    }

    public string Name { get; }
    public IrValueType ResultType { get; }
    public IReadOnlyList<IrInstruction> Instructions { get; }
    public int ReturnId { get; }
}

public static class OresIrVerifier
{
    // .NET CIL backend materializes one i64 local per SSA result.
    // Match its hard bound so 'check' cannot accept a program 'compile' rejects.
    public const int MaxInstructions = 16_384;

    public static VerifiedIrFunction Verify(IrFunction function,
        DotnetHostImportPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        policy ??= DotnetHostImportPolicy.DenyAll;
        if (string.IsNullOrWhiteSpace(function.Name) || function.Name.Length > 128 ||
            !IsIdentifier(function.Name))
            throw new FormatException("Invalid IR function name");
        if (function.ResultType != IrValueType.I64)
            throw new FormatException("Bootstrap IR only supports i64 function results");

        var source = function.Instructions ??
            throw new FormatException("IR instructions must not be null");
        if (source.Count == 0 || source.Count > MaxInstructions)
            throw new FormatException("IR instruction budget exceeded");

        // Snapshot the input list. A caller must not be able to mutate an IR
        // program after verification and before code generation.
        var verified = new IrInstruction[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            var instruction = source[i] ??
                throw new FormatException("Null IR instruction");
            if (instruction.ResultId != i)
                throw new FormatException("IR must use unique contiguous SSA result IDs");
            if (instruction.Type != IrValueType.I64 || instruction.UnsignedConstant.HasValue)
                throw new FormatException("Unsupported IR value type");

            if (instruction.Opcode == IrOpcode.ConstantI64)
            {
                if (!instruction.Constant.HasValue ||
                    instruction.Left.HasValue || instruction.Right.HasValue)
                    throw new FormatException("Malformed IR constant");
            }
            else if (instruction.Opcode == IrOpcode.DotnetMathAbsI64)
            {
                policy.RequireMathI64();
                // A host call is not arbitrary reflection or an opaque value.
                // Type, arity, exact method and SSA ownership are verified.
                if (instruction.Constant.HasValue || instruction.Right.HasValue ||
                    !instruction.Left.HasValue || instruction.Left.Value < 0 ||
                    instruction.Left.Value >= i ||
                    verified[instruction.Left.Value].Type != IrValueType.I64)
                    throw new FormatException("Malformed typed CLR Abs(i64) instruction");
            }
            else if (instruction.Opcode == IrOpcode.DotnetMathMaxI64)
            {
                policy.RequireMathI64();
                if (instruction.Constant.HasValue || !instruction.Left.HasValue ||
                    !instruction.Right.HasValue ||
                    instruction.Left.Value < 0 || instruction.Left.Value >= i ||
                    instruction.Right.Value < 0 || instruction.Right.Value >= i ||
                    verified[instruction.Left.Value].Type != IrValueType.I64 ||
                    verified[instruction.Right.Value].Type != IrValueType.I64)
                    throw new FormatException("Malformed typed CLR Max(i64,i64) instruction");
            }
            else
            {
                // Keep the retired opcode's numeric identity stable, but
                // never accept it from externally constructed or stale IR.
                if (instruction.Opcode == IrOpcode.SignedRemainderLiteral)
                    throw new FormatException("Removed Oreslang operator '%' in verified IR");
                if (instruction.Opcode is not (IrOpcode.CheckedAdd or
                    IrOpcode.CheckedSubtract or IrOpcode.CheckedMultiply or
                    IrOpcode.CheckedDivide or IrOpcode.BitXor or
                    IrOpcode.StrictRem or IrOpcode.StrictMod or IrOpcode.StrictPow))
                    throw new FormatException("Unknown IR opcode");

                if (instruction.Constant.HasValue ||
                    !instruction.Left.HasValue || !instruction.Right.HasValue)
                    throw new FormatException("Malformed IR arithmetic operation");
                if (instruction.Left.Value < 0 || instruction.Left.Value >= i ||
                    instruction.Right.Value < 0 || instruction.Right.Value >= i)
                    throw new FormatException("IR operand must be a prior SSA definition");
                if (verified[instruction.Left.Value].Type != IrValueType.I64 ||
                    verified[instruction.Right.Value].Type != IrValueType.I64)
                    throw new FormatException("IR arithmetic requires i64 operands");
            }
            verified[i] = instruction;
        }

        if (function.ReturnId < 0 || function.ReturnId >= verified.Length ||
            verified[function.ReturnId].Type != function.ResultType)
            throw new FormatException("IR return references missing or incorrect type");

        return new VerifiedIrFunction(function.Name, function.ResultType,
            Array.AsReadOnly(verified), function.ReturnId);
    }

    // Explicit opt-in until source lowering and NativeAOT ABI are integrated.
    // Existing Verify() remains signed-only; mixed signedness is never inferred.
    public static VerifiedIrFunction VerifyUnsigned(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (string.IsNullOrWhiteSpace(function.Name) || function.Name.Length > 128 ||
            !IsIdentifier(function.Name) || function.ResultType != IrValueType.U64)
            throw new FormatException("Unsigned IR requires a valid u64 function");
        var source = function.Instructions ?? throw new FormatException("Missing unsigned IR instructions");
        if (source.Count == 0 || source.Count > MaxInstructions)
            throw new FormatException("IR instruction budget exceeded");
        var verified = new IrInstruction[source.Count];
        for (int i = 0; i < verified.Length; i++)
        {
            var op = source[i] ?? throw new FormatException("Null unsigned IR instruction");
            if (op.ResultId != i || op.Type != IrValueType.U64 || op.Constant.HasValue)
                throw new FormatException("Unsigned IR requires contiguous typed u64 SSA");
            if (op.Opcode == IrOpcode.ConstantU64)
            {
                if (!op.UnsignedConstant.HasValue || op.Left.HasValue || op.Right.HasValue)
                    throw new FormatException("Malformed u64 constant");
            }
            else
            {
                if (op.Opcode is not (IrOpcode.CheckedAddU64 or IrOpcode.CheckedSubtractU64 or
                    IrOpcode.CheckedMultiplyU64 or IrOpcode.CheckedDivideU64) ||
                    op.UnsignedConstant.HasValue || !op.Left.HasValue || !op.Right.HasValue ||
                    op.Left.Value < 0 || op.Left.Value >= i || op.Right.Value < 0 || op.Right.Value >= i)
                    throw new FormatException("Malformed or unsupported checked u64 operation");
            }
            verified[i] = op;
        }
        if (function.ReturnId < 0 || function.ReturnId >= verified.Length)
            throw new FormatException("Unsigned IR return references missing SSA value");
        return new VerifiedIrFunction(function.Name, function.ResultType,
            Array.AsReadOnly(verified), function.ReturnId);
    }

    private static bool IsIdentifier(string value)
    {
        if (!IsAsciiAlpha(value[0]) && value[0] != '_') return false;
        for (int i = 1; i < value.Length; i++)
            if (!IsAsciiAlpha(value[i]) && value[i] != '_' &&
                value[i] is not (>= '0' and <= '9')) return false;
        return true;
    }

    private static bool IsAsciiAlpha(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
}

/// <summary>
/// Translates the existing arithmetic parser AST into immutable verified SSA.
/// This is deliberately an independent stage, not yet connected to CIL emission:
/// current release bootstrap behavior must not silently change.
/// </summary>
public static class OresIrLowering
{
    private const int MaxAstNodes = 100_000;
    private const int MaxPendingValues = 256;
    private readonly record struct Pending(Expression Node, bool Finish, bool Cleanup = false);

    public static VerifiedIrFunction Lower(Expression root, string name = "main",
        DotnetHostImportPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var pending = new Stack<Pending>();
        var results = new Stack<int>();
        var instructions = new List<IrInstruction>();
        pending.Push(new Pending(root, false));
        int visited = 0;
        var patternValues = new Dictionary<int,int>();

        int Constant(long value)
        {
            if (instructions.Count >= OresIrVerifier.MaxInstructions)
                throw new FormatException("IR instruction budget exceeded");
            int id = instructions.Count;
            instructions.Add(new IrInstruction(id, IrOpcode.ConstantI64,
                IrValueType.I64, Constant: value));
            return id;
        }

        int EmitBinary(IrOpcode opcode, int left, int right)
        {
            if (instructions.Count >= OresIrVerifier.MaxInstructions)
                throw new FormatException("IR instruction budget exceeded");
            int id = instructions.Count;
            instructions.Add(new IrInstruction(id, opcode,
                IrValueType.I64, Left: left, Right: right));
            return id;
        }

        int EmitDotnetAbs(int value)
        {
            if (instructions.Count >= OresIrVerifier.MaxInstructions)
                throw new FormatException("IR instruction budget exceeded");
            int id = instructions.Count;
            instructions.Add(new IrInstruction(id, IrOpcode.DotnetMathAbsI64,
                IrValueType.I64, Left: value));
            return id;
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var node = current.Node ??
                throw new FormatException("Null AST node");
            if (current.Cleanup)
            {
                if (node is not ScalarPatternApply done)
                    throw new FormatException("Invalid scalar pattern cleanup");
                foreach (int slot in done.Slots)
                    if (slot >= 0 && !patternValues.Remove(slot))
                        throw new FormatException("Missing scalar pattern slot");
                continue;
            }
            if (current.Finish)
            {
                switch (node)
                {
                    case ScalarPatternApply pattern:
                    {
                        if (pattern.Roots.Count != pattern.Slots.Count || pattern.Roots.Count > 8 ||
                            results.Count < pattern.Roots.Count)
                            throw new FormatException("Invalid scalar pattern arity");
                        // Root instructions have already executed in source order.
                        // Their SSA IDs, not AST expressions, are bound into the body.
                        for (int i=pattern.Slots.Count-1;i>=0;i--)
                        {
                            int value=results.Pop();
                            int slot=pattern.Slots[i];
                            if (slot < -1 || (slot >= 0 && !patternValues.TryAdd(slot,value)))
                                throw new FormatException("Invalid or duplicate scalar pattern slot");
                        }
                        pending.Push(new Pending(pattern, false, Cleanup: true));
                        pending.Push(new Pending(pattern.Body, false));
                        break;
                    }
                    case Unary { Operator: '+' }:
                        break;
                    case Unary { Operator: '-' }:
                    {
                        if (results.Count < 1)
                            throw new FormatException("IR lowering operand underflow");
                        int operand = results.Pop();
                        results.Push(EmitBinary(IrOpcode.CheckedSubtract,
                            Constant(0), operand));
                        break;
                    }
                    case DotnetMathAbsI64:
                    {
                        if (results.Count < 1)
                            throw new FormatException("CLR intrinsic operand underflow");
                        results.Push(EmitDotnetAbs(results.Pop()));
                        break;
                    }
                    case EntryScalarProjection projection:
                    {
                        if (results.Count < 2)
                            throw new FormatException("IR lowering Entry operand underflow");
                        int right = results.Pop();
                        int left = results.Pop();
                        // Every SSA instruction is executed in order, including
                        // the unused value; selecting an earlier ID is legal.
                        results.Push(projection.SelectKey ? left : right);
                        break;
                    }
                    case DotnetMathMaxI64:
                    {
                        if (results.Count < 2)
                            throw new FormatException("CLR Max intrinsic operand underflow");
                        int right = results.Pop();
                        int left = results.Pop();
                        results.Push(EmitBinary(IrOpcode.DotnetMathMaxI64, left, right));
                        break;
                    }
                    case NamedBitXor:
                    {
                        if (results.Count < 2)
                            throw new FormatException("IR lowering bitXor operand underflow");
                        int right = results.Pop();
                        int left = results.Pop();
                        results.Push(EmitBinary(IrOpcode.BitXor, left, right));
                        break;
                    }
                    case Binary binary:
                    {
                        if (results.Count < 2)
                            throw new FormatException("IR lowering operand underflow");
                        int right = results.Pop();
                        int left = results.Pop();
                        var opcode = binary.Operator switch
                        {
                            '+' => IrOpcode.CheckedAdd,
                            '-' => IrOpcode.CheckedSubtract,
                            '*' => IrOpcode.CheckedMultiply,
                            '/' => IrOpcode.CheckedDivide,
                            _ => throw new FormatException("Unsupported or removed arithmetic operator")
                        };
                        results.Push(EmitBinary(opcode, left, right));
                        break;
                    }
                    case NamedIntegerRem:
                    case NamedIntegerMod:
                    case NamedIntegerPow:
                    {
                        if (results.Count < 2)
                            throw new FormatException("IR intrinsic operand underflow");
                        int right = results.Pop();
                        int left = results.Pop();
                        var op = node switch
                        {
                            NamedIntegerRem => IrOpcode.StrictRem,
                            NamedIntegerMod => IrOpcode.StrictMod,
                            NamedIntegerPow => IrOpcode.StrictPow,
                            _ => throw new FormatException("Unsupported typed Integer operation")
                        };
                        results.Push(EmitBinary(op, left, right));
                        break;
                    }
                    default:
                        throw new FormatException("Invalid IR lowering finish frame");
                }
            }
            else
            {
                if (++visited > MaxAstNodes)
                    throw new FormatException("IR AST node budget exceeded");
                switch (node)
                {
                    case ScalarPatternSlot slot:
                        if (!patternValues.TryGetValue(slot.Id,out int value))
                            throw new FormatException("Unbound scalar pattern slot");
                        results.Push(value);
                        break;
                    case ScalarPatternApply pattern:
                        if (pattern.Roots.Count != pattern.Slots.Count || pattern.Roots.Count > 8)
                            throw new FormatException("Invalid scalar pattern arity");
                        pending.Push(new Pending(pattern,true));
                        for (int i=pattern.Roots.Count-1;i>=0;i--)
                            pending.Push(new Pending(pattern.Roots[i],false));
                        break;
                    case Literal literal:
                        results.Push(Constant(literal.Value));
                        break;
                    case DotnetMathAbsI64 host:
                        pending.Push(new Pending(node, true));
                        pending.Push(new Pending(host.Value, false));
                        break;
                    case EntryScalarProjection projection:
                        pending.Push(new Pending(node, true));
                        pending.Push(new Pending(projection.Second, false));
                        pending.Push(new Pending(projection.First, false));
                        break;
                    case DotnetMathMaxI64 hostMax:
                        pending.Push(new Pending(node, true));
                        pending.Push(new Pending(hostMax.Right, false));
                        pending.Push(new Pending(hostMax.Left, false));
                        break;
                    case Unary { Operator: '+' or '-' } unary:
                        pending.Push(new Pending(node, true));
                        pending.Push(new Pending(unary.Value, false));
                        break;
                    case Binary binary when binary.Operator is '+' or '-' or '*' or '/':
                        pending.Push(new Pending(binary, true));
                        pending.Push(new Pending(binary.Right, false));
                        pending.Push(new Pending(binary.Left, false));
                        break;
                    case NamedBitXor bitXor:
                        pending.Push(new Pending(bitXor, true));
                        pending.Push(new Pending(bitXor.Right, false));
                        pending.Push(new Pending(bitXor.Left, false));
                        break;
                    case NamedIntegerRem call:
                        pending.Push(new Pending(call, true));
                        pending.Push(new Pending(call.Right, false));
                        pending.Push(new Pending(call.Left, false));
                        break;
                    case NamedIntegerMod call:
                        pending.Push(new Pending(call, true));
                        pending.Push(new Pending(call.Right, false));
                        pending.Push(new Pending(call.Left, false));
                        break;
                    case NamedIntegerPow call:
                        pending.Push(new Pending(call, true));
                        pending.Push(new Pending(call.Right, false));
                        pending.Push(new Pending(call.Left, false));
                        break;
                    default:
                        throw new FormatException("Unsupported AST node");
                }
            }
            if (results.Count > MaxPendingValues)
                throw new FormatException("IR evaluation stack budget exceeded");
        }
        if (patternValues.Count != 0 || results.Count != 1)
            throw new FormatException("IR lowering must yield exactly one result");
        return OresIrVerifier.Verify(new IrFunction(name, IrValueType.I64,
            instructions, results.Pop()), policy);
    }
}
