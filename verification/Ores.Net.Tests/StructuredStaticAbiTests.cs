using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

// Strictly experimental, separately compiled MethodDef ABI proof.
// A second source/inlined CFG interpreter supplies the expected result while
// the independent CIL test interpreter actually follows call MethodDef tokens.
internal static class StructuredStaticAbiTests
{
    private static void Check(string source, long expected, int helperCount,
        string label, bool expressionOnlyReference = true)
    {
        var module = OresStructuredCfgLowering.LowerModule(source);
        if (expressionOnlyReference)
        {
            var inline = OresStructuredCfgLowering.Lower(source);
            if (CfgReferenceEvaluator.Evaluate(inline) != expected)
                throw new Exception("Structured inline reference disagreement: " + label);
        }
        if (module.Main.Name != "main" || module.Helpers.Count != helperCount)
            throw new Exception("Unexpected verified static ABI method count: " + label);

        string folder = Path.Combine(Path.GetTempPath(),
            "ores-static-abi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string first = Path.Combine(folder, "OresGenerated.dll");
            string second = Path.Combine(folder, "OresGenerated.copy.dll");
            CilEmitter.EmitLibrary(module, first);
            CilEmitter.EmitLibrary(module, second);
            if (!File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second)))
                throw new Exception("Non-deterministic static MethodDef metadata: " + label);
            using var assembly = AssemblyDefinition.ReadAssembly(first);
            var type = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram");
            if (type.Methods.Count != helperCount + 1)
                throw new Exception("Missing independent helper CIL methods: " + label);
            var run = type.Methods.Single(m => m.Name == "Run");
            if (!run.IsPublic || !run.IsStatic || run.Parameters.Count != 0 ||
                run.ReturnType.MetadataType != MetadataType.Int64)
                throw new Exception("Broken Native AOT static Run entry signature: " + label);
            if (CfgCilTestInterpreter.Evaluate(run) != expected)
                throw new Exception("Static ABI emitted CIL disagrees with source: " + label);
            // A declaration is not an invocation. In particular, an unused
            // helper must remain an independent MethodDef with zero emitted
            // calls, while each verified CFG Call must map to its exact
            // emitted target (not merely to any helper with the same arity).
            AssertExactCallTargets(module, type, label);
            // Compare the emitted MethodDef table against every independently
            // verified CFG signature, not just the aggregate method count.
            for (int index = 0; index < module.Helpers.Count; index++)
            {
                var expectedHelper = module.Helpers[index];
                var helper = type.Methods[index + 1];
                if (!helper.IsPrivate || !helper.IsStatic ||
                    helper.Name != "helper_" + expectedHelper.Name ||
                    helper.Parameters.Count != expectedHelper.Parameters.Count ||
                    helper.ReturnType.MetadataType != MetadataTypeFor(expectedHelper.ResultType))
                    throw new Exception("Helper MethodDef signature mismatch: " + label);
                for (int argument = 0; argument < helper.Parameters.Count; argument++)
                {
                    if (helper.Parameters[argument].ParameterType.MetadataType !=
                        MetadataTypeFor(expectedHelper.Parameters[argument]) ||
                        helper.Parameters[argument].Name != "arg" + argument ||
                        helper.Parameters[argument].Index != argument)
                        throw new Exception("Helper signature/ParamList ABI mismatch: " + label);
                }
                if (!helper.Body.Instructions.Any(i =>
                    i.OpCode.Code is Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2
                        or Code.Ldarg_3 or Code.Ldarg or Code.Ldarg_S) &&
                    helper.Parameters.Count > 0)
                    throw new Exception("Helper was not compiled with ldarg: " + label);
            }
            AssertClosedDirectCalls(type, label);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void AssertExactCallTargets(
        VerifiedCfgModule module, TypeDefinition type, string label)
    {
        var verifiedMethods = new[] { module.Main }.Concat(module.Helpers).ToArray();
        if (type.Methods.Count != verifiedMethods.Length)
            throw new FormatException("Verified CFG and CIL method count differ: " + label);
        for (int methodIndex = 0; methodIndex < verifiedMethods.Length; methodIndex++)
        {
            var expectedTargets = verifiedMethods[methodIndex].Blocks
                .SelectMany(block => block.Instructions)
                .Where(op => op.Opcode == CfgOpcode.Call)
                .Select(op => "helper_" + op.Target).ToArray();
            var emittedTargets = type.Methods[methodIndex].Body.Instructions
                .Where(op => op.OpCode.Code == Code.Call)
                .Select(op => op.Operand is MethodDefinition callee
                    ? callee.Name
                    : throw new FormatException("Non-MethodDef CIL call: " + label))
                .ToArray();
            if (!expectedTargets.AsSpan().SequenceEqual(emittedTargets))
                throw new FormatException("Verified CFG and CIL call targets differ: " + label);
        }
    }

