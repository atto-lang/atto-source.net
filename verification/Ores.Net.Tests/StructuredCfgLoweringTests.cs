using Mono.Cecil;
using Mono.Cecil.Cil;
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

    // A generic FormatException is insufficient: malformed source can fail for
    // unrelated reasons before reaching the typed equality operator under test.
    private static void RejectWithDiagnostic(string source, string expected, string label)
    {
        try { _ = OresStructuredCfgLowering.Lower(source); }
        catch (FormatException e)
        {
            if (e.Message.Contains(expected, StringComparison.Ordinal)) return;
            throw new Exception("Wrong structured rejection for " + label +
                ": expected '" + expected + "', got '" + e.Message + "'", e);
        }
        throw new Exception("Invalid structured source accepted: " + label);
    }

    private static void RejectAliasBoundary(string source, string spelling, string label)
    {
        int offset = source.LastIndexOf(spelling, StringComparison.Ordinal);
        if (offset < 0 || source[offset] != '!')
            throw new Exception("Missing operator boundary in fixture: " + label);
        RejectWithDiagnostic(source, "Invalid character at offset " + offset, label);
    }

    public static void Run()
    {
        // These cases pass the real structured typed CFG + generated CIL
        // reference interpreter. They do not claim native host execution.
        Check("""
            fnc plus(int a, int b): int { return a + b; }
            fnc add(int n) -> int ::
                int x = plus(2, 3),
                x + n
            end
            fnc main() -> int ::
                add(37)
            end
            """, 42, "comma fnc body with immutable runtime local and final result");
        Check("""
            routine twice(int n) -> int ::
                int factor = 2,
                factor * n
            end
            routine main() -> int ::
                twice(21)
            end
            """, 42, "comma routine body and terminal expression");
        Check("""
            fnc main() -> int ::
                int x = 1,
                int y = 40,
                x + y + 1
            end
            """, 42, "multiple sequenced immutable scalar locals");
        Check("""
            fnc main() -> int ::
                int x = 41,
                x + 1
            end
            """, 42, "comma body exactly-once final expression");
        foreach (string bad in new[] {
            "fnc main() -> int :: int x = 5,, x end",
            "fnc main() -> int :: int x = 5, end",
            "fnc main() -> int :: , 42 end",
            "fnc main() -> int :: int x = true, x end",
            "fnc main() -> int :: int x = 5, x = 7, x end",
            "fnc main() -> int :: bool x = true, x end"
        })
            Reject(bad, "comma-body syntax, binding or return type must fail closed");
        // Pipeline and lambda syntax must never masquerade as an
        // already-lowered native callable. The structured CIL backend has
        // scalar :: bodies but no closure frames or tuple lambda dispatch.
        foreach (string notYetLowered in new[] {
            "fnc main():int{val callback=|| => int -> 42;return callback();}",
            "fnc main() -> int :: 5 |> |n| => int -> n + 1 end",
            "fnc main() -> int :: 5 |> |n| -> val int x = n + 1, x end",
            "fnc main():int{val callback=|| -> int :: int x=5,x+3 end;return callback();}"
        })
            Reject(notYetLowered,
                "unlowered expression lambda or |> must fail native CIL admission");
        RejectWithDiagnostic("""
            define class Counter as
                mtd add(mut self, int n) -> int ::
                    n + 1
                end
            end
            fnc main(): int { return 42; }
            """, "Nominal class/actor methods require",
            "dotnet nominal method remains unsupported, not falsely certified");
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
        // Three literal spellings are equivalent, but const is NOT a
        // compile-time initializer requirement. All four observations travel
        // through real generated CIL and its checked reference evaluator.
        foreach (var (source, label) in new[] {
            ("fnc main():int{int x=7;return x;}", "type-first literal"),
            ("fnc main():int{const x=7;return x;}", "inferred const literal"),
            ("fnc main():int{const int x=7;return x;}", "typed const literal"),
            ("fnc inc(int p):int{return p+1;}fnc main():int{int x=inc(6);return x;}",
                "type-first runtime helper"),
            ("fnc inc(int p):int{return p+1;}fnc main():int{const x=inc(6);return x;}",
                "const runtime helper must remain legal"),
            ("fnc inc(int p):int{return p+1;}fnc main():int{const int x=inc(6);return x;}",
                "typed const runtime helper must remain legal")
        })
            Check(source, 7, "binding equivalence with CIL: " + label);
        Check("fnc main():int{let int x=7;x=8;return x;}",
            8, "explicit let actually rebinds in emitted CIL");
        Reject("fnc inc(int p):int{return p+1;}" +
               "fnc main():int{const int x=inc(6);x=8;return x;}",
            "readonly runtime const cannot rebind after successful call parse");
        Reject("fnc inc(int p):int{return p+1;}" +
               "fnc main():int{int x=inc(6);x=8;return x;}",
            "type-first readonly cannot rebind after successful call parse");

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
        // Statement-bodied helpers (a runtime-readonly local inside the callee)
        // need the independent MethodDef ABI, which is not implemented yet.
        // Until it is, lowering must fail closed with that exact diagnostic
        // rather than fold the helper or fabricate a CIL result.
        RejectWithDiagnostic("fnc plus_one(int p):int{int local=p+1;return local;}" +
                "fnc main():int{return plus_one(41);}",
            "Statement-bodied helper requires the independent MethodDef ABI",
            "statement-bodied helper stays fail-closed until MethodDef ABI");
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
        // Same byte-identical portable .atto fixture as Java #2648 and LLVM
        // #1554; evaluate compiler-owned CFG AND actual emitted CIL.
        const string logicalFixture = "contracts/logical-word-operators-v1/scalar-parity.atto";
        var logicalBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, logicalFixture));
        var blobHeader = System.Text.Encoding.ASCII.GetBytes("blob " + logicalBytes.Length + "\0");
        var blobBytes = new byte[blobHeader.Length + logicalBytes.Length];
        blobHeader.CopyTo(blobBytes, 0);
        logicalBytes.CopyTo(blobBytes, blobHeader.Length);
        var logicalBlobSha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(blobBytes))
            .ToLowerInvariant();
        if (logicalBlobSha != "9e3d2d230faaba5e1ccfeaf1c4449c0c2a120c34")
            throw new Exception("Cross-backend logical .atto fixture blob drift");
        Check(new System.Text.UTF8Encoding(false, true).GetString(logicalBytes),
            42, "exact Java/LLVM logical truth-table fixture through typed CFG/CIL");
        Check("fnc main():int{if false and true{return 7;}else{return 42;}}",
            42, "logical and false branch");
        Check("fnc main():int{if true or false{return 42;}else{return 7;}}",
            42, "logical or true branch");
        Check("fnc main():int{let int x=9223372036854775807;" +
            "if false and x+1>0{return 7;}else{return 42;}}",
            42, "and skips overflowing right operand");
        Check("fnc main():int{let int x=9223372036854775807;" +
            "if true or x+1>0{return 42;}else{return 7;}}",
            42, "or skips overflowing right operand");
        CheckOverflow("fnc main():int{let int x=9223372036854775807;" +
            "if false xor x+1>0{return 42;}else{return 7;}}",
            "xor eagerly evaluates overflowing right operand");
        Check("fnc main():int{if true xor true{return 7;}else{return 42;}}",
            42, "xor boolean false result");
        Check("fnc main():int{if true != false{return 42;}else{return 7;}}",
            42, "typed bool inequality");
        Check("fnc main():int{let int n=0;while (n<3) and (n<10) {n=n+1;}return n+39;}",
            42, "loop condition short-circuits through SSA backedges");
        Check("fnc inc(int x):int{return x+1;}" +
            "fnc main():int{return inc((false xor true) ? 41 : 0);}",
            42, "ternary and eager xor inside typed call argument");
        Check("fnc main():int{if true or (false and true){return 42;}else{return 7;}}",
            42, "nested logical short-circuit branches");
        Check("fnc main():int{const bool b=false and (9223372036854775807+1>0);" +
            "return b ? 7 : 42;}",
            42, "compile-time false and skips unchecked RHS");
        Check("fnc main():int{const bool b=true or (9223372036854775807+1>0);" +
            "return b ? 42 : 7;}",
            42, "compile-time true or skips unchecked RHS");
        Check("fnc main():int{let bool b=true; b=false;" +
            "if (b or true) and (true xor false) {return 42;}else{return 7;}}",
            42, "logical word expressions and assignments");
        // Lower() intentionally inlines simple helper bodies. Exercise the
        // *shipping* --structured linked-MethodDef path independently so a
        // passing reference interpreter cannot masquerade as NativeAOT ABI
        // evidence. The platform CI additionally executes the native binary.
        StructuredCfgModuleParityTests.Run();

        // Actual typed source-call semantics are checked in the independent
        // reference evaluator, emitted CIL interpreter and deterministic PE.
        // This is an acyclic, expression-only helper milestone, not a
        // general function ABI or runtime call instruction.
        Check("fnc plus_one(int x):int{return x+1;}fnc main():int{return plus_one(41);}",
            42, "one typed i64 helper");
        // A conservative *candidate audit*, not a CIL tail. or NativeAOT
        // guarantee. It cannot authorize recursive calls or ABI transfer.
        var simpleTail = OresStructuredCfgLowering.LowerModule(
            "fnc inc(int x):int{return x+1;}fnc main():int{return inc(41);}");
        var tailCandidates = CfgTailCallCandidates.Inspect(simpleTail.Main);
        if (tailCandidates.Count != 1 || tailCandidates[0].Target != "inc")
            throw new Exception("CFG direct return/call candidate not identified");
        var intervening = OresStructuredCfgLowering.LowerModule(
            "fnc inc(int x):int{return x+1;}fnc main():int{return inc(40)+1;}");
        if (CfgTailCallCandidates.Inspect(intervening.Main).Count != 0)
            throw new Exception("CFG falsely claimed non-tail arithmetic as tail");
        var nested = OresStructuredCfgLowering.LowerModule(
            "fnc inc(int x):int{return x+1;}fnc main():int{return inc(inc(40));}");
        if (CfgTailCallCandidates.Inspect(nested.Main).Count != 1)
            throw new Exception("CFG must identify only the outer returned call");

        // The structural call candidate is NOT automatically ABI compatible:
        // a zero-parameter main cannot musttail a one-parameter helper.
        if (CfgSameSignatureTailCandidates.Inspect(simpleTail).Count != 0 ||
            CfgSameSignatureTailCandidates.Inspect(nested).Count != 0)
            throw new Exception("CFG ABI probe admitted mismatched caller parameters");
        var sameSignature = OresStructuredCfgLowering.LowerModule(
            "fnc inc(int x):int{return x+1;}" +
            "fnc bridge(int n):int{return inc(n);}" +
            "fnc main():int{return bridge(41);}");
        var compatible = CfgSameSignatureTailCandidates.Inspect(sameSignature);
        if (compatible.Count != 1 ||
            compatible[0].Caller != "bridge" ||
            compatible[0].Target != "inc")
            throw new Exception("CFG failed to identify exact same-signature helper return");
        var sameSignatureZero = OresStructuredCfgLowering.LowerModule(
            "fnc answer():int{return 42;}fnc main():int{return answer();}");
        if (CfgSameSignatureTailCandidates.Inspect(sameSignatureZero).Count != 1)
            throw new Exception("CFG zero-parameter scalar return ABI missing");
        var mismatchedScalarType = OresStructuredCfgLowering.LowerModule(
            "fnc pred(int x):bool{return x==0;}" +
            "fnc bridge(bool b):bool{return pred(0);}" +
            "fnc main():int{return 0;}");
        if (CfgSameSignatureTailCandidates.Inspect(mismatchedScalarType).Count != 0)
            throw new Exception("CFG ABI gate admitted mismatched boolean/int parameter types");

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

        // These are .atto files. Check() passes them through the real
        // structured source parser, verified CFG, deterministic CIL emission
        // and an independent CIL evaluator, never runtime source generation.
        string operatorDir = Path.Combine(AppContext.BaseDirectory, "contracts", "erl-otp-aot");
        Check(File.ReadAllText(Path.Combine(operatorDir, "neq-alias-true.atto")),
            42, "AttoLang !eq true CIL comparison");
        Check(File.ReadAllText(Path.Combine(operatorDir, "neq-alias-false.atto")),
            42, "AttoLang !eq false CIL comparison");
        RejectAliasBoundary(
            File.ReadAllText(Path.Combine(operatorDir, "neq-alias-split.atto")),
            "! eq", "spaces must not turn ! eq into != or !eq");
        RejectWithDiagnostic(
            File.ReadAllText(Path.Combine(operatorDir, "neq-alias-mixed.atto")),
            "Equality requires matching int or bool operands", "no implicit bool-to-int equality");
        RejectAliasBoundary(
            File.ReadAllText(Path.Combine(operatorDir, "neq-alias-prefix.atto")),
            "!equation", "!equation must not tokenize as !eq plus suffix");

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
        // Spec ch. 16: eq/neq are the canonical comparisons. With bool equality
        // admitted, matching bool operands compare; mixed int/bool still rejects.
        Check("fnc main():int{if true neq false{return 42;}return 0;}",
            42, "neq compares matching bool operands");
        RejectWithDiagnostic("fnc main():int{if 1 neq true{return 42;}return 0;}",
            "Equality requires matching int or bool operands", "neq rejects mixed int/bool operands");
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
