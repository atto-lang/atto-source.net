using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

// Guest parser -> verified SSA -> actual CIL emission, not runtime oracle tests.
// The generated assembly is inspected without loading it, preserving NativeAOT
// build compatibility. A separate executed NativeAOT binary receipt is required.
internal static class NamedBitXorSourceTests
{
    public static void Run()
    {
        foreach (var (expression, left, right) in new[]
        {
            ("Integer.bitXor(0,0)", 0L, 0L),
            ("Integer.bitXor(5,3)", 5L, 3L),
            ("Integer.bitXor(-1,0)", -1L, 0L),
            ("Integer.bitXor(-9223372036854775808,9223372036854775807)", long.MinValue, long.MaxValue)
        })
        {
            var source = $"fnc main():int{{return {expression};}}";
            var verified = OresIrLowering.Lower(OresParser.Parse(source));
            if (verified.Instructions[^1].Opcode != IrOpcode.NamedBitXor)
                throw new Exception("Guest named XOR failed verified SSA lowering");
            var actual = left ^ right;
            var expected = expression switch
            {
                "Integer.bitXor(0,0)" => 0L,
                "Integer.bitXor(5,3)" => 6L,
                "Integer.bitXor(-1,0)" => -1L,
                _ => -1L
            };
            if (actual != expected)
                throw new Exception("Reference signed64 bitwise XOR vector mismatch");

            var output = Path.Combine(Path.GetTempPath(),
                $"oresnet-named-xor-{Guid.NewGuid():N}", "OresGenerated.dll");
            try
            {
                CilEmitter.EmitLibrary(verified, output);
                using var assembly = AssemblyDefinition.ReadAssembly(output);
                var run = assembly.MainModule.Types.SelectMany(t => t.Methods)
                    .Single(m => m.Name == "Run");
                if (!run.HasBody || run.Body.Instructions.Count(i => i.OpCode == OpCodes.Xor) != 1)
                    throw new Exception("Native-targeted CIL output lacks exactly one XOR opcode");
            }
            finally
            {
                if (File.Exists(output)) File.Delete(output);
                var dir = Path.GetDirectoryName(output)!;
                if (Directory.Exists(dir)) Directory.Delete(dir);
            }

            if (!ManagedSourceEmitter.Emit(verified, ManagedSourceTarget.CSharp)
                    .Contains(" ^ ", StringComparison.Ordinal) ||
                !ManagedSourceEmitter.Emit(verified, ManagedSourceTarget.FSharp)
                    .Contains(" ^^^ ", StringComparison.Ordinal))
                throw new Exception("Managed source target XOR drift");
        }

        foreach (var source in new[]
        {
            "fnc main():int{return Integer.bitXor(1);}",
            "fnc main():int{return Integer.bitXor(1,2,3);}",
            "fnc main():int{return Integer.mod(9,4);}",
            "fnc main():int{return Integer.bitXor(1.5,2);}",
            "fnc main():int{return 5^3;}"
        })
        {
            try
            {
                _ = OresIrLowering.Lower(OresParser.Parse(source));
            }
            catch (FormatException) { continue; }
            throw new Exception("Unsupported named XOR shape was admitted: " + source);
        }
    }
}
