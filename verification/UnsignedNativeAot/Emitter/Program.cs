using Ores.Net.Compiler;

// Emit verified IR, not a source parser or an interpreter.
if (args.Length != 2) throw new ArgumentException("usage: Emitter CASE OUTPUT");
IrInstruction C(int id, ulong v) => new(id, IrOpcode.ConstantU64, IrValueType.U64) { UnsignedConstant = v };
IrInstruction B(int id, IrOpcode op, int a, int b) => new(id, op, IrValueType.U64, Left: a, Right: b);
var ops = args[0] switch
{
    "max" => new[] { C(0, ulong.MaxValue), C(1, 1), B(2, IrOpcode.CheckedSubtractU64, 0, 1), B(3, IrOpcode.CheckedAddU64, 2, 1) },
    "divide" => new[] { C(0, ulong.MaxValue), C(1, 2), B(2, IrOpcode.CheckedDivideU64, 0, 1) },
    "multiply" => new[] { C(0, 4294967295), C(1, 4294967297), B(2, IrOpcode.CheckedMultiplyU64, 0, 1) },
    "add-overflow" => new[] { C(0, ulong.MaxValue), C(1, 1), B(2, IrOpcode.CheckedAddU64, 0, 1), B(3, IrOpcode.CheckedSubtractU64, 2, 1) },
    "sub-underflow" => new[] { C(0, 0), C(1, 1), B(2, IrOpcode.CheckedSubtractU64, 0, 1) },
    "mul-overflow" => new[] { C(0, 9223372036854775808UL), C(1, 2), B(2, IrOpcode.CheckedMultiplyU64, 0, 1), C(3, 0), B(4, IrOpcode.CheckedMultiplyU64, 2, 3) },
    "divide-zero" => new[] { C(0, ulong.MaxValue), C(1, 0), B(2, IrOpcode.CheckedDivideU64, 0, 1) },
    _ => throw new ArgumentException("unknown case")
};
var checkedIr = OresIrVerifier.VerifyUnsigned(new IrFunction("main", IrValueType.U64, ops, ops.Length - 1));
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
CilEmitter.EmitUnsignedLibrary(checkedIr, output);
