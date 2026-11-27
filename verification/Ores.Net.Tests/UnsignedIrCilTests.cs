using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

internal static class UnsignedIrCilTests
{
    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("Unsigned IR: " + why);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Unsigned IR unexpectedly admitted invalid input");
    }
    private static IrInstruction Constant(int id, ulong value) =>
        new(id, IrOpcode.ConstantU64, IrValueType.U64) { UnsignedConstant = value };
    private static IrInstruction Binary(int id, IrOpcode opcode, int a, int b) =>
        new(id, opcode, IrValueType.U64, Left: a, Right: b);
    private static IrFunction Function(params IrInstruction[] ops) =>
        new("main", IrValueType.U64, ops, ops.Length - 1);

    public static void Run()
    {
        var parsed = OresParser.LowerUnsigned("pub fnc main():uint { return (2+3)*4/2; }");
        Require(parsed.ResultType == IrValueType.U64, "source result signedness");
        Require(parsed.Instructions.Where(x => x.Opcode != IrOpcode.ConstantU64).Select(x => x.Opcode)
            .SequenceEqual(new[] { IrOpcode.CheckedAddU64, IrOpcode.CheckedMultiplyU64, IrOpcode.CheckedDivideU64 }),
            "source precedence and ordered SSA");
        Require(OresParser.LowerUnsigned("pub fnc main():u64 {return 18_446_744_073_709_551_615;}").Instructions[0].UnsignedConstant == ulong.MaxValue,
            "source literal full width");
        foreach (string invalid in new[] { "-1", "01", "18446744073709551616", "1.0", "1^^2", "x", "f()", "1;return 2", new string('(', 130) + "1" + new string(')', 130) })
            Reject(() => OresParser.LowerUnsigned("pub fnc main():u64{return " + invalid + ";}"));
        Reject(() => OresParser.LowerUnsigned("pub fnc main():int{return 1;}"));
        Reject(() => OresParser.LowerUnsigned("pub fnc main():u64{return 1;} fnc f():u64{return 2;}"));
        Reject(() => OresParser.Parse("pub fnc main():u64{return 1;}"));
        Reject(() => OresParser.LowerUnsigned("pub fnc main():u64{return " + string.Join("+", Enumerable.Repeat("1", 9000)) + ";}"));

        var input = new[] {
            Constant(0, ulong.MaxValue), Constant(1, 1),
            Binary(2, IrOpcode.CheckedSubtractU64, 0, 1),
            Binary(3, IrOpcode.CheckedAddU64, 2, 1),
            Binary(4, IrOpcode.CheckedMultiplyU64, 3, 1),
            Binary(5, IrOpcode.CheckedDivideU64, 4, 1)
        };
        var source = Function(input);
        var verified = OresIrVerifier.VerifyUnsigned(source);
        input[0] = Constant(0, 0);
        Require(verified.Instructions[0].UnsignedConstant == ulong.MaxValue, "immutable snapshot");
        Reject(() => OresIrVerifier.Verify(source));
        Reject(() => OresIrVerifier.VerifyUnsigned(source with { ResultType = IrValueType.I64 }));
        Reject(() => OresIrVerifier.VerifyUnsigned(Function(Constant(0, 0) with { Constant = 0 })));
        Reject(() => OresIrVerifier.VerifyUnsigned(Function(Constant(0, 0) with { UnsignedConstant = null })));
        Reject(() => OresIrVerifier.VerifyUnsigned(Function(Constant(1, 0))));
        Reject(() => OresIrVerifier.VerifyUnsigned(Function(Constant(0, 0), Binary(1, IrOpcode.CheckedAddU64, 0, 1))));
        Reject(() => OresIrVerifier.VerifyUnsigned(Function(Constant(0, 0), Binary(1, IrOpcode.CheckedAdd, 0, 0))));
        Reject(() => OresIrVerifier.VerifyUnsigned(Function(Constant(0, 0) with { Type = IrValueType.I64 })));
        Reject(() => OresIrVerifier.VerifyUnsigned(source with { ReturnId = -1 }));
        Reject(() => OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64, new[] {
            new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 42) { UnsignedConstant = 1 }
        }, 0)));

        string directory = Path.Combine(Path.GetTempPath(), "atto-u64-ir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "OresGenerated.dll");
            Reject(() => CilEmitter.EmitLibrary(verified, output));
            Require(!File.Exists(output), "wrong ABI emits no artifact");
            CilEmitter.EmitUnsignedLibrary(verified, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var method = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram").Methods.Single();
            Require(method.Name == "RunU64" && method.ReturnType.MetadataType == MetadataType.UInt64,
                "explicit unsigned method signature");
            Require(method.Body.Variables.Count == 6 && method.Body.Variables.All(v =>
                v.VariableType.MetadataType == MetadataType.UInt64), "unsigned local signatures");
            var instructions = method.Body.Instructions;
            Require(instructions.Any(i => i.OpCode.Code == Code.Ldc_I8 && (long)i.Operand == -1L), "max u64 bits");
            var operations = instructions.Select(i => i.OpCode.Code).Where(code =>
                code is Code.Add_Ovf_Un or Code.Sub_Ovf_Un or Code.Mul_Ovf_Un or Code.Div_Un).ToArray();
            Require(operations.SequenceEqual(new[] { Code.Sub_Ovf_Un, Code.Add_Ovf_Un, Code.Mul_Ovf_Un, Code.Div_Un }),
                "ordered checked unsigned opcodes");
            Require(!instructions.Any(i => i.OpCode.Code is Code.Add_Ovf or Code.Sub_Ovf or Code.Mul_Ovf or Code.Div),
                "no signed arithmetic fallback");
            var repeat = Path.Combine(directory, "repeat.dll");
            CilEmitter.EmitUnsignedLibrary(verified, repeat);
            Require(File.ReadAllBytes(output).AsSpan().SequenceEqual(File.ReadAllBytes(repeat)), "deterministic PE");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
