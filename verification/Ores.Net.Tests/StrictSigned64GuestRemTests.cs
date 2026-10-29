using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;
using System.Globalization;
using System.Text.Json;

// Compiled-source admission and emitted CIL inspection, not Native AOT proof.
internal static class StrictSigned64GuestRemTests
{
    private static void Require(bool pass, string why)
    {
        if (!pass) throw new Exception("FAILED: signed64 guest rem " + why);
    }

    private static VerifiedIrFunction CompileSource(string expression) =>
        OresIrLowering.Lower(OresParser.Parse(
            "define pub fnc main(): int { return " + expression + "; }"));

    private static void Reject(string expression)
    {
        try { _ = CompileSource(expression); }
        catch (FormatException) { return; }
        throw new Exception("Invalid signed64 call was admitted: " + expression);
    }

    private static void ExpectZeroDivisor(Action action, string why)
    {
        try { action(); }
        catch (DivideByZeroException) { return; }
        throw new Exception("Expected zero-divisor trap: " + why);
    }

    // These cases are not host-oracle shortcuts: feed the contract's literal
    // operands through the shipping guest parser and verified CIL generator.
    private static void VerifyPortableRemContract(string folder)
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "strict-named-integer-arithmetic-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement.GetProperty("cases");
        int admitted = 0;
        foreach (var item in cases.EnumerateArray())
        {
            if (item.GetProperty("operation").GetString() != "rem") continue;
            admitted++;
            string id = item.GetProperty("id").GetString()!;
            string left = item.GetProperty("left").GetString()!;
            string right = item.GetProperty("right").GetString()!;
            var expected = item.GetProperty("expect");
            var ir = CompileSource("Integer.rem(" + left + ", " + right + ")");
            Require(ir.Instructions.Any(op => op.Opcode == IrOpcode.StrictRem),
                "canonical guest source admission " + id);
            string emitted = Path.Combine(folder, "OresGenerated.contract.dll");
            CilEmitter.EmitLibrary(ir, emitted);
            using var assembly = AssemblyDefinition.ReadAssembly(emitted);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            Require(entry.Body.Instructions.Any(ins => ins.OpCode.Code == Code.Call &&
                ins.Operand is MethodReference method && method.Name == "Rem" &&
                method.DeclaringType.FullName ==
                    "Ores.Net.Runtime.StrictSigned64Arithmetic"),
                "portable rem emitted CIL static call " + id);
            if (expected.TryGetProperty("error", out var error))
            {
                Require(error.GetString() == "DIVISOR_ZERO",
                    "unexpected shared rem error category " + id);
                ExpectZeroDivisor(() => _ = OresIrReferenceEvaluator.Evaluate(ir),
                    "canonical SSA " + id);
                ExpectZeroDivisor(() => _ = CilReferenceEvaluator.Evaluate(entry),
                    "canonical emitted CIL " + id);
            }
            else
            {
                long value = long.Parse(expected.GetProperty("value").GetString()!,
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                Require(OresIrReferenceEvaluator.Evaluate(ir) == value,
                    "canonical guest SSA result " + id);
                Require(CilReferenceEvaluator.Evaluate(entry) == value,
                    "canonical emitted guest CIL result " + id);
            }
        }
        Require(admitted == 7, "all seven canonical rem vectors admitted");
    }

    public static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "oresnet-strict-rem-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (expression, expected) in new (string, long)[]
            {
                ("Integer.rem(-9, 4)", -1L),
                ("Integer.rem(9, -4)", 1L),
                ("Integer.rem(-9223372036854775808, -1)", 0L),
                ("Integer.rem(-9223372036854775808, 9223372036854775807)", -1L),
                ("Integer.rem(Integer.rem(9, 4), 2)", 1L),
                ("Integer.rem(-9, 4) + 43", 42L)
            })
            {
                var ir = CompileSource(expression);
                Require(ir.Instructions.Any(op => op.Opcode == IrOpcode.StrictRem),
                    "source must reach verified intrinsic opcode: " + expression);
                Require(OresIrReferenceEvaluator.Evaluate(ir) == expected,
                    "SSA reference value: " + expression);
                string emitted = Path.Combine(folder, "OresGenerated.dll");
                CilEmitter.EmitLibrary(ir, emitted);
                using var assembly = AssemblyDefinition.ReadAssembly(emitted);
                var entry = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(m => m.Name == "Run");
                var calls = entry.Body.Instructions.Where(ins =>
                    ins.OpCode.Code == Code.Call).ToArray();
                Require(calls.Length > 0, "emitted CIL must contain static call");
                Require(calls.All(ins => ins.Operand is MethodReference method &&
                    method.Name == "Rem" &&
                    method.DeclaringType.FullName ==
                        "Ores.Net.Runtime.StrictSigned64Arithmetic" &&
                    method.DeclaringType.Scope is AssemblyNameReference
                        { Name: "Ores.Net.Runtime" } &&
                    method.ReturnType.MetadataType == MetadataType.Int64 &&
                    method.Parameters.Count == 2 &&
                    method.Parameters.All(p =>
                        p.ParameterType.MetadataType == MetadataType.Int64)),
                    "CIL must reference exact runtime ABI");
                Require(CilReferenceEvaluator.Evaluate(entry) == expected,
                    "decoded emitted CIL reference value: " + expression);
                string reproducible = Path.Combine(folder, "OresGenerated.repro.dll");
                CilEmitter.EmitLibrary(ir, reproducible);
                Require(File.ReadAllBytes(emitted).AsSpan().SequenceEqual(
                    File.ReadAllBytes(reproducible)),
                    "CIL binary identity must be deterministic");
            }

            foreach (string invalid in new[]
            {
                "Integer.rem(1)", "Integer.rem(1, 2, 3)",
                "Integer.rem()", "Integer.rem(true, 1)",
                "Integer.rem(1.5, 2)", "Integer.rem(1, false)",
                "Integer.rem(1, 2.0)", "Integer.rem(left=1, 2)",
                "Integer.rem<int>(1, 2)", "Integer.mod(1, 2)",
                "Other.rem(1, 2)", "Integer.rem(1, 2).x"
            })
                Reject(invalid);

            var zero = CompileSource("Integer.rem(1, 0)");
            ExpectZeroDivisor(() => _ = OresIrReferenceEvaluator.Evaluate(zero),
                "SSA reference");
            string zeroDll = Path.Combine(folder, "OresGenerated.zero.dll");
            CilEmitter.EmitLibrary(zero, zeroDll);
            using var zeroAssembly = AssemblyDefinition.ReadAssembly(zeroDll);
            var zeroEntry = zeroAssembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            ExpectZeroDivisor(() => _ = CilReferenceEvaluator.Evaluate(zeroEntry),
                "decoded CIL reference");

            VerifyPortableRemContract(folder);

            // Deliberately forge forward-reference SSA: verifier must fail closed.
            var forged = new IrFunction("main", IrValueType.I64,
                new IrInstruction[]
                {
                    new(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 1),
                    new(1, IrOpcode.StrictRem, IrValueType.I64, Left: 0, Right: 1)
                }, 1);
            try { _ = OresIrVerifier.Verify(forged); }
            catch (FormatException) { return; }
            throw new Exception("Unverified intrinsic SSA was admitted");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
