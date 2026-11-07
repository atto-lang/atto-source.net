using Mono.Cecil;
using Mono.Cecil.Cil;
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

    // Inspect emitted PE opcodes: unsigned CIL comparisons can pass positive-only tests.
    // This is an IL-shape gate, separate from reference and interpreted execution.
    private static void CheckSignedOpcode(string comparison, Code expected, bool inverted)
    {
        var graph = OresStructuredCfgLowering.Lower(
            "fnc main():int{if " + comparison + "{return 42;}else{return 0;}}");
        string folder = Path.Combine(Path.GetTempPath(),
            "ores-relational-il-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(graph, path);
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            var run = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            var codes = run.Body.Instructions.Select(i => i.OpCode.Code).ToArray();
            int index = Array.IndexOf(codes, expected);
            bool hasInversion = index >= 0 && index + 2 < codes.Length &&
                codes[index + 1] == Code.Ldc_I4_0 && codes[index + 2] == Code.Ceq;
            int relationalOps = codes.Count(c => c is Code.Clt or Code.Cgt or
                Code.Clt_Un or Code.Cgt_Un);
            if (index < 0 || relationalOps != 1 || hasInversion != inverted ||
                codes.Contains(Code.Clt_Un) || codes.Contains(Code.Cgt_Un))
                throw new Exception("Unsigned/malformed comparison CIL: " + comparison);
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
        // Signed relational source must execute through verified CFG and
        // emitted CIL; parser-only acceptance is not parity evidence.
        Check(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "structured-signed-comparisons.atto")), 42,
            "canonical .atto signed comparisons and typed helper");
        foreach (var (comparison, result) in new (string, bool)[] {
            ("-7 > -8", true), ("-8 > -7", false),
            ("7 >= 7", true), ("7 >= 8", false),
            ("7 <= 7", true), ("8 <= 7", false),
            ("-9223372036854775808 > -9223372036854775808", false),
            ("9223372036854775807 > -9223372036854775808", true),
            ("-9223372036854775808 <= -9223372036854775808", true),
            ("9223372036854775807 >= 9223372036854775807", true),
            ("-9223372036854775808 >= 9223372036854775807", false),
            ("9223372036854775807 <= -9223372036854775808", false)
        })
            Check("fnc main():int{if " + comparison +
                "{return 42;}else{return 0;}}", result ? 42 : 0,
                "signed relational CIL: " + comparison);
        Check("fnc main():int{const bool within = -9223372036854775808 <= " +
            "9223372036854775807;if within{return 42;}return 0;}", 42,
            "compile-time bool relational evaluation");
        CheckSignedOpcode("-9223372036854775808 < 0", Code.Clt, false);
        CheckSignedOpcode("-9223372036854775808 > 0", Code.Cgt, false);
        CheckSignedOpcode("-9223372036854775808 <= 0", Code.Cgt, true);
        CheckSignedOpcode("-9223372036854775808 >= 0", Code.Clt, true);
        Check("fnc main():int{let int lhs=-8;val int rhs=-7;if lhs<=rhs{return 42;}return 0;}",
            42, "runtime local signed comparison");
        Check("fnc main():int{let int x=0;while x<3{x=x+1;}if x>=3{return 42;}return 0;}",
            42, "relational operator after loop SSA join");
        Check("fnc main():int{const bool no=7>8;if no{return 0;}return 42;}",
            42, "compile-time false signed relation");
        Check("fnc main():int{if false{if (9223372036854775807+1)>=0{return 0;}}return 42;}",
            42, "unselected signed comparison cannot overflow");
        CheckOverflow("fnc main():int{if 0<=(9223372036854775807+1){return 1;}return 0;}",
            "right signed-comparison operand overflow");

        CheckOverflow(
            "fnc main():int{if (9223372036854775807+1)>=0{return 1;}return 0;}",
            "relational operand overflow must not wrap or be skipped");
        Reject("fnc main():int{if true > 1{return 42;}return 0;}",
            "bool/int greater-than type mismatch");
        Reject("fnc main():int{if 1 <= false{return 42;}return 0;}",
            "int/bool less-than-or-equal type mismatch");
        Reject("fnc main():int{if 1 < 2 > 0{return 42;}return 0;}",
            "relational chaining cannot coerce bool to int");
        Reject("fnc main():int{if 1 => 0{return 42;}return 0;}",
            "invalid arrow comparison");
        Reject("fnc main():int{if 1 =< 0{return 42;}return 0;}",
            "invalid reverse comparison");
        Check(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "structured-bindings42.ores")),
            42, "real const/val/let source through verified CFG and CIL");
        Check("fnc main():int{const int c=40+2;return c;}",
            42, "compile-time const typed SSA");
        Check("fnc main():int{let int x=5;val int y=x+1;x=x+1;return y;}",
            6, "runtime val has independent SSA identity");
        Check("fnc main():int{const bool b=2<3;if b{return 42;}else{return 0;}}",
            42, "compile-time boolean branch in emitted CIL");
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
            "legacy inlining oracle rejects statement bodies (module path has real MethodDef support)");
        Reject("fnc same():int{return 1;}fnc same():int{return 2;}fnc main():int{return 0;}",
            "duplicate helper declarations");
        CheckOverflow("fnc inc(int x):int{return x+1;}fnc main():int{return inc(9223372036854775807);}",
            "typed call overflow preserves checked CIL");

        Check("fnc main():int{if 3 neq 4{return 42;}else{return 0;}}",
            42, "canonical neq true comparison");
        Check("fnc main():int{if 4 neq 4{return 0;}else{return 42;}}",
            42, "canonical neq false comparison");
        Check("fnc main():int{if -5 neq 5{return 42;}return 0;}",
            42, "signed neq operands");
        Check("fnc main():int{if 3 != 4{return 42;}return 0;}",
            42, "symbolic not-equal alias");
        Check("fnc main():int{if 4 eq 4{return 42;}return 0;}",
            42, "canonical eq comparison");
        Check("fnc main():int{if 4 == 4{return 42;}return 0;}",
            42, "symbolic equality retains existing semantics");
        Reject("fnc main():int{if 1 ne 2{return 42;}return 0;}",
            "ne is not an Oreslang inequality operator");
        Reject("fnc main():int{if true neq false{return 42;}return 0;}",
            "i64 comparison must not accept bool operands");
        Reject("fnc main():int{if 1 ! 2{return 42;}return 0;}",
            "bare exclamation is not an admitted operator");
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

        Reject("fnc main():int{val int x=1;x=2;return x;}", "val reassignment");
        Reject("fnc main():int{const int x=1;x=2;return x;}", "const reassignment");
        Reject("fnc main():int{val int x=1;if true{x=2;}return x;}",
            "val cannot be mutated inside nested block");
        Reject("fnc main():int{let int x=42;const int y=x;return y;}",
            "const cannot capture mutable binding");
        Reject("fnc main():int{val int x=42;const int y=x;return y;}",
            "const cannot depend on runtime val");
        Reject("fnc main():int{const int x=9223372036854775807+1;return x;}",
            "const overflow is rejected before CIL");

        CheckOverflow("fnc main():int{return -(-9223372036854775808);}",
            "checked negation");
        CheckOverflow("fnc main():int{val int n=9223372036854775807;return n+1;}",
            "runtime val checked overflow in emitted CIL");
        CheckOverflow("fnc main():int{return 9223372036854775807+1;}",
            "checked addition");
        CheckOverflow("fnc main():int{return 9223372036854775807*2;}",
            "checked multiplication");
        CheckOverflow("fnc main():int{return -9223372036854775808-1;}",
            "checked subtract minimum");
        CheckOverflow(
            "fnc main():int{let int x=9223372036854775807;if true{x=x+1;}return x;}",
            "overflow through structured if/SSA join");
        CheckOverflow("fnc main():int{let int x=9223372036854775807;while x==9223372036854775807{x=x+1;}return x;}",
            "checked overflow inside loop");
        Console.WriteLine("Structured source to verified SSA CFG and CIL passed");
    }
}
