using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

// Strictly experimental, separately compiled MethodDef ABI proof.
// A second source/inlined CFG interpreter supplies the expected result while
// the independent CIL test interpreter actually follows call MethodDef tokens.
internal static class StructuredStaticAbiTests
{
    private static void Check(string source, long expected, int helperCount, string label)
    {
        var module = OresStructuredCfgLowering.LowerModule(source);
        var inline = OresStructuredCfgLowering.Lower(source);
        if (CfgReferenceEvaluator.Evaluate(inline) != expected)
            throw new Exception("Structured inline reference disagreement: " + label);
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
            int calls = type.Methods.Sum(m =>
                m.Body.Instructions.Count(i => i.OpCode.Code == Code.Call));
            if (helperCount > 0 && calls == 0)
                throw new Exception("Source helpers were flattened instead of called: " + label);
            foreach (var helper in type.Methods.Where(m => m != run))
            {
                if (!helper.IsPrivate || !helper.IsStatic ||
                    !helper.Name.StartsWith("helper_", StringComparison.Ordinal))
                    throw new Exception("Helper escaped private static MethodDef ABI: " + label);
                if (!helper.Body.Instructions.Any(i =>
                    i.OpCode.Code is Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2
                        or Code.Ldarg_3 or Code.Ldarg or Code.Ldarg_S) &&
                    helper.Parameters.Count > 0)
                    throw new Exception("Helper was not compiled with ldarg: " + label);
            }
            foreach (var method in type.Methods)
                foreach (var instruction in method.Body.Instructions)
                    if (instruction.OpCode.Code is Code.Calli or Code.Callvirt
                        or Code.Ldftn or Code.Newobj)
                        throw new Exception("Dynamic dispatch escaped static ABI: " + label);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

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

    public static void Run()
    {
        Check("fnc main():int{return 42;}", 42, 0, "single main remains supported");
        Check("fnc inc(int x):int{return x+1;}fnc main():int{return inc(41);}",
            42, 1, "single static helper call");
        Check("fnc twice(int n):int{return n*2;}" +
            "fnc inc(int n):int{return n+1;}" +
            "fnc main():int{return twice(inc(20));}",
            42, 2, "nested separate method tokens");
        Check("fnc eq(int x,int y):bool{return x==y;}" +
            "fnc main():int{if eq(20,20){return 42;}else{return 0;}}",
            42, 1, "bool result and two parameter ABI");
        Check("fnc sum(int x,int y):int{return x+y;}" +
            "fnc main():int{return sum(19,23);}",
            42, 1, "argument order and typed signature");
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
