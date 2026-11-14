using Mono.Cecil;
using Ores.Net.Compiler;

// Tests the same LowerModule -> CIL MethodDef path used by the real
// compile-structured NativeAOT frontend, not the inline-only Lower() oracle.
internal static class StructuredCfgModuleParityTests
{
    private static void Check(string source, long expected, string label)
    {
        var module = OresStructuredCfgLowering.LowerModule(source);
        if (module.Helpers.Count == 0)
            throw new Exception("Module parity probe has no independent helper: " + label);
        string folder = Path.Combine(Path.GetTempPath(),
            "ores-logical-methoddef-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string output = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(module, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var type = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram");
            var entry = type.Methods.Single(m => m.Name == "Run");
            if (type.Methods.Count(m =>
                m.Name.StartsWith("helper_", StringComparison.Ordinal)) != module.Helpers.Count)
                throw new Exception("Module MethodDef boundary was lost: " + label);
            if (CfgCilTestInterpreter.Evaluate(entry) != expected)
                throw new Exception("Module MethodDef CIL logical mismatch: " + label);
            string second = Path.Combine(folder, "OresGenerated.second.dll");
            CilEmitter.EmitLibrary(module, second);
            if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(File.ReadAllBytes(second)))
                throw new Exception("Module MethodDef CIL not deterministic: " + label);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void CheckOverflow(string source, string label)
    {
        var module = OresStructuredCfgLowering.LowerModule(source);
        string folder = Path.Combine(Path.GetTempPath(),
            "ores-logical-methoddef-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string output = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(module, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            bool trapped = false;
            try { _ = CfgCilTestInterpreter.Evaluate(entry); }
            catch (OverflowException) { trapped = true; }
            if (!trapped)
                throw new Exception("Eager MethodDef RHS did not overflow: " + label);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    public static void Run()
    {
        const string fixture = "contracts/logical-word-operators-v1/module-methoddef.atto";
        Check(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fixture)),
            42, "pinned MethodDef logical guest matches native execution probe");

        // Exhaust the two-operand bool truth tables through independent CIL
        // MethodDefs and branch-sensitive SSA joining, not an AST simulation.
        foreach (bool lhs in new[] { false, true })
        foreach (bool rhs in new[] { false, true })
        foreach (string op in new[] { "and", "or", "xor" })
        {
            bool result = op switch
            {
                "and" => lhs && rhs,
                "or" => lhs || rhs,
                "xor" => lhs != rhs,
                _ => throw new Exception("Invalid test operator")
            };
            string source =
                "fnc combine(bool a,bool b):bool{return a " + op + " b;}" +
                "fnc main():int{return combine(" +
                (lhs ? "true" : "false") + "," + (rhs ? "true" : "false") +
                ") ? 42 : 7;}";
            Check(source, result ? 42 : 7,
                "MethodDef bool " + op + " truth table " + lhs + "/" + rhs);
        }

        Check("fnc explode():bool{return 9223372036854775807+1>0;}" +
              "fnc main():int{if false and explode(){return 7;}else{return 42;}}",
            42, "and skips overflowing helper");
        Check("fnc explode():bool{return 9223372036854775807+1>0;}" +
              "fnc main():int{if true or explode(){return 42;}else{return 7;}}",
            42, "or skips overflowing helper");
        CheckOverflow("fnc explode():bool{return 9223372036854775807+1>0;}" +
              "fnc main():int{if false xor explode(){return 42;}else{return 7;}}",
            "xor evaluates helper with false LHS");
        CheckOverflow("fnc explode():bool{return 9223372036854775807+1>0;}" +
              "fnc main():int{if true xor explode(){return 7;}else{return 42;}}",
            "xor evaluates helper with true LHS");
        Check("fnc explode():int{return 9223372036854775807+1;}" +
              "fnc main():int{return true ? 42 : explode();}",
            42, "conditional skips false-arm helper");
        Check("fnc explode():int{return 9223372036854775807+1;}" +
              "fnc main():int{return false ? explode() : 42;}",
            42, "conditional skips true-arm helper");
        Check("fnc decide(bool a,bool b):bool{let bool x=a and b;" +
              "if x or a{return true;}else{return false;}}" +
              "fnc main():int{return decide(true,false) ? 42 : 7;}",
            42, "statement-bodied helper and bool branches");
        Check("fnc identity(bool b):bool{return b;}" +
              "fnc main():int{return identity(true or false and false) ? 42 : 7;}",
            42, "logical expression inside helper call argument");
        Check("fnc answer(int n):int{let int result=7;let int i=n;" +
              "while i<2 and (i<3 or false){result=result+17;i=i+1;}" +
              "return result+1;}" +
              "fnc main():int{return answer(0);}",
            42, "MethodDef loop-header condition and SSA backedges");
    }
}