    private static void RejectSameSignatureRetargetMutation()
    {
        // Both helpers are intentionally ABI compatible. A validator that
        // merely checks owner/private/static/arity cannot distinguish them;
        // the exact verified CFG target must still determine the call.
        var module = OresStructuredCfgLowering.LowerModule(
            "fnc correct(int n):int{return n+1;}" +
            "fnc wrong(int n):int{return n+2;}" +
            "fnc main():int{return correct(41);}");
        string folder = Path.Combine(Path.GetTempPath(),
            "ores-static-abi-retarget-mutation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(module, file);
            using var image = AssemblyDefinition.ReadAssembly(file);
            var type = image.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram");
            var run = type.Methods.Single(m => m.Name == "Run");
            var wrong = type.Methods.Single(m => m.Name == "helper_wrong");
            var call = run.Body.Instructions.Single(i => i.OpCode.Code == Code.Call);
            var original = call.Operand;
            try
            {
                call.Operand = wrong;
                // This less-specific check is expected to pass; the target
                // is private and static and belongs to the right type.
                AssertClosedDirectCalls(type, "same-signature-retarget");
                try
                {
                    AssertExactCallTargets(module, type, "same-signature-retarget");
                }
                catch (FormatException error) when
                    (error.Message.Contains("call targets differ", StringComparison.Ordinal))
                {
                    return;
                }
                throw new Exception("ABI-compatible forged MethodDef retarget escaped CFG proof");
            }
            finally { call.Operand = original; }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void AssertClosedDirectCalls(TypeDefinition type, string label)
    {
        foreach (var method in type.Methods)
            foreach (var instruction in method.Body.Instructions)
            {
                // This currently supported scalar helper profile has NO
                // runtime metadata lookup, method-pointer or virtual ABI.
                if (instruction.OpCode.Code is Code.Calli or Code.Callvirt
                    or Code.Ldftn or Code.Ldvirtftn or Code.Newobj
                    or Code.Jmp or Code.Ldtoken)
                    throw new FormatException("Dynamic dispatch escaped static ABI: " + label);
                if (instruction.OpCode.Code != Code.Call) continue;
                if (instruction.Operand is not MethodDefinition callee ||
                    !ReferenceEquals(callee.DeclaringType, type) ||
                    !callee.IsPrivate || !callee.IsStatic ||
                    !callee.Name.StartsWith("helper_", StringComparison.Ordinal) ||
                    method.Body.MaxStackSize < callee.Parameters.Count)
                    throw new FormatException("Call operand was not a bounded local MethodDef: " + label);
            }
    }

    private static void RejectDynamicOpcodeMutation()
    {
        var module = OresStructuredCfgLowering.LowerModule(
            "fnc inc(int n):int{return n+1;}fnc main():int{return inc(41);}");
        string folder = Path.Combine(Path.GetTempPath(),
            "ores-static-abi-dynamic-mutation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(module, file);
            using var image = AssemblyDefinition.ReadAssembly(file);
            var type = image.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram");
            var run = type.Methods.Single(m => m.Name == "Run");
            var call = run.Body.Instructions.Single(i => i.OpCode.Code == Code.Call);
            var original = call.OpCode;
            foreach (var injected in new[] {
                OpCodes.Calli, OpCodes.Callvirt, OpCodes.Ldftn,
                OpCodes.Ldvirtftn, OpCodes.Jmp, OpCodes.Ldtoken, OpCodes.Newobj
            })
            {
                try
                {
                    call.OpCode = injected;
                    try
                    {
                        AssertClosedDirectCalls(type, injected.Name);
                    }
                    catch (FormatException error) when
                        (error.Message.Contains("Dynamic dispatch escaped static ABI",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    throw new Exception("Static ABI checker accepted forged opcode: " + injected.Name);
                }
                finally { call.OpCode = original; }
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static MetadataType MetadataTypeFor(IrValueType type) => type switch
    {
        IrValueType.I64 => MetadataType.Int64,
        IrValueType.Bool => MetadataType.Boolean,
        _ => throw new FormatException("Unsupported static ABI test type")
    };

    private static void Reject(string source, string label)
    {
        try { _ = OresStructuredCfgLowering.LowerModule(source); }
        catch (FormatException) { return; }
        throw new Exception("Unsafe source accepted by static MethodDef ABI: " + label);
    }

    private static void CheckOverflow(string source, string label)
    {
        var inline = OresStructuredCfgLowering.Lower(source);
        bool sourceOverflow = false;
        try { _ = CfgReferenceEvaluator.Evaluate(inline); }
        catch (OverflowException) { sourceOverflow = true; }
        if (!sourceOverflow)
            throw new Exception("Reference helper call must overflow: " + label);

        string folder = Path.Combine(Path.GetTempPath(),
            "ores-static-abi-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(OresStructuredCfgLowering.LowerModule(source), file);
            using var image = AssemblyDefinition.ReadAssembly(file);
            var entry = image.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            bool cilOverflow = false;
            try { _ = CfgCilTestInterpreter.Evaluate(entry); }
            catch (OverflowException) { cilOverflow = true; }
            if (!cilOverflow)
                throw new Exception("Emitted helper MethodDef lost checked overflow: " + label);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // The constructor is intentionally internal. These test-only reflections
    // simulate a future internal lowering pass packaging individually verified
    // functions whose call signatures do NOT match the actual module. The
    // production compiler/emitter performs no reflection.
    private static VerifiedCfgModule ForgeUnlinkedModuleForTest(
        VerifiedCfgFunction main, IReadOnlyList<VerifiedCfgFunction> helpers)
    {
        var ctor = typeof(VerifiedCfgModule).GetConstructors(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic).Single();
        return (VerifiedCfgModule)ctor.Invoke(new object[] { main, helpers });
    }

    private static void RejectForgedModule(VerifiedCfgModule module, string label)
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "ores-static-abi-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "OresGenerated.dll");
            try
            {
                CilEmitter.EmitLibrary(module, file);
            }
            catch (FormatException)
            {
                if (File.Exists(file))
                    throw new Exception("Failed link-check emitted artifact: " + label);
                return;
            }
            throw new Exception("Inconsistent CFG MethodDef module accepted: " + label);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    public static void Run()
    {
        const string lexicalMethods =
            "fnc z_helper(int n):int{return n+1;}" +
            "fnc a_helper(int n):int{return z_helper(n)*2;}" +
            "fnc main():int{return a_helper(20);}";
        var declaredOrder = OresStructuredParser.Parse(lexicalMethods).FunctionOrder;
        if (declaredOrder is null ||
            !declaredOrder.SequenceEqual(new[] { "z_helper", "a_helper" }))
            throw new Exception("Typed function declaration order was not preserved");
        var emittedOrder = OresStructuredCfgLowering.LowerModule(lexicalMethods)
            .Helpers.Select(h => h.Name).ToArray();
        if (!emittedOrder.SequenceEqual(new[] { "z_helper", "a_helper" }))
            throw new Exception("CIL MethodDef order diverged from source declaration order");
        Check(lexicalMethods, 42, 2, "source-order MethodDef tokens must not sort helpers");
        RejectDynamicOpcodeMutation(); // mutate real emitted CIL in-memory, never ship it
        RejectSameSignatureRetargetMutation(); // same ABI, wrong method token must reject

        Check("fnc main():int{return 42;}", 42, 0, "single main remains supported");
        Check("fnc inc(int x):int{return x+1;}fnc main():int{return inc(41);}",
            42, 1, "single static helper call");
        Check("fnc twice(int n):int{return n*2;}" +
            "fnc inc(int n):int{return n+1;}" +
            "fnc main():int{return twice(inc(20));}",
            42, 2, "nested separate method tokens");
        Check("fnc zed(int n):int{return n+1;}" +
            "fnc alpha(int n):int{return n+1;}" +
            "fnc add(int a,int b):int{return a+b;}" +
            "fnc main():int{return add(zed(18),alpha(22));}",
            42, 3, "different call targets preserve left-to-right order");
        Check("fnc eq(int x,int y):bool{return x==y;}" +
            "fnc main():int{if eq(20,20){return 42;}else{return 0;}}",
            42, 1, "bool result and two parameter ABI");
        Check("fnc sum(int x,int y):int{return x+y;}" +
            "fnc main():int{return sum(19,23);}",
            42, 1, "argument order and typed signature");
        Check("fnc distinct(int a,int b):bool{return a neq b;}" +
            "fnc main():int{if distinct(1,2){return 42;}else{return 0;}}",
            42, 1, "canonical neq in a typed private static MethodDef");
        Check("fnc same(int a,int b):bool{return a eq b;}" +
            "fnc main():int{if same(2,2){return 42;}else{return 0;}}",
            42, 1, "canonical eq in a typed private static MethodDef");
        Check("fnc inc(int x):int{return x+1;}" +
            "fnc main():int{let int total=0;let int i=0;" +
            "while i<3{total=inc(total);i=i+1;}return total;}",
            3, 1, "native call inside CFG loop");
        Check("fnc unused():int{return 500;}" +
            "fnc main():int{return 42;}",
            42, 1, "declared but unreachable code preserved in module");
        CheckOverflow("fnc inc(int x):int{return x+1;}" +
            "fnc main():int{return inc(9223372036854775807);}",
            "separate helper retains overflow trap");
        Check("fnc huge(int x):int{return x+1;}" +
            "fnc main():int{if false{return huge(9223372036854775807);}return 42;}",
            42, 1, "unselected helper overflow must not run");
        Check("fnc id(bool flag):bool{return flag;}" +
            "fnc main():int{if id(true){return 42;}else{return 0;}}",
            42, 1, "bool argument and bool return method signature");

        // The maximum legal arity exercises 16 sequential pushes,
        // ldarg.s for slots 4..15, MethodDef parameter encoding, and maxstack.
        var names = Enumerable.Range(0, 16).Select(i => "a" + i).ToArray();
        var parameters16 = string.Join(",", names.Select(n => "int " + n));
        var values16 = string.Join(",", Enumerable.Range(1, 16));
        var sum16 = string.Join("+", names);
        Check("fnc sum16(" + parameters16 + "):int{return " + sum16 +
            ";}fnc main():int{return sum16(" + values16 + ");}",
            136, 1, "maximum sixteen-argument direct call");
        Reject("fnc too_many(" +
            string.Join(",", Enumerable.Range(0, 17).Select(i => "int p" + i)) +
            "):int{return 1;}fnc main():int{return 42;}",
            "seventeen-parameter helper must fail closed");

        // An independently verified caller and callee can still disagree
        // if an internal module builder wires up the wrong MethodDef. The
        // emitter must fail before touching the output file.
        var callerModule = OresStructuredCfgLowering.LowerModule(
            "fnc answer(int x):int{return x;}fnc main():int{return answer(42);}");
        var wrongParameterModule = OresStructuredCfgLowering.LowerModule(
            "fnc answer(bool flag):int{return 42;}fnc main():int{return answer(true);}");
        RejectForgedModule(ForgeUnlinkedModuleForTest(
            callerModule.Main, wrongParameterModule.Helpers),
            "mismatched packaged argument type");
        var wrongReturnModule = OresStructuredCfgLowering.LowerModule(
            "fnc answer(int x):bool{return x==42;}" +
            "fnc main():int{if answer(42){return 42;}return 0;}");
        RejectForgedModule(ForgeUnlinkedModuleForTest(
            callerModule.Main, wrongReturnModule.Helpers),
            "mismatched packaged return type");

        var cyclic = OresCfgVerifier.Verify(new CfgFunction(
            "answer", IrValueType.I64, "entry", new[] {
                new CfgBlock("entry", Array.Empty<CfgParameter>(), new[] {
                    new CfgInstruction(0, CfgOpcode.Call, IrValueType.I64,
                        Target: "answer", Arguments: Array.Empty<int>())
                }, new CfgReturn(0))
            }), new Dictionary<string, CfgCallSignature>(StringComparer.Ordinal) {
                ["answer"] = new(IrValueType.I64, Array.Empty<IrValueType>())
            });
        var zeroArgMain = OresStructuredCfgLowering.LowerModule(
            "fnc answer():int{return 42;}fnc main():int{return answer();}");
        RejectForgedModule(ForgeUnlinkedModuleForTest(
            zeroArgMain.Main, Array.AsReadOnly(new[] { cyclic })),
            "recursive MethodDef hidden in verified helper CFG");

        // Verify-call operand lists are frozen at admission; a mutating
        // caller cannot retarget a call after SSA/type checking.
        var mutableArgs = new List<int> { 0 };
        var signatures = new Dictionary<string, CfgCallSignature>(StringComparer.Ordinal)
        {
            ["callee"] = new(IrValueType.I64, new[] { IrValueType.I64 })
        };
        var graph = new CfgFunction("caller", IrValueType.I64, "entry", new[] {
            new CfgBlock("entry", Array.Empty<CfgParameter>(), new[] {
                new CfgInstruction(0, CfgOpcode.ConstantI64, IrValueType.I64,
                    I64Constant: 41),
                new CfgInstruction(1, CfgOpcode.Call, IrValueType.I64,
                    Target: "callee", Arguments: mutableArgs)
            }, new CfgReturn(1))
        });
        var verifiedCall = OresCfgVerifier.Verify(graph, signatures);
        mutableArgs[0] = 999;
        if (verifiedCall.Blocks[0].Instructions[1].Arguments![0] != 0)
            throw new Exception("Verified call retained mutable operand alias");
        try
        {
            _ = OresCfgVerifier.Verify(graph);
            throw new Exception("Standalone verifier admitted unresolved call target");
        }
        catch (FormatException) { }

        Check("fnc calculate(int x):int{" +
            "let int sum=0;let int i=0;" +
            "while i<x{sum=sum+i;i=i+1;}" +
            "return sum;}" +
            "fnc main():int{return calculate(10);}",
            45, 1, "statement helper loop with persistent locals", false);
        Check("fnc select_value(int n):int{" +
            "if n<10{return 5;}else{return 42;}}" +
            "fnc main():int{return select_value(10);}",
            42, 1, "statement helper both return branches", false);
        Check("fnc plus_one(int x):int{return x+1;}" +
            "fnc calculate(int value):int{" +
            "let int x=value;let int i=0;" +
            "while i<3{x=plus_one(x);i=i+1;}" +
            "return x;}" +
            "fnc main():int{return calculate(39);}",
            42, 2, "loop in helper calls separate earlier method", false);
        Check("fnc eligible(int n):bool{" +
            "let bool ok=n<43;return ok;}" +
            "fnc main():int{if eligible(42){return 42;}else{return 0;}}",
            42, 1, "bool-return helper local", false);
        Check("fnc unused(int n):int{" +
            "let int x=n;while x<5{x=x+1;}return x;}" +
            "fnc main():int{return 42;}",
            42, 1, "unselected statement helper remains an emitted MethodDef", false);

        Reject("fnc incomplete(int x):int{let int y=x;}" +
            "fnc main():int{return 42;}", "helper lacks return");
        Reject("fnc wrong(int n):bool{return n;}" +
            "fnc main():int{return 42;}", "typed bool helper return mismatch");
        Reject("fnc broken(int n):int{if n<1{return 1;}else{let int x=2;}}" +
            "fnc main():int{return 42;}", "one helper branch lacks return");
        Reject("fnc leaked(int n):int{if n<2{let int x=9;}return x;}" +
            "fnc main():int{return 42;}", "helper block local escaped");
        Reject("fnc bad(int x):int{let int x=1;return x;}" +
            "fnc main():int{return bad(1);}", "helper parameter shadowed");
        Reject("fnc bad(int n):int{return missing(n);}" +
            "fnc main():int{return 0;}", "hidden unverified call in unused helper");

        Reject("fnc main():int{return missing();}", "unknown function");
        Reject("fnc first():int{return second();}fnc second():int{return 1;}" +
            "fnc main():int{return first();}", "forward call");
        Reject("fnc rec():int{return rec();}fnc main():int{return 1;}",
            "recursion");
        Reject("fnc wrong(bool a):int{return 42;}fnc main():int{return wrong(3);}",
            "call argument type mismatch");
        Reject("fnc main():int{return 1;}fnc second():int{return 1;}",
            "trailing function");
        Reject("fnc inc(int x):int{return x+1;}" +
            "fnc main():int{return inc(1,2);}", "arity mismatch");
    }
}
