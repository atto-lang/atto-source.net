using Mono.Cecil;
using System.Security.Cryptography;
using System.Text;
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

    private static void PinnedFiveBackendCastGuests()
    {
        // Same exact .ores source files as the Java/LLVM/Wasm/Cranelift
        // candidates. CFG evaluation, emitted CIL interpretation and
        // deterministic assembly bytes are separate from NativeAOT execution.
        (string File, string GitBlob)[] fixtures =
        {
            ("45-cast-int-literal.ores", "69cecd231d32bf70ea36424e59c932861d01fe44"),
            ("46-chained-cast-int.ores", "b088dd3f5e3114be4affbb4935582b58f010d75f"),
            ("47-grouped-cast-multiply.ores", "9ebc3f7bc230557fe2f2639172883bb2f4589464"),
            ("48-readonly-local-cast.ores", "87d0773504e0d22fc3cf1ee76755b36a2fc415c6"),
            ("49-const-typed-identity.ores", "bff7c6070008c5c6a8b3316e8a289f93d3f8c5b3")
        };
        foreach (var fixture in fixtures)
        {
            string file = Path.Combine(AppContext.BaseDirectory, "parity", fixture.File);
            byte[] sourceBytes = File.ReadAllBytes(file);
            if (sourceBytes.Length == 0 || sourceBytes.Length >= 8192)
                throw new Exception("Invalid pinned Oreslang guest size: " + fixture.File);
            byte[] header = Encoding.ASCII.GetBytes("blob " + sourceBytes.Length);
            byte[] objectBytes = new byte[header.Length + 1 + sourceBytes.Length];
            header.CopyTo(objectBytes, 0);
            sourceBytes.CopyTo(objectBytes, header.Length + 1);
            string actualBlob = Convert.ToHexString(SHA1.HashData(objectBytes)).ToLowerInvariant();
            if (actualBlob != fixture.GitBlob)
                throw new Exception("Oreslang source-byte drift: " + fixture.File);
            Check(Encoding.UTF8.GetString(sourceBytes), 42,
                "shared native i64 cast fixture " + fixture.File);
        }
    }

    public static void Run()
    {
        PinnedFiveBackendCastGuests();
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
        // Genuine type-first readonly bindings in the EXPERIMENTAL structured
        // source -> verified CFG -> deterministic CIL interpreter path.
        // This is not a released Windows/Linux Native AOT guest receipt.
        foreach (var (source, expected, label) in new[] {
            ("fnc main():int{int x=7;return x;}", 7L, "readonly int literal"),
            ("fnc main():int{const x=7;return x;}", 7L, "existing constexpr"),
            ("fnc main():int{const int x=7;return x;}", 7L, "typed constexpr"),
            ("fnc main():int{const x:int=7;return x;}", 7L, "name-first constexpr"),
            ("fnc main():int{int x=7;int y=x*3;return y;}", 21L, "readonly SSA dependencies"),
            ("fnc main():int{int x=7;return x+1;}", 8L, "readonly scalar read"),
            ("fnc main():int{bool yes=true;if yes{return 42;}else{return 0;}}",
                42L, "readonly bool flow"),
            ("fnc main():int{let int x=7;x=8;return x;}", 8L, "existing explicit let")
        }) Check(source, expected, label);
        foreach (var (source, label) in new[] {
            ("fnc main():int{int x=7;x=8;return x;}", "readonly rebinding"),
            ("fnc main():int{int x=7;int x=8;return x;}", "duplicate local"),
            ("fnc main():int{int x=7;return x+undefined_name;}", "unknown name"),
            ("fnc main():int{int x=x+1;return x;}", "self initializer"),
            ("fnc main():int{int x=true;return x;}", "typed int/bool mismatch"),
            ("fnc main():int{bool yes=7;return 42;}", "typed bool/int mismatch"),
            ("fnc main():int{int x;return 42;}", "initializer mandatory")
        }) Reject(source, label);
        CheckOverflow(
            "fnc main():int{int unused=9223372036854775807+1;return 7;}",
            "unused readonly runtime initializer still traps");
        // The input is unavailable at compile time inside the helper.
        // Runtime-readonly must remain a nonconstant local while CIL retains
        // the helper argument and checked arithmetic, not fold to 42.
        Check("fnc plus_one(int p):int{int local=p+1;return local;}" +
                "fnc main():int{return plus_one(41);}",
            42, "runtime readonly helper argument survives CFG/CIL");
        Reject("fnc plus_one(int p):int{int local=p+1;local=0;return local;}" +
                "fnc main():int{return plus_one(41);}",
            "runtime readonly helper local cannot be reassigned");



        Check(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "structured-bindings42.ores")),
            42, "real const/val/let source through verified CFG and CIL");
        Check("fnc main():int{const int c=40+2;return c;}",
            42, "compile-time const typed SSA");
        Check(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "scalar-cast-inference-five-backend.ores")),
            42, "byte-identical five-backend immutable/inference/cast core");
        // Sources are copied verbatim from the five-compiler Git blob contract.
        // Check compiles and independently interprets actual emitted CIL;
        // a rejected source must never reach PE publication.
        Check(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "portable-checked-cast-positive-v2.ores")),
            42, "exact-byte identity-cast guest shared with Java LLVM Wasm Cranelift");
        Reject(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "portable-checked-cast-negative-v2.ores")),
            "exact-byte impossible int-to-bool cast may not reach CIL");
        Check("fnc main():int{bool flag=true;if flag{return 42;}else{return 0;}}",
            42, "type-first bool is immutable typed SSA");
        Check("fnc main():int{int x=(2+5) as int;return x+35;}",
            42, "type-first int with checked identity cast");
        Check("fnc plus(int n):int{return n+1;}fnc main():int{int x=plus(41);return x;}",
            42, "type-first immutable runtime helper result");
        Reject("fnc main():int{int x=7;x=8;return x;}",
            "type-first int binding is immutable by default");
        Reject("fnc main():int{int x=true;return 0;}",
            "type-first int initializer type mismatch");
        Reject("fnc main():int{bool x=7;return 0;}",
            "type-first bool initializer type mismatch");

        // Compiler-backed .NET evidence: each source passes typed admission,
        // independent CFG reference evaluation, actual CIL interpretation,
        // and deterministic PE byte reproduction.
        foreach (var (source, label) in new[] {
            ("fnc main():int{const int x=7;const y=7 as int;return x+y+28;}", "explicit-annotation equals inferred checked cast"),
            ("fnc main():int{const x=7;const y=x as int;return y+35;}", "unannotated int inference"),
            ("fnc main():int{const x:int=7;return x+35;}", "name-first typed annotation"),
            ("fnc main():int{const x=(2+5) as int;return x+35;}", "parenthesized additive cast"),
            ("fnc main():int{const x=7 as int as int;return x+35;}", "chained identity proofs"),
            ("fnc main():int{const yes=true as bool;if yes{return 42;}else{return 0;}}", "bool inference never int"),
            ("fnc main():int{const yes=(6*7 eq 42) as bool;if yes{return 42;}else{return 0;}}", "bool comparison checked cast"),
            ("fnc main():int{let x=7 as int;x=42;return x;}", "explicit let permits rebinding"),
            ("fnc main():int{let int x=40;const y=x;return y+2;}", "const runtime initializer is fixed binding"),
            ("fnc plus(int n):int{return n+1;}fnc main():int{const x=plus(41) as int;return x;}", "checked cast of function result"),
            ("fnc plus(int n):int{return n+1;}fnc main():int{const x=plus(41);return x;}", "inferred function result without cast"),
            ("fnc main():int{const x=7;if true{const z=x as int;return z+35;}else{return 0;}}", "scope-local inference"),
            ("fnc main():int{const x=true as bool as bool;if x{return 42;}else{return 0;}}", "chained bool identity casts"),
            ("fnc main():int{val x=7;const y=x as int;return y+35;}", "inferred val preserves int"),
            ("fnc main():int{let x=7;x=42;return x;}", "inferred let preserves mutable int"),
            ("fnc main():int{let x=false;x=true;if x{return 42;}else{return 0;}}", "inferred mutable bool"),
            ("fnc yes():bool{return true;}fnc main():int{const x=yes();if x{return 42;}else{return 0;}}", "inferred bool helper result"),
            ("fnc yes():bool{return true;}fnc main():int{const x=yes() as bool;if x{return 42;}else{return 0;}}", "checked bool helper result"),
            ("fnc seven():int{return 7 as int;}fnc main():int{return seven()+35;}", "checked identity cast at helper return"),
            ("fnc main():int{const a=7;const b=a;const c=b as int;return c+35;}", "multi-hop int inference"),
            ("fnc main():int{const a=true;const b=a;const c=b as bool;if c{return 42;}else{return 0;}}", "multi-hop bool inference"),
            ("fnc main():int{const x=(-9223372036854775808) as int;if x<0{return 42;}else{return 0;}}", "checked min-i64 identity cast"),
            ("fnc main():int{const x=9223372036854775807 as int;if 0<x{return 42;}else{return 0;}}", "checked max-i64 identity cast"),
            ("fnc main():int{const x:bool=true as bool;if x{return 42;}else{return 0;}}", "name-first bool annotation with identity cast"),
            ("fnc main():int{return (7 as int)+(35 as int);}", "two checked identity operands"),
            ("fnc main():int{return ((2+5) as int)*6;}", "cast binds before multiplication"),
            ("fnc main():int{return (7 as int as int)*6;}", "chained cast binds before multiplication"),
            ("fnc main():int{const x=7 as int;const y=x as int;return y*6;}", "checked cast through inferred constant reference"),
            ("fnc main():int{let x=7;x=x+1;x=x+34;return x;}", "SSA let multiwrite matches Cranelift LLVM"),
            ("fnc main():int{let x=7;const snap=x;x=42;return snap+35;}", "const captures prior SSA version"),
            ("fnc main():int{let x=7;val snap=x;x=42;return snap+35;}", "val captures prior SSA version"),
            ("fnc main():int{let x=7;const snap=x as int;x=42;return snap+35;}", "checked cast snapshot cannot alias mutable local"),
            ("fnc main():int{let x=7 as int;x=(x+35) as int;return x;}", "cast applied to mutable SSA replacement"),
            ("fnc main():int{const base=7;let x=base;x=42;return x;}", "mutable sourced from immutable const")
        })
            Check(source, 42, "scalar cast/inference " + label);
        foreach (var (source, label) in new[] {
            ("fnc main():int{const x=7 as bool;return 42;}", "int-to-bool cast"),
            ("fnc main():int{const x=true as int;return 42;}", "bool-to-int cast"),
            ("fnc main():int{const x=7 as? int;return 42;}", "optional cast without tagged Option lowering"),
            ("fnc main():int{const x=7 as Phantom;return 42;}", "nominal cast without runtime tag"),
            ("fnc main():int{const x=true;return x+1;}", "inferred bool arithmetic"),
            ("fnc main():int{const x=7 as int;x=8;return 42;}", "cast does not grant mutation"),
            ("fnc main():int{const x=false as int;return 0;}", "false cannot cast to int"),
            ("fnc main():int{const x=0 as bool;return 0;}", "zero cannot cast to bool"),
            ("fnc main():int{const x=(2+5) as bool;return 0;}", "int expression cannot cast to bool"),
            ("fnc main():int{const x=(7 eq 7) as int;return 0;}", "bool expression cannot cast to int"),
            ("fnc yes():bool{return true;}fnc main():int{const x=yes() as int;return 0;}", "bool helper cannot cast to int"),
            ("fnc seven():int{return 7;}fnc main():int{const x=seven() as bool;return 0;}", "int helper cannot cast to bool"),
            ("fnc main():int{const x=true as? bool;return 0;}", "optional bool cast lacks Option lowering"),
            ("fnc main():int{let x=true;x=7;return 0;}", "inferred bool let rejects int write"),
            ("fnc main():int{let x=7;x=false;return 0;}", "inferred int let rejects bool write"),
            ("fnc main():int{const x=true as bool as int;return 0;}", "identity chain cannot cross scalar types"),
            ("fnc main():int{const x=7;const y=x as bool;return 0;}", "inferred int variable cannot cast to bool"),
            ("fnc main():int{return (7 as int) as bool;}", "nested int cast may not reinterpret bool"),
            ("fnc main():int{return (false as bool) as int;}", "nested bool cast may not reinterpret int"),
            ("fnc main():int{return (7 as int) as? int;}", "nested optional cast requires tagged Option"),
            ("fnc main():int{return (7 as int)+true;}", "identity cast does not permit bool arithmetic")
        })
            Reject(source, "scalar cast/inference " + label);
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
        Check("fnc main():int{let int x=40;const int y=x;return y+2;}",
            42, "const eagerly snapshots current mutable binding value");
        Check("fnc main():int{val int x=40;const int y=x;return y+2;}",
            42, "const accepts runtime single-assignment source value");
        Reject("fnc main():int{const int x=9223372036854775807+1;return x;}",
            "const overflow is rejected before CIL");

        CheckOverflow("fnc main():int{return -(-9223372036854775808);}",
            "checked negation");
        CheckOverflow("fnc main():int{val int n=9223372036854775807;return n+1;}",
            "runtime val checked overflow in emitted CIL");
        CheckOverflow("fnc main():int{int unused=9223372036854775807+1;return 7;}",
            "immutable-default unused initializer remains eager and checked");
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
