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

    private static void ExpectOverflow(Action action, string why)
    {
        try { action(); }
        catch (OverflowException) { return; }
        throw new Exception("Expected checked signed64 overflow: " + why);
    }

    // Mutate a *copy* of generated CIL metadata, not the emitted assembly or
    // the original method reference. Every illegal call shape must fail closed.
    private static void RejectForgedRemCall(MethodDefinition entry,
        Action<MethodReference> mutate, string label)
    {
        var instruction = entry.Body.Instructions.Single(ins =>
            ins.OpCode.Code == Code.Call);
        if (instruction.Operand is not MethodReference original)
            throw new Exception("Expected generated CIL method reference");
        var forged = new MethodReference(
            original.Name, original.ReturnType, original.DeclaringType)
        {
            HasThis = original.HasThis,
            ExplicitThis = original.ExplicitThis,
            CallingConvention = original.CallingConvention
        };
        foreach (var parameter in original.Parameters)
            forged.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        mutate(forged);
        instruction.Operand = forged;
        try
        {
            try { _ = CilReferenceEvaluator.Evaluate(entry); }
            catch (FormatException) { return; }
            throw new Exception("Forged CIL call was admitted: " + label);
        }
        finally { instruction.Operand = original; }
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
                ins.Operand is MethodReference method && !method.HasThis &&
                !method.ExplicitThis && method.GenericParameters.Count == 0 &&
                method.CallingConvention == MethodCallingConvention.Default &&
                method.Name == "Rem" &&
                method.DeclaringType.FullName ==
                    "Ores.Net.Runtime.StrictSigned64Arithmetic" &&
                method.DeclaringType.Scope is AssemblyNameReference
                    { Name: "Ores.Net.Runtime", Version: { Major: 1, Minor: 0, Build: 0, Revision: 0 } } &&
                method.ReturnType.MetadataType == MetadataType.Int64 &&
                method.Parameters.Count == 2 &&
                method.Parameters.All(p => p.ParameterType.MetadataType == MetadataType.Int64)),
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

    private static void VerifyPortableModContract(string folder)
    {
        // This is real source -> verified SSA -> emitted static CIL, verified
        // independently with two readers. It is NOT a Native AOT execution.
        var path = Path.Combine(AppContext.BaseDirectory,
            "strict-named-integer-arithmetic-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        int admitted = 0;
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (item.GetProperty("operation").GetString() != "mod") continue;
            admitted++;
            string id = item.GetProperty("id").GetString()!;
            string left = item.GetProperty("left").GetString()!;
            string right = item.GetProperty("right").GetString()!;
            var ir = CompileSource("Integer.mod(" + left + ", " + right + ")");
            Require(ir.Instructions.Any(op => op.Opcode == IrOpcode.StrictMod),
                "mod guest source reaches dedicated verified opcode: " + id);
            string emitted = Path.Combine(folder, "OresGenerated.mod.dll");
            CilEmitter.EmitLibrary(ir, emitted);
            using var assembly = AssemblyDefinition.ReadAssembly(emitted);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            Require(entry.Body.Instructions.Any(ins => ins.OpCode.Code == Code.Call &&
                ins.Operand is MethodReference method && !method.HasThis &&
                !method.ExplicitThis && method.GenericParameters.Count == 0 &&
                method.CallingConvention == MethodCallingConvention.Default &&
                method.Name == "Mod" &&
                method.DeclaringType.FullName ==
                    "Ores.Net.Runtime.StrictSigned64Arithmetic" &&
                method.DeclaringType.Scope is AssemblyNameReference
                    { Name: "Ores.Net.Runtime", Version: { Major: 1, Minor: 0, Build: 0, Revision: 0 } } &&
                method.ReturnType.MetadataType == MetadataType.Int64 &&
                method.Parameters.Count == 2 &&
                method.Parameters.All(p => p.ParameterType.MetadataType == MetadataType.Int64)),
                "mod static CIL MemberRef ABI: " + id);
            var expected = item.GetProperty("expect");
            if (expected.TryGetProperty("error", out var failure))
            {
                Require(failure.GetString() == "DIVISOR_ZERO",
                    "expected modulus error class: " + id);
                ExpectZeroDivisor(() => _ = OresIrReferenceEvaluator.Evaluate(ir),
                    "mod SSA: " + id);
                ExpectZeroDivisor(() => _ = CilReferenceEvaluator.Evaluate(entry),
                    "mod decoded CIL: " + id);
            }
            else
            {
                long wanted = long.Parse(expected.GetProperty("value").GetString()!,
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                Require(OresIrReferenceEvaluator.Evaluate(ir) == wanted,
                    "mod SSA reference: " + id);
                Require(CilReferenceEvaluator.Evaluate(entry) == wanted,
                    "mod decoded CIL reference: " + id);
            }
            // A forged intrinsic name or ABI must fail before invocation.
            RejectForgedRemCall(entry, method => method.Name = "NotMod",
                "forged mod intrinsic name: " + id);
            RejectForgedRemCall(entry, method =>
                method.Parameters[0].ParameterType = entry.Module.TypeSystem.Int32,
                "forged mod parameter type: " + id);
        }
        Require(admitted == 8, "all eight shared signed64 modulus vectors admitted");

        // The two instructions have the same CIL call token index in otherwise
        // identical modules. Their MVID must still differ because their
        // static runtime methods, and therefore behavior, differ.
        string remAssemblyPath = Path.Combine(folder, "OresGenerated.rem.identity.dll");
        string modAssemblyPath = Path.Combine(folder, "OresGenerated.mod.identity.dll");
        var remProgram = CompileSource("Integer.rem(-9, 4)");
        var modProgram = CompileSource("Integer.mod(-9, 4)");
        CilEmitter.EmitLibrary(remProgram, remAssemblyPath);
        CilEmitter.EmitLibrary(modProgram, modAssemblyPath);
        using (var remAssembly = AssemblyDefinition.ReadAssembly(remAssemblyPath))
        using (var modAssembly = AssemblyDefinition.ReadAssembly(modAssemblyPath))
        {
            Require(remAssembly.MainModule.Mvid != modAssembly.MainModule.Mvid,
                "different verified runtime methods cannot share a deterministic MVID");
            Require(remAssembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run").Body.Instructions.Any(ins =>
                    ins.OpCode.Code == Code.Call &&
                    ins.Operand is MethodReference mr && mr.Name == "Rem"),
                "rem module preserves the exact static method name");
            Require(modAssembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run").Body.Instructions.Any(ins =>
                    ins.OpCode.Code == Code.Call &&
                    ins.Operand is MethodReference mr && mr.Name == "Mod"),
                "mod module preserves the exact static method name");
        }
        string replayPath = Path.Combine(folder, "OresGenerated.mod.identity.repro.dll");
        CilEmitter.EmitLibrary(modProgram, replayPath);
        Require(File.ReadAllBytes(modAssemblyPath).AsSpan().SequenceEqual(
            File.ReadAllBytes(replayPath)),
            "same verified mod source must emit byte-identical deterministic assembly");

        foreach (var (expression, wanted) in new (string, long)[]
        {
            ("Integer.mod(-9, 4) + 39", 42),
            ("Integer.mod(-1, -9223372036854775808)", long.MaxValue),
            ("Integer.mod(-9223372036854775808, 9223372036854775807)",
                long.MaxValue - 1),
            ("Integer.mod(Integer.rem(-9, 4), -4)", 3),
            ("Integer.rem(Integer.mod(-9, 4), 2)", 1)
        })
        {
            var ir = CompileSource(expression);
            string emitted = Path.Combine(folder, "OresGenerated.mod.nested.dll");
            CilEmitter.EmitLibrary(ir, emitted);
            using var assembly = AssemblyDefinition.ReadAssembly(emitted);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            Require(OresIrReferenceEvaluator.Evaluate(ir) == wanted &&
                    CilReferenceEvaluator.Evaluate(entry) == wanted,
                "nested strict numeric call exact result: " + expression);
        }
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
                ("Integer.rem(-9, 4) + 43", 42L),
                ("Integer.rem(-9, 4) + (9 % -4)", 0L),
                ("Integer.rem(9 % 4, Integer.rem(10, 3))", 0L),
                ("Integer.rem(-9223372036854775808, -1) + (11 % 6)", 5L)
            })
            {
                var ir = CompileSource(expression);
                Require(ir.Instructions.Any(op => op.Opcode == IrOpcode.StrictRem),
                    "source must reach verified intrinsic opcode: " + expression);
                if (expression.Contains('%'))
                    Require(ir.Instructions.Any(op => op.Opcode == IrOpcode.SignedRemainderLiteral),
                        "merged literal % and named remainder must remain distinct SSA operations");
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
                if (expression.Contains('%'))
                    Require(entry.Body.Instructions.Any(ins => ins.OpCode.Code == Code.Rem),
                        "literal % must survive alongside named rem static CIL call");
                Require(calls.All(ins => ins.Operand is MethodReference method &&
                    method.Name == "Rem" &&
                    !method.HasThis && !method.ExplicitThis &&
                    method.GenericParameters.Count == 0 &&
                    method.CallingConvention == MethodCallingConvention.Default &&
                    method.DeclaringType.FullName ==
                        "Ores.Net.Runtime.StrictSigned64Arithmetic" &&
                    method.DeclaringType.Scope is AssemblyNameReference
                        { Name: "Ores.Net.Runtime", Version: { Major: 1, Minor: 0, Build: 0, Revision: 0 } } &&
                    method.ReturnType.MetadataType == MetadataType.Int64 &&
                    method.Parameters.Count == 2 &&
                    method.Parameters.All(p =>
                        p.ParameterType.MetadataType == MetadataType.Int64)),
                    "CIL must reference exact runtime ABI");
                Require(CilReferenceEvaluator.Evaluate(entry) == expected,
                    "decoded emitted CIL reference value: " + expression);
                if (expression == "Integer.rem(-9, 4)")
                {
                    RejectForgedRemCall(entry, m => m.Name = "NotRem",
                        "wrong intrinsic name");
                    RejectForgedRemCall(entry, m => m.HasThis = true,
                        "instance-call substitution");
                    RejectForgedRemCall(entry, m => m.ExplicitThis = true,
                        "explicit-instance call");
                    RejectForgedRemCall(entry,
                        m => m.CallingConvention = MethodCallingConvention.VarArg,
                        "varargs ABI substitution");
                    RejectForgedRemCall(entry,
                        m => m.ReturnType = entry.Module.TypeSystem.Int32,
                        "wrong result type");
                    RejectForgedRemCall(entry,
                        m => m.Parameters.RemoveAt(1),
                        "missing signed64 operand");
                    RejectForgedRemCall(entry,
                        m => m.Parameters[0].ParameterType = entry.Module.TypeSystem.Int32,
                        "wrong operand type");
                    RejectForgedRemCall(entry,
                        m => m.DeclaringType = entry.Module.TypeSystem.Object,
                        "wrong runtime type");
                    RejectForgedRemCall(entry,
                        m => m.DeclaringType = new Mono.Cecil.TypeReference(
                            "Ores.Net.Runtime", "StrictSigned64Arithmetic", entry.Module,
                            new AssemblyNameReference("Ores.Net.Runtime", new Version(2, 0, 0, 0))),
                        "wrong runtime assembly version");
                }
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
                "Integer.rem<int>(1, 2)", "Integer.mod(1)",
                "Integer.mod(1, 2, 3)", "Integer.mod(true, 2)",
                "Integer.mod(1, false)", "Integer.pow(1, 2)",
                "Other.rem(1, 2)", "Integer.rem(1, 2).x",
                "Integer.rem(1, 2) % 0",
                "Integer.rem(1, 2) % Integer.rem(1, 2)"
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

            // Eager left-to-right checked arithmetic must win over a later
            // divisor-zero trap; neither SSA nor decoded CIL may reorder the
            // operands or constant-fold away the overflowing expression.
            foreach (string overflowFirst in new[]
            {
                "Integer.rem(9223372036854775807 + 1, 0)",
                "Integer.rem(-9223372036854775808 - 1, 0)",
                "Integer.rem(1, 9223372036854775807 + 1)"
            })
            {
                var verified = CompileSource(overflowFirst);
                Require(verified.Instructions.Any(op => op.Opcode == IrOpcode.StrictRem),
                    "typed named rem preserved despite checked overflow");
                ExpectOverflow(() => _ = OresIrReferenceEvaluator.Evaluate(verified),
                    "verified SSA eager order: " + overflowFirst);
                string emittedOverflow = Path.Combine(folder, "OresGenerated.overflow.dll");
                CilEmitter.EmitLibrary(verified, emittedOverflow);
                using var overflowAssembly = AssemblyDefinition.ReadAssembly(emittedOverflow);
                var overflowEntry = overflowAssembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(m => m.Name == "Run");
                ExpectOverflow(() => _ = CilReferenceEvaluator.Evaluate(overflowEntry),
                    "decoded CIL eager order: " + overflowFirst);
            }

            VerifyPortableRemContract(folder);
            VerifyPortableModContract(folder);

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
