using Mono.Cecil;
using Ores.Net.Compiler;

internal static class StructuredCfgLoweringTests
{
    private static void Check(string source, long expected, string label)
    {
        var graph = OresStructuredCfgLowering.Lower(source);
        if (CfgReferenceEvaluator.Evaluate(graph) != expected)
            throw new Exception("Structured CFG reference mismatch: " + label);

        string folder = Path.Combine(Path.GetTempPath(),
            "ores-structured-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string output = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(graph, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            if (CfgCilTestInterpreter.Evaluate(entry) != expected)
                throw new Exception("Structured CIL mismatch: " + label);
            string second = Path.Combine(folder, "OresGenerated.second.dll");
            CilEmitter.EmitLibrary(graph, second);
            if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(
                File.ReadAllBytes(second)))
                throw new Exception("Structured CFG CIL is not deterministic: " + label);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Both the independently verified CFG evaluator and emitted CIL must
    // actually trap on a reachable checked-i64 overflow. Reference-only
    // testing cannot prove the native-code input retains checked opcodes.
    private static void CheckOverflow(string source, string label)
    {
        var graph = OresStructuredCfgLowering.Lower(source);
        bool referenceTrapped = false;
        try { _ = CfgReferenceEvaluator.Evaluate(graph); }
        catch (OverflowException) { referenceTrapped = true; }
        if (!referenceTrapped)
            throw new Exception("Structured CFG missed overflow: " + label);

        string folder = Path.Combine(Path.GetTempPath(),
            "ores-structured-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string output = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(graph, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var method = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            bool cilTrapped = false;
            try { _ = CfgCilTestInterpreter.Evaluate(method); }
            catch (OverflowException) { cilTrapped = true; }
            if (!cilTrapped)
                throw new Exception("Structured CIL lost checked overflow: " + label);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void Reject(string source, string label)
    {
        try { _ = OresStructuredCfgLowering.Lower(source); }
        catch (FormatException) { return; }
        throw new Exception("Invalid structured source accepted: " + label);
    }

    public static void Run()
    {
        const string sum = """
            define pub fnc main():int {
                let int total=0;
                let int i=0;
                while i<10 {
                    total=total+i;
                    i=i+1;
                }
                return total;
            }
            """;
        Check(sum, 45, "two loop-carried SSA values sum 0..9");
        // Actual typed source-call semantics are checked in the independent
        // reference evaluator, emitted CIL interpreter and deterministic PE.
        // This is an acyclic, expression-only helper milestone, not a
        // general function ABI or runtime call instruction.
        Check("fnc plus_one(int x):int{return x+1;}fnc main():int{return plus_one(41);}",
            42, "one typed i64 helper");
        Check("fnc twice(int x):int{return x*2;}fnc answer():int{return twice(21);}fnc main():int{return answer();}",
            42, "chained helper functions");
        Check("fnc sum(int x,int y):int{return x+y;}fnc main():int{return sum(19,23);}",
            42, "two parameter bindings in source order");
        Check("fnc is_five(int x):bool{return x==5;}fnc main():int{if is_five(5){return 42;}else{return 0;}}",
            42, "typed boolean result controls branch");
        Check("fnc plus_one(int x):int{return x+1;}fnc main():int{let int x=99;return plus_one(41);}",
            42, "callee cannot capture caller local");
        Check("fnc from_constant():int{return 42;}fnc main():int{return from_constant();}",
            42, "zero argument call");
        Check("fnc plus_one(int x):int{return x+1;}fnc main():int{let int result=0;let int i=0;while i<3{result=plus_one(result);i=i+1;}return result;}",
            3, "helper calls inside loop body");
        Check("fnc add(int x,int y):int{return x+y;}fnc main():int{return add(10,add(20,12));}",
            42, "nested call evaluation");

        // Negative tests must reject before CIL artifact creation.
        Reject("fnc main():int{return unknown(42);}", "unknown helper");
        Reject("fnc main():int{return plus_one(41);}fnc plus_one(int x):int{return x+1;}",
            "forward helper calls disabled");
        Reject("fnc recursive(int x):int{return recursive(x);}fnc main():int{return recursive(1);}",
            "recursive helper calls disabled");
        Reject("fnc loop(int x):int{return loop(x);}fnc main():int{return 0;}",
            "unreachable recursive helper still denied");
        Reject("fnc add(int x,int y):int{return x+y;}fnc main():int{return add(1);}",
            "arity mismatch");
        Reject("fnc is_true(bool ok):bool{return ok;}fnc main():int{if is_true(1){return 1;}return 0;}",
            "typed argument mismatch");
        Reject("fnc bad(int x,int x):int{return x;}fnc main():int{return 0;}",
            "duplicate parameter");
        Reject("fnc wrong(int x):bool{return x;}fnc main():int{return 0;}",
            "wrong return type");
        Reject("fnc bad(int x):int{let int y=x;return y;}fnc main():int{return bad(1);}",
            "helper statement bodies not yet supported");
        Reject("fnc same():int{return 1;}fnc same():int{return 2;}fnc main():int{return 0;}",
            "duplicate helper declarations");
        CheckOverflow("fnc inc(int x):int{return x+1;}fnc main():int{return inc(9223372036854775807);}",
            "typed call overflow preserves checked CIL");


        Check("fnc main():int{return 2+3*4;}", 14, "arithmetic");
        Check("fnc main():int{let int i=0;while i<10 {i=i+1;}return i;}",
            10, "single loop-carried variable");
        Check("fnc main():int{let int x=1;if x<2{x=x+2;}else{x=x+100;}return x;}",
            3, "diamond join");
        Check("fnc main():int{let int x=5;if x<2{x=x+2;}else{x=x+100;}return x;}",
            105, "diamond else branch");
        Check("fnc main():int{let int x=5;if false{x=9;}return x;}",
            5, "implicit else preserves value");
        Check("fnc main():int{let int x=5;if true{x=9;}return x;}",
            9, "implicit else joins updated value");
        Check("fnc main():int{if true{return 11;}else{return 22;}}",
            11, "both branches return");
        Check("fnc main():int{if false{return 11;}else{return 22;}}",
            22, "both branches return false");
        Check("fnc main():int{let int x=2;if x<3{return x;}return 99;}",
            2, "early return in conditional");
        Check("fnc main():int{let int x=2;if x<1{return x;}return 99;}",
            99, "fallthrough from conditional");
        Check("fnc main():int{let int x=1;if true{let int y=7;x=x+y;}return x;}",
            8, "block local plus enclosing write");
        Check("fnc main():int{if false{let int x=1;}else{let int x=2;}return 3;}",
            3, "sibling block local names");
        Check("fnc main():int{let bool ok=true;let int n=0;while ok{n=n+1;ok=n<3;}return n;}",
            3, "bool loop-carried binding");
        Check("fnc main():int{let int n=0;while n<5{if n==3{return n;}n=n+1;}return 99;}",
            3, "nested branch return inside loop");
        Check("fnc main():int{return -9223372036854775808;}",
            long.MinValue, "signed minimum");
        Check("fnc main():int{let int n=-1;if n<0{return 1;}return 2;}",
            1, "signed negative comparison");
        Check("fnc main():int{if true{return 7;}else{return -(-9223372036854775808);}}",
            7, "unselected else must not execute checked-overflow trap");
        Check("fnc main():int{if false{return -(-9223372036854775808);}else{return 8;}}",
            8, "unselected then must not execute checked-overflow trap");

        Reject("fnc main():int{let int x=1;while true {let int x=2;}return x;}",
            "nested shadowing must not bypass lowering");
        Reject("fnc main():int{let int x=x;return x;}",
            "no self-reference");
        Reject("fnc main():int{if true{return 1;}}",
            "all source paths must return");
        Reject("fnc main():int{return 1/2;}",
            "source int division requires float lowering");

        CheckOverflow("fnc main():int{return -(-9223372036854775808);}",
            "checked negation");
        CheckOverflow("fnc main():int{return 9223372036854775807+1;}",
            "checked addition");
        CheckOverflow("fnc main():int{return 9223372036854775807*2;}",
            "checked multiplication");
        CheckOverflow("fnc main():int{let int x=9223372036854775807;while x==9223372036854775807{x=x+1;}return x;}",
            "checked overflow inside loop");
        Console.WriteLine("Structured source to verified SSA CFG and CIL passed");
    }
}
