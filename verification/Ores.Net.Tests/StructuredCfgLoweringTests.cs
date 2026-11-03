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
        Check(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "structured-bindings42.ores")),
            42, "real const/val/let source through verified CFG and CIL");
        // Real Oreslang source -> static closed-row typecheck -> verified CFG
        // -> deterministic emitted CIL. No managed aggregate object is emitted.
        Check("fnc main():int{const base=infer struct{x:20};const next=infer struct{...base,y:23};return next.x+next.y;}",
            43, "inferred struct spread to emitted CIL");
        Check("fnc main():int{const base=infer record{x:20};const next=infer record{...base,y:24};return next.x+next.y;}",
            44, "inferred readonly record spread to emitted CIL");
        Check("fnc main():int{const a=record{x:20};const b=record{...a,x:45};return b.x-a.x+20;}",
            45, "record override does not mutate original");
        Check("fnc main():int{const a=infer struct{active:true,score:44};const b=infer struct{...a,active:2};return b.active+b.score;}",
            46, "overridden field's inferred type changes bool to int");
        Check("fnc main():int{const a=infer struct{active:true,value:47};if a.active{return a.value;}else{return 0;}}",
            47, "typed bool field projects into scalar CFG");
        Reject("fnc main():int{const a=infer struct{...missing,x:1};return a.x;}",
            "dynamic or missing spread source rejected");
        Reject("fnc main():int{const a=infer struct{x:1,x:2};return a.x;}",
            "duplicate explicit aggregate fields rejected");
        Reject("fnc main():int{const a=infer record{x:1};return a;}",
            "compile-time aggregate cannot escape as scalar");
        Reject("fnc main():int{const a=record{foo:2};const b=record{...a,foo:true};return b.foo;}",
            "record bool cannot return as int");
        // Runtime-valued immutable rows snapshot scalars into private verified
        // SSA places: later mutation of the original local cannot change them.
        Check("fnc main():int{let int source=20;val row=infer struct{x:source,y:28};source=99;return row.x+row.y;}",
            48, "runtime val aggregate snapshot survives source reassignment");
        Check("fnc main():int{val int seed=20;val a=record{x:seed};val b=record{...a,y:29};return b.x+b.y;}",
            49, "runtime readonly record spread into immutable result");
        Check("fnc main():int{const a=record{x:20};val b=record{...a,x:40+10};return b.x;}",
            50, "runtime val spread from compile-time const row");
        Check("fnc main():int{val row=infer struct{x:true,y:49};val update=infer struct{...row,x:2};return update.x+update.y;}",
            51, "runtime val type-changing scalar override");
        Check("fnc twice(int n):int{return n*2;}fnc main():int{val row=infer record{x:twice(26)};return row.x;}",
            52, "typed helper call evaluated by emitted CIL in row construction");
        Check("fnc main():int{let int source=20;val original=record{x:source};val changed=record{...original,x:53};source=99;return changed.x-original.x+20;}",
            53, "runtime spread never aliases or mutates original owner");
        Reject("fnc main():int{let int seed=20;val runtime=record{x:seed};const illegal=record{...runtime,y:22};return illegal.x+illegal.y;}",
            "compile-time const row cannot spread a runtime val row");
        Reject("fnc main():int{let int value=41;const row=infer struct{x:value};return row.x;}",
            "const aggregate cannot capture runtime let");
        Reject("fnc main():int{val row=infer record{...missing,x:42};return row.x;}",
            "runtime spread from unknown source fails before CIL");
        Reject("fnc main():int{val row=infer struct{x:1,x:2};return row.x;}",
            "runtime row duplicate explicit key fails");
        Reject("fnc main():int{val row=infer record{x:42};return row;}",
            "runtime aggregate value cannot escape as scalar");
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
