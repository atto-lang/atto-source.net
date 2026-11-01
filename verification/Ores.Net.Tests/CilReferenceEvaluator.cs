using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Runtime;

// Verification-only reader/interpreter for the exact CIL bootstrap subset.
// Does not Assembly.Load guest code or use Reflection.Emit.
internal static class CilReferenceEvaluator
{
    public static long Evaluate(MethodDefinition method)
    {
        if (!method.HasBody)
            throw new FormatException("Missing CIL body");

        var locals = new long[method.Body.Variables.Count];
        var stack = new Stack<long>();
        foreach (var ins in method.Body.Instructions)
        {
            switch (ins.OpCode.Code)
            {
                // Method-body alignment and compiler-inserted no-ops have no
                // effect on the evaluation stack; reject other unknown IL.
                case Code.Nop:
                    break;
                case Code.Ldc_I8:
                    stack.Push((long)ins.Operand);
                    break;
                case Code.Ldloc_0: stack.Push(locals[0]); break;
                case Code.Ldloc_1: stack.Push(locals[1]); break;
                case Code.Ldloc_2: stack.Push(locals[2]); break;
                case Code.Ldloc_3: stack.Push(locals[3]); break;
                case Code.Stloc_0: locals[0] = stack.Pop(); break;
                case Code.Stloc_1: locals[1] = stack.Pop(); break;
                case Code.Stloc_2: locals[2] = stack.Pop(); break;
                case Code.Stloc_3: locals[3] = stack.Pop(); break;
                case Code.Ldloc:
                case Code.Ldloc_S:
                    stack.Push(locals[((VariableDefinition)ins.Operand).Index]);
                    break;
                case Code.Stloc:
                case Code.Stloc_S:
                    locals[((VariableDefinition)ins.Operand).Index] = stack.Pop();
                    break;
                case Code.Add_Ovf:
                case Code.Sub_Ovf:
                case Code.Mul_Ovf:
                case Code.Div:
                case Code.Rem:
                {
                    long right = stack.Pop();
                    long left = stack.Pop();
                    long value = ins.OpCode.Code switch
                    {
                        Code.Add_Ovf => checked(left + right),
                        Code.Sub_Ovf => checked(left - right),
                        Code.Mul_Ovf => checked(left * right),
                        Code.Div => DivideChecked(left, right),
                        Code.Rem => right == -1 ? 0 : left % right,
                        _ => throw new FormatException("Unexpected CIL operator")
                    };
                    stack.Push(value);
                    break;
                }
                case Code.Call:
                {
                    // Verification-only interpreter: only the exact statically
                    // linked intrinsic signature is recognized. Not a native run.
                    if (ins.Operand is not MethodReference calledMethod ||
                        calledMethod.Name is not ("Rem" or "Mod" or "Pow" or "BitXor") ||
                        calledMethod.DeclaringType.FullName !=
                            "Ores.Net.Runtime.StrictSigned64Arithmetic" ||
                        calledMethod.DeclaringType.Scope is not AssemblyNameReference
                            { Name: "Ores.Net.Runtime", Version: { Major: 1, Minor: 0, Build: 0, Revision: 0 } } ||
                        calledMethod.HasThis || calledMethod.ExplicitThis ||
                        calledMethod.GenericParameters.Count != 0 ||
                        calledMethod.CallingConvention != MethodCallingConvention.Default ||
                        calledMethod.ReturnType.MetadataType != MetadataType.Int64 ||
                        calledMethod.Parameters.Count != 2 ||
                        calledMethod.Parameters.Any(p => p.ParameterType.MetadataType != MetadataType.Int64))
                        throw new FormatException("Unverified CIL method call");
                    long right = stack.Pop();
                    long left = stack.Pop();
                    stack.Push(calledMethod.Name switch
                    {
                        "Rem" => StrictSigned64Arithmetic.Rem(left, right),
                        "Mod" => StrictSigned64Arithmetic.Mod(left, right),
                        "Pow" => StrictSigned64Arithmetic.Pow(left, right),
                        "BitXor" => StrictSigned64Arithmetic.BitXor(left, right),
                        _ => throw new FormatException("Unverified CIL integer operation")
                    });
                    break;
                }
                case Code.Ret:
                    if (stack.Count != 1)
                        throw new FormatException("CIL return stack imbalance");
                    return stack.Pop();
                default:
                    throw new FormatException("Unsupported CIL opcode " + ins.OpCode.Code);
            }
            if (stack.Count > method.Body.MaxStackSize)
                throw new FormatException("CIL body understates maximum stack");
        }
        throw new FormatException("Missing CIL return");
    }

    private static long DivideChecked(long left, long right)
    {
        if (right == 0) throw new DivideByZeroException();
        if (left == long.MinValue && right == -1) throw new OverflowException();
        return left / right;
    }
}
