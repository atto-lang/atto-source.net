using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

internal static class StructuredCfgLoweringTests
{
    public static void RunUnicodeIdentifierNegation()
    {
        const string source = "pub fnc main(): int { val int Φ = 10; val int φ = 20; val int 数量 = 30; val int 𠮷 = 40; if ¬false && ¬¬true then return Φ + φ + 数量 + 𠮷; else return 0; fi }";
        Check(source, 100, "Unicode names and negation");
        Check(source.Replace("¬", "!", StringComparison.Ordinal), 100, "ASCII negation alias");
        CheckModule("fnc 合計(int Φ, int φ, int 数量, int 𠮷): int { return Φ + φ + 数量 + 𠮷; } pub fnc main(): int { return 合計(10,20,30,40); }", 100, "Unicode callable and parameter names");
        foreach (var invalid in new[] {
            "pub fnc main(): int { return 未定義; }",
            "pub fnc main(): int { if ¬1 then return 1; else return 0; fi }",
            "pub fnc main(): int { val int ρ = 1; val int ρ = 2; return ρ; }",
            "pub fnc main(): int { return 42; } // \u202e"
        })
        {
            try { _ = OresStructuredCfgLowering.Lower(invalid); }
            catch (FormatException) { continue; }
            throw new Exception("Expected Unicode source rejection: " + Convert.ToHexString(Encoding.UTF8.GetBytes(invalid)));
        }
    }

    private static void Check(string source, long expected, string label, bool executeClr = false)
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
            if (executeClr)
            {
                // This is a test-only bridge. Load the actual emitted DLL,
                // not a host reconstruction of the guest calculation.
                var context = new System.Runtime.Loader.AssemblyLoadContext("record-guest", isCollectible: true);
                try
                {
                    using var bytes = new MemoryStream(File.ReadAllBytes(output));
                    var guest = context.LoadFromStream(bytes);
                    var method = guest.GetType("Oreslang.Generated.OresProgram", throwOnError: true)!
                        .GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        ?? throw new Exception("Generated CLR guest entry not found");
                    if (Convert.ToInt64(method.Invoke(null, null), System.Globalization.CultureInfo.InvariantCulture) != expected)
                        throw new Exception("Structured CoreCLR guest mismatch: " + label);
                }
                finally { context.Unload(); }
            }
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

    // Statement-bodied helpers (e.g. comma bodies with locals) are not inlined by the
    // legacy Lower() path; they use LowerModule()'s independent MethodDef ABI.
    private static void CheckModule(string source, long expected, string label)
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "ores-structured-cfg-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string output = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(OresStructuredCfgLowering.LowerModule(source), output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            if (CfgCilTestInterpreter.Evaluate(entry) != expected)
                throw new Exception("Structured module CIL mismatch: " + label);
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
        // Standalone ! now lexes as unary Boolean negation. In these
        // infix fixtures the preceding integer remains the condition and
        // must be rejected, never interpreted as != or !eq.
        int conditionEnd = offset;
        while (conditionEnd > 0 && char.IsWhiteSpace(source[conditionEnd - 1]))
            conditionEnd--;
        int conditionStart = conditionEnd;
        while (conditionStart > 0 && char.IsAsciiDigit(source[conditionStart - 1]))
            conditionStart--;
        if (conditionStart == conditionEnd)
            throw new Exception("Missing integer condition: " + label);
        RejectWithDiagnostic(source, "If condition must be bool at offset " + conditionStart, label);
    }

    private static void NegatedDoIfAotControlFlow()
    {
        Check("""
            fnc main():int {
                let int i = 0;
                loop {
                    i = i + 1;
                    do ifnot true then break; fi
                    do unless true then break; fi
                    do ifnot i < 3 then break; fi
                }
                return i;
            }
            """, 3, "do-ifnot-loop-break");
        Check("""
            fnc main():int {
                let int i = 0;
                loop {
                    i = i + 1;
                    do unless i < 4 then break; fi
                }
                return i;
            }
            """, 4, "do-unless-loop-break");
        Check("""
            fnc main():int {
                let int i = 0;
                loop {
                    i = i + 1;
                    do ifnot i eq 3 then continue; fi
                    break;
                }
                return i;
            }
            """, 3, "do-ifnot-loop-continue");
        Check("""
            fnc main():int {
                let int outer = 0;
                let int total = 0;
                loop {
                    outer = outer + 1;
                    do unless outer < 3 then break; fi
                    let int inner = 0;
                    loop {
                        inner = inner + 1;
                        do ifnot inner < 2 then break; fi
                        total = total + 1;
                    }
                }
                return total;
            }
            """, 2, "nested-loop-break-nearest");
        Check("fnc main():int{return ifnot false then 7 else 9 fi;}",
            7, "ifnot-value");
        Check("fnc main():int{return unless true then 7 else 9 fi;}",
            9, "unless-value");
        const string legacy = "fnc main():int{loop{if true then break; fi}return 0;}";
        Check(legacy, 0, "migration-only keyword statement");
        try
        {
            _ = OresStructuredCfgLowering.LowerStrictConditionals(legacy);
            throw new Exception("Strict conditional CFG incorrectly admitted a bare keyword statement");
        }
        catch (FormatException ex) when (ex.Message.Contains("E_CONDITIONAL_STATEMENT", StringComparison.Ordinal)) { }
        foreach (string head in new[] { "if", "ifnot", "unless" })
        {
            var strict = OresStructuredCfgLowering.LowerStrictConditionals(
                "fnc main():int{do " + head + " true then return 42; else return 42; fi}");
            if (CfgReferenceEvaluator.Evaluate(strict) != 42)
                throw new Exception("Strict conditional CFG mismatch: " + head);
        }
        Reject("fnc main():int{return if true then break; else 0 fi;}",
            "value-if cannot contain break");
        Reject("fnc main():int{do ifnot false then break; fi return 0;}",
            "break outside loop");
    }

    public static void RunExplicitTernaryArms()
    {
        Check("fnc main():int{let int x=true ? {return 40;} : {^ 7;};return x+2;}",
            42, "explicit arm return is not a function return");
        Check("fnc main():int{return false ? 7 : {^ 42;};}",
            42, "selected explicit false arm");
        Check("fnc main():int{let int x=9223372036854775807;return true ? {^ 42;} : {return x+1;};}",
            42, "unselected explicit arm does not overflow");
    }

    public static void RunStoredPair()
    {
        foreach (string body in new[] {
            "const x:=42;const Pair<int> p=x;return p.value;",
            "const x=x:=42;const Pair<int> p=x;return p.value;",
            "const x=Pair.of(\"x\",42);const Pair<int> p=x;return p.value;",
            "const x=\"x\" :=42;return x.value;",
            "const int y=7;const x=y:=42;return x.value+y-7;",
            "const x:=40;const y:=x.value+2;return y.value;"
        }) Check("fnc main():int{" + body + "}",42,"stored Pair: " + body, executeClr: true);
        foreach (string body in new[] {
            "const x:=1;const x:=2;return 0;",
            "const x:=x;return 0;",
            "const x=x:=x;return 0;",
            "const x:=true;return 0;",
            "val int y=42;const x:=y;return 0;",
            "const int x=42;const Pair<int> p=x;return 0;",
            "const x={value:42};const Pair<int> p=x;return 0;",
            "const x:=42;return x;",
            "const x:=42;x.value=7;return x.value;",
            "const x:=42;const r={...x};return 0;",
            "const x:=42;const r=x with {a:1};return 0;",
            "const x=Pair.of(7,42);return 0;",
            "const x:=42;const x=Pair.of(\"x\",42);return 0;"
        }) Reject("fnc main():int{" + body + "}", "stored Pair boundary: " + body);
        Console.WriteLine("STORED_PAIR_CORECLR_PASS positives=6 negatives=13");
    }

    public static void Run()
    {
        foreach (var body in new[]{
            "let int x=40; set x++; set ++x; return x;",
            "let int x=44; set x--; set --x; return x;",
            "let int x=20; set x+=2; set x-=1; set x*=2; return x;",
            "let int x=40; set x=x+2; return x;",
            "let int x=0; while x<42 { set x++; } return x;"})
            Check("fnc main():int{"+body+"}",42,"explicit update "+body);
        foreach (var body in new[]{
            "val int x=40; set x++; return x;",
            "set missing++; return 42;",
            "let bool x=true; set x++; return 42;",
            "let int x=40; set x+ =2; return x;",
            "let int x=40; set ++x++; return x;"})
            Reject("fnc main():int{"+body+"}","explicit update "+body);
        CheckOverflow("fnc main():int{let int x=9223372036854775807;set x++;return x;}",
            "explicit counter overflow");
        Check("fnc main():int{let int x=40;set! (x=x+2);return x;}",42,"wrapped local explicit update");
        Check("fnc main():int{let int x=40;set! (x=x+1);set! x=x+1;return x;}",42,"wrapped and unwrapped update");
        RejectWithDiagnostic("fnc main():int{val int x=40;set! (x=x+2);return x;}",
            "E_IMMUTABLE_REBIND","wrapped fixed binding");
        foreach (var body in new[]{
            "let int x=40; return set!(x=42);",
            "let int x=40; set!(42); return x;",
            "let int x=40; set!(x==42); return x;",
            "let int x=40; other!(x=42); return x;",
            "let int x=40; set !(x=42); return x;",
            "let int x=40; set! (x.field=42); return x;"})
            RejectWithDiagnostic("fnc main():int{"+body+"}","NET_STRUCTURED_MACRO_EXPANDER_REQUIRED","macro boundary "+body);
        Reject("fnc main():int{let int x=40;set!(x=42,43);return x;}","wrapped update cannot expand arguments");
        Console.WriteLine("EXPLICIT_SCALAR_UPDATE_CFG_CIL_PASS positives=7 negatives=13 overflow=1");
        foreach (var body in new[]{
            "let x=32;for let i=0;i<10;set i++{set x++;}return x;",
            "let x=32;for (let i=10;i>0;set --i){set x++;}return x;",
            "let x=32;for let i=0;i<10;set i+=2{set x+=2;}return x;",
            "let x=0;for (set x=40;x<42;set x++){}return x;",
            "let n=0;for let i=0;i<10;set i++{if i==2{continue;}if i==5{break;}set n+=10;}return n+2;",
            "let x=42;for let i=9223372036854775807;i<0;set i++{set x=0;}return x;",
            "let x=42;for let i=9223372036854775807;i>0;set i++{break;}return x;",
            "let x=30;for let i=0;i<3;set i++{for let j=0;j<4;set j++{set x++;}}return x;",
            "let x=40;for let i=0;i<2;set i++{set x++;continue;}return x;"})
            Check("fnc main():int{"+body+"}",42,"regular for "+body);
        foreach (var body in new[]{
            "for let i=0;i<10;i++{}return 42;",
            "for let i=0;i<10;let i++{}return 42;",
            "for let i=0;true;set missing++{}return 42;",
            "for let i=0;i;set i++{}return 42;",
            "for let i=0;i<1;set i++{}return i;",
            "val int i=0;for (;i<1;set i++){}return 42;",
            "for let i=0;i<1;set i+ =1{}return 42;"})
            Reject("fnc main():int{"+body+"}","regular for "+body);
        CheckOverflow("fnc main():int{for let i=9223372036854775807;i>0;set i++{}return 42;}",
            "regular for checked update overflow");
        Console.WriteLine("EXPLICIT_REGULAR_FOR_CFG_CIL_PASS positives=9 negatives=7 overflow=1");


        Check("fnc main():int{return match 42 over on _ as int z -> z end;}", 42, "typed scalar alias");
        Check("fnc main():int{return match 42 over on 41 as z when z==42 -> 0, on _ as const z -> z end;}", 42, "guard fallback scalar alias");
        Check("fnc main():int{let n=20;let r=0;do match n over on _ as rebind int z -> {z=z+2;r=z;} end return n+r;}", 42, "rebind alias does not write back subject");
        CheckModule("fnc bump(rebind int n):int{n=n+2;return n;} fnc main():int{let n=20;return bump(n)+n;}", 42, "rebind formal does not write back caller");
        CheckModule("fnc bump(rebind n:int):int{n=n+2;return n;} fnc main():int{let n=20;return bump(n)+n;}", 42, "name-first rebind formal");
        RejectWithDiagnostic("fnc main():int{do match 42 over on _ as int z -> {z=0;} end return 0;}", "E_IMMUTABLE_REBIND", "fixed alias");
        RejectWithDiagnostic("fnc main():int{return match 42 over on _ as bool z -> 0 end;}", "MATCH_ALIAS_TYPE", "wrong alias type");
        RejectWithDiagnostic("fnc main():int{return match 42 over on _ as rebind rebind int z -> z end;}", "MATCH_ALIAS_MODIFIER", "duplicate marker");
        RejectWithDiagnostic("fnc main():int{return match 42 over on _ as mut z -> 0 end;}", "MATCH_ALIAS_MUT_UNSUPPORTED", "scalar referent mutation");
        RejectWithDiagnostic("fnc bump(int n):int{n=n+2;return n;} fnc main():int{return bump(40);}", "E_IMMUTABLE_REBIND", "fixed formal");
        NegatedDoIfAotControlFlow();
        Check("""
            fnc main(): int {
                val bool a = Xor(true, false);
                val bool b = And(true, false);
                val bool c = Or(true, false);
                val bool d = Xor([true, false]);
                val bool e = And([true, false, true, true, true]);
                val bool f = Or([true, false, true, false]);
                return (a && !b && c && d && !e && f &&
                    And([]) && !Or([]) && !Xor([]) &&
                    And([true]) && !Or([false]) && Xor([true]) &&
                    Xor(true,true,true) && !Xor(true,true)) ? 42 : 7;
            }
            """, 42, "six named gate declarations and pack identities");
        Check("fnc main():int{let int x=9223372036854775807;" +
            "return (!And(false,x+1>0) && Or(true,x+1>0)) ? 42 : 7;}",
            42, "named scalar short circuit");
        CheckOverflow("fnc main():int{let int x=9223372036854775807;" +
            "return Xor(false,x+1>0,true) ? 42 : 7;}", "named XOR eager RHS");
        foreach (string gate in new[]{"And","Or","Xor"})
        foreach (string args in new[]{"","true","1,2","false,1","true,1","[true,1]",
            "[[true]]","[true],[false]","[1 eq 1,false]"})
        {
            bool rejected = false;
            try { _ = OresStructuredCfgLowering.Lower(
                $"fnc main():int{{return {gate}({args}) ? 42 : 7;}}"); }
            catch (FormatException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid named gate accepted: " + gate + "(" + args + ")");
        }


        // These cases pass the real structured typed CFG + generated CIL
        // reference interpreter. They do not claim native host execution.
        CheckModule("""
            fnc plus(int a, int b): int { return a + b; }
            fnc add(int n) -> int ::
                val int x = plus(2, 3),
                x + n
            end
            fnc main() -> int ::
                add(37)
            end
            """, 42, "comma fnc body with immutable runtime local and final result");
        CheckModule("""
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
        Check("""
            fnc main() -> int ::
                int x = 5,
                return x + 37
            end
            """, 42, "comma body explicit return without semicolon");
        Check("""
            fnc main() -> int ::
                int x = 5,
                return x + 37;
            end
            """, 42, "comma body explicit return with semicolon");
        Check("""
            fnc plus(int a, int b): int { return a + b; }
            fnc main() -> int ::
                val int x = plus(5, 7),
                val int y = plus(x, 29),
                y + 1
            end
            """, 42, "nested call commas and sequential local visibility");
        foreach (string badBody in new[] {
            "fnc main() -> int :: int x = 5, return true end",
            "fnc main() -> int :: int x = 5, return x + 1, end",
            "fnc main() -> int :: int x = 5, return x + 1; x end",
            "fnc main() -> int :: int x = 5 x + 1 end"
        })
            Reject(badBody, "comma body typed return and separator fail closed");
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
        // A type-first scalar is compile-time const. Runtime helper values
        // remain legal as non-rebindable val, not as constant-folded calls.
        foreach (var (source, label) in new[] {
            ("fnc main():int{int x=7;return x;}", "type-first scalar literal"),
            ("fnc main():int{const x=7;return x;}", "inferred const literal"),
            ("fnc main():int{const int x=7;return x;}", "typed const literal"),
            ("fnc inc(int p):int{return p+1;}fnc main():int{val int x=inc(6);return x;}",
                "typed val runtime helper"),
            ("fnc inc(int p):int{return p+1;}fnc main():int{val x=inc(6);return x;}",
                "inferred val runtime helper"),
            // Untyped const keeps java's stage-one readonly-binding meaning.
            ("fnc inc(int p):int{return p+1;}fnc main():int{const x=inc(6);return x;}",
                "untyped const runtime helper stays a readonly binding")
        })
            Check(source, 7, "const-versus-val CIL binding: " + label);
        Check("fnc main():int{let int x=7;x=8;return x;}",
            8, "explicit let actually rebinds in emitted CIL");
        foreach (var source in new[] {
            "fnc inc(int p):int{return p+1;}fnc main():int{int x=inc(6);return x;}",
            "fnc inc(int p):int{return p+1;}fnc main():int{const int x=inc(6);return x;}",
            "fnc inc(int p):int{return p+1;}fnc main():int{const x:int=inc(6);return x;}"
        })
            RejectWithDiagnostic(source, "E_NON_CONST_INITIALIZER",
                "runtime helper cannot initialize implicit/explicit const");
        RejectWithDiagnostic("fnc main():int{int x=7;x=8;return x;}",
            "E_IMMUTABLE_REBIND", "type-first constant cannot rebind");
        RejectWithDiagnostic("fnc main():int{const int x=7;x=8;return x;}",
            "E_IMMUTABLE_REBIND", "explicit constant cannot rebind");
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
        // Compile-time type-first immutable bindings in the EXPERIMENTAL structured
        // source -> verified CFG -> deterministic CIL interpreter path.
        // This is not a released Windows/Linux Native AOT guest receipt.
        foreach (var (source, expected, label) in new[] {
            ("fnc main():int{int x=7;return x;}", 7L, "implicit const int literal"),
            ("fnc main():int{const x=7;return x;}", 7L, "existing constexpr"),
            ("fnc main():int{const int x=7;return x;}", 7L, "typed constexpr"),
            ("fnc main():int{const x:int=7;return x;}", 7L, "name-first constexpr"),
            ("fnc main():int{int x=7;int y=x*3;return y;}", 21L, "constexpr dependency folded without runtime read"),
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
        RejectWithDiagnostic(
            "fnc main():int{int unused=9223372036854775807+1;return 7;}",
            "E_CONST_OVERFLOW",
            "unused implicit const overflow must reject before CIL");
        // Statement-bodied helpers (a runtime-readonly local inside the callee)
        // need the independent MethodDef ABI, which is not implemented yet.
        // Until it is, lowering must fail closed with that exact diagnostic
        // rather than fold the helper or fabricate a CIL result.
        RejectWithDiagnostic("fnc plus_one(int p):int{val int local=p+1;return local;}" +
                "fnc main():int{return plus_one(41);}",
            "Statement-bodied helper requires the independent MethodDef ABI",
            "statement-bodied helper stays fail-closed until MethodDef ABI");
        Reject("fnc plus_one(int p):int{val int local=p+1;local=0;return local;}" +
                "fnc main():int{return plus_one(41);}",
            "runtime readonly helper local cannot be reassigned");



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
        Check("fnc main():int{const Record<infer R> r=(a=1,b=true);const Record<R> r2=(b=false,a=10);const Tuple<infer T> t=(true,1);const Tuple<T> t2=(false,12);const struct<infer S> s={a:1};const struct<S> s2={a:20};return r2.a+t2[1]+s2.a;}",
            42, "named scalar aggregate types through real structured CFG and CIL");
        Check("fnc main():int{const Record r=(a=10);const Tuple t=(true,12);const struct s={a:20};return r.a+t[1]+s.a;}",
            42, "bare aggregate annotations create no named type");
        Check("fnc main():int{const Tuple t=(true,42);return t[1];}", 42, "checked constant tuple projection");
        foreach (string tail in new[] { "return t[2];", "return t[-1];", "return t[0+1];", "let int i=1;return t[i];", "return values[0];", "return [1,2][0];", "return t[1][0];", "return t[1]; } ]" })
            Reject("fnc main():int{const Tuple t=(true,42);" + tail + "}", "no unchecked collection or dynamic tuple projection: " + tail);
        Reject("fnc main():int{const Record<infer> r=(a=1);return 0;}", "bare infer invalid");
        foreach (string name in new[] { "select", "await", "Pointer", "ActorMail",
            "ReadLoan", "AtomicInt", "RwLock", "boolean", "String", "while" })
            Reject("fnc main():int{const Record<infer " + name + "> r=(a=1);return 0;}",
                "reserved aggregate type cannot disappear during lowering: " + name);
        Reject("fnc main():int{const Record<infer R> select=(a=1);return 0;}",
            "reserved aggregate local");
        Reject("fnc main():int{const struct<infer S> s={await:1};return 0;}",
            "reserved aggregate field");
        Check("fnc main():int{const Record<infer If> r=(a=1);const Record<If> s=(a=42);return s.a;}",
            42, "case-sensitive type alias remains legal");
        Check("fnc main():int{const Record<infer R> r=(a=1,b=true);const Record<R> r2=(b=false,a=11);const Tuple<infer T> t=(true,1);const Tuple<T> t2=(false,14);const struct<infer S> s={a:1};const struct<S> s2={a:23};return r2.a+t2[1]+s2.a;}",
            48, "named shape reuse observes the second values");

        Reject("fnc main():int{const Record<Missing> r=(a=1);return 0;}", "unknown aggregate alias");
        Reject("fnc main():int{const Record<infer R> r=(a=1);const Tuple<infer R> t=(1,2);return 0;}", "duplicate alias");
        Reject("fnc main():int{const Record<infer R> r=(a=1);const Record<R> r2=(a=true);return 0;}", "record field type mismatch");
        Reject("fnc main():int{const Record<infer R> r=(a=1);const Record<R> r2=(a=1,b=2);return 0;}", "closed record extra field");
        Reject("fnc main():int{const Record<infer R> r=(a=1,b=2);const Record<R> r2=(a=1);return 0;}", "closed record missing field");
        Reject("fnc main():int{const Record<infer R> r=(a=1,a=2);return 0;}", "duplicate named field");
        Reject("fnc main():int{const Tuple<infer T> t=(1,true);const Tuple<T> t2=(true,1);return 0;}", "tuple slot mismatch");
        Reject("fnc main():int{const Tuple<infer T> t=(1,true);const Tuple<T> t2=(1);return 0;}", "tuple arity mismatch");
        Reject("fnc main():int{const struct<infer S> s={a:1};const Record<S> r=(a=1);return 0;}", "aggregate kind mismatch");
        Reject("fnc main():int{const Tuple<infer T> t=(1,true);const r=infer record{...t,x:1};return r.x;}", "tuple cannot spread as record");
        Reject("fnc main():int{const Record r=(a=1);const Record<R> r2=(a=2);return 0;}", "anonymous record does not bind type");
        // Mixed Record construction uses the real checked CFG and emitted CIL.
        foreach (string literal in new[] {"{foo:=40,bar:2}", "(foo:=40,bar=2)", "(foo:40,bar:=2)", "{foo=40,bar:=2,}"})
        {
            Check("fnc main():int{const Record r=" + literal + ";return r.foo+r.bar;}", 42, "mixed explicit Record " + literal, executeClr: true);
            if (literal.StartsWith("{", StringComparison.Ordinal))
                Check("fnc main():int{const r=" + literal + ";return r.foo+r.bar;}", 42, "mixed inferred Record " + literal, executeClr: true);
        }
        Check("fnc main():int{const r=(foo:40,bar:2);return r.foo+r.bar;}",42,"inferred colon Record",executeClr:true);
        Check("fnc main():int{const Record r=(foo:=40,bar:=2);return r.foo+r.bar;}",42,"typed Pair fields Record",executeClr:true);
        Check("fnc main():int{const x=();const Record r=();const Record s={};return 42;}",42,"typed empty Records and empty Tuple",executeClr:true);
        Check("fnc main():int{const r={foo:40};const Record<T> v=infer {...r,bar:2};const Record<T> w=(bar:2,foo=40);return w.foo+v.bar;}",42,"inferred extended lexical Record shape", executeClr: true);
        Check("fnc main():int{const r={foo:true};const v={...r,foo:=42};return v.foo;}",42,"explicit spread override changes type", executeClr: true);
        Check("fnc main():int{const r={foo:40};const s={foo:1};const v={...r,...s,foo:42};return v.foo;}",42,"explicitly resolved spread overlap", executeClr: true);
        Check("fnc main():int{const r={foo:40};const v={...r,foo:42};return v.foo+r.foo-40;}",42,"spread leaves original unchanged", executeClr: true);
        foreach (string body in new[] {
            "const r={foo:=1,foo:2};return 0;",
            "const r={};return 0;",
            "const r=(foo=1);return 0;",
            "const r=(foo:1,bar:=2);return 0;",
            "const r=(foo:1,bar=2);return 0;",
            "const r=(foo:=1,bar:=2);return 0;",
            "const r=(foo:=1);return 0;",
            "const r={foo : = 1};return 0;",
            "const r={foo:=1 bar:=2};return 0;",
            "const r={foo:1};const s={foo:2};const v={...r,...s};return 0;",
            "const r={foo:1};const v={foo:2,...r};return 0;",
            "const Record<T> v=infer {...v,foo:1};return 0;",
            "const int T=1;const Record<T> v=infer {foo:1};return 0;",
            "const Record<T> v={foo:1};return 0;",
            "const Record<infer T> r={foo:1};const Record<T> v=infer {foo:true};return 0;",
            "const struct s={foo:=1};return 0;",
            "const r={foo:1};return r;",
            "val int x=1;val r=record{foo:x};const v={...r};return 0;"
        }) Reject("fnc main():int{" + body + "}", "mixed Record safety: " + body);
        Console.WriteLine("MIXED_RECORD_CORECLR_PASS positives=13 negatives=18");
        Check("fnc main():int{const Tuple t=(20,22);const (a,b) as r=t;return a+r[1];}",42,"scalar tuple capture",executeClr:true);
        Check("fnc main():int{const s={a:20,b:22,c:9};const {a,b} as r=s;return a+r.b+r.c-9;}",42,"Record capture retains unselected fields",executeClr:true);
        Check("fnc main():int{const Tuple t=(20,22);const (a,b) as Record r=t;return r.a+b;}",42,"explicit Tuple to Record capture",executeClr:true);
        Check("fnc main():int{const x=();const () as r=x;const Record e={};const {} as s=e;return 42;}",42,"empty aggregate capture",executeClr:true);
        foreach (string body in new[] {
            "const Tuple t=(1,2);const (a) as r=t;return 0;",
            "const Tuple t=(1,2);const (a,a) as r=t;return 0;",
            "const Tuple t=(1,2);const (a,b) as a=t;return 0;",
            "const Tuple t=(1,2);const {a,b} as r=t;return 0;",
            "const s={a:1};const {b} as r=s;return 0;",
            "const Tuple t=(1,2);const (a,b) as Array r=t;return 0;",
            "const Tuple t=(1,2);const (a,let b) as r=t;return 0;",
            "const Tuple t=(1,2);const [a,b] as r=t;return 0;",
            "const Tuple t=(1,2);const (a,b) as r=t;a=3;return a;"
        }) Reject("fnc main():int{"+body+"}", "scalar destructure safety: "+body);
        foreach (string body in new[] {
            "const r={foo:=40\nbar:=2};return r.foo+r.bar;",
            "const r={foo:=40\nbar=2};return r.foo+r.bar;",
            "const r={foo:=40\r\nbar:2};return r.foo+r.bar;",
            "const r={foo:=40 // comment\nbar:=2};return r.foo+r.bar;",
            "const r={foo:40};const v=r with {bar:2};return v.foo+v.bar;",
            "const r={foo:true};const v=r with {foo:=42};return v.foo;",
            "const r={foo:40};const v=r with {foo:42};return v.foo+r.foo-40;",
            "const r={foo:40};const v=r with {bar:=1\nbaz:=1};return v.foo+v.bar+v.baz;"
        }) Check("fnc main():int{" + body + "}",42,"Record with/newline: " + body, executeClr: true);
        foreach (string body in new[] {
            "const r={foo:=1 bar:=2};return 0;",
            "const r={foo:=1 /* comment\n */ bar:=2};return 0;",
            "const r={foo:1\nbar:=2};return 0;",
            "const r={foo=1\nbar:=2};return 0;",
            "const r=(foo:=1\nbar=2);return 0;",
            "const r={foo:=1\nfoo:2};return 0;",
            "const r={foo:1};const v=r with {foo:2,foo:=3};return 0;",
            "const v=missing with {foo:1};return 0;",
            "const int r=1;const v=r with {foo:1};return 0;",
            "const Tuple r=(1,2);const v=r with {foo:1};return 0;",
            "const struct r={foo:1};const v=r with {foo:2};return 0;",
            "val int x=1;val r=record{foo:x};const v=r with {foo:2};return 0;",
            "const r={foo:1};return r with {foo:2};"
        }) Reject("fnc main():int{" + body + "}", "Record with/newline boundary: " + body);
        Console.WriteLine("RECORD_WITH_NEWLINE_CORECLR_PASS positives=8 negatives=13");
        RunStoredPair();
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
        Check("fnc plus(int n):int{return n+1;}fnc main():int{val int x=plus(41);return x;}",
            42, "val runtime helper result (type-first int is const)");
        RejectWithDiagnostic("fnc plus(int n):int{return n+1;}fnc main():int{int x=plus(41);return x;}",
            "E_NON_CONST_INITIALIZER", "type-first int cannot hold a runtime helper result");
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
            ("fnc main():int{let int x=40;val y=x;return y+2;}", "val runtime initializer is fixed binding"),
            ("fnc plus(int n):int{return n+1;}fnc main():int{val x=plus(41) as int;return x;}", "checked cast of function result"),
            ("fnc plus(int n):int{return n+1;}fnc main():int{val x=plus(41);return x;}", "inferred function result without cast"),
            ("fnc main():int{const x=7;if true{const z=x as int;return z+35;}else{return 0;}}", "scope-local inference"),
            ("fnc main():int{const x=true as bool as bool;if x{return 42;}else{return 0;}}", "chained bool identity casts"),
            ("fnc main():int{val x=7;val y=x as int;return y+35;}", "inferred val preserves int"),
            ("fnc main():int{let x=7;x=42;return x;}", "inferred let preserves mutable int"),
            ("fnc main():int{let x=false;x=true;if x{return 42;}else{return 0;}}", "inferred mutable bool"),
            ("fnc yes():bool{return true;}fnc main():int{val x=yes();if x{return 42;}else{return 0;}}", "inferred bool helper result"),
            ("fnc yes():bool{return true;}fnc main():int{val x=yes() as bool;if x{return 42;}else{return 0;}}", "checked bool helper result"),
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
            ("fnc main():int{let x=7;const snap=x;x=42;return snap+35;}", "untyped const captures prior SSA version"),
            ("fnc main():int{let x=7;val snap=x;x=42;return snap+35;}", "val captures prior SSA version"),
            ("fnc main():int{let x=7;val snap=x as int;x=42;return snap+35;}", "checked cast snapshot cannot alias mutable local"),
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
        // Canonical symbolic spellings (spec ch.16) share the same typed CFG/CIL path.
        Check("fnc main():int{if false && true{return 7;}else{return 42;}}",
            42, "&& false branch");
        Check("fnc main():int{if true || false{return 42;}else{return 7;}}",
            42, "|| true branch");
        Check("fnc main():int{let int x=9223372036854775807;" +
            "if false && x+1>0{return 7;}else{return 42;}}",
            42, "&& skips overflowing right operand");
        Check("fnc main():int{let int x=9223372036854775807;" +
            "if true || x+1>0{return 42;}else{return 7;}}",
            42, "|| skips overflowing right operand");
        CheckOverflow("fnc main():int{let int x=9223372036854775807;" +
            "if Xor(false, x+1>0){return 42;}else{return 7;}}",
            "named Xor eagerly evaluates overflowing right operand");
        Check("fnc main():int{if Xor(true,true){return 7;}else{return 42;}}",
            42, "named Xor boolean false result");
        Check("fnc main():int{if Xor(true,false){return 42;}else{return 7;}}",
            42, "named Xor boolean true result");
        Check("fnc main():int{if Xor(true,true && false){return 42;}else{return 7;}}",
            42, "named Xor retains typed conjunction argument");
        Check("fnc main():int{if true || Xor(true,true){return 42;}else{return 7;}}",
            42, "named Xor remains inside lazy disjunction");
        foreach (var (body, expected) in new (string, long)[] {
            ("let int x=12; x ^^=10; return x;", 6),
            ("let int x=-9223372036854775808; x ^^= -1; return x;", long.MaxValue),
            ("let int x=9223372036854775807; x ^^= -1; return x;", long.MinValue),
            ("let int x=-9223372036854775808; x ^^=x; return x;", 0),
            ("let int x=9007199254740993; x ^^=9007199254740992; return x;", 1),
            ("let int x=12; if true {x ^^=10;} return x;", 6),
            ("const int x=12 ^^ 10; return x;", 6),
            ("return 5+6 ^^ 4;", 15),
            ("return 11 ^^ 4 ^^ 7;", 8),
            ("return Integer.bitXor(12,10);", 6),
            ("return (11 ^^ 4) eq 15 ? 42 : 0;", 42)
        })
            Check("fnc main():int{" + body + "}", expected, "canonical i64 XOR " + body);
        CheckModule("fnc mask(int a,int b):int{return a ^^ b;}" +
            "fnc main():int{return mask(12,10);}", 6, "linked typed helper XOR");
        CheckOverflow("fnc main():int{let int x=9223372036854775807; x ^^=x+1; return x;}",
            "compound XOR evaluates checked RHS before storing");
        foreach (string body in new[] {
            "val int x=12; x ^^=10; return x;",
            "const int x=12; x ^^=10; return x;",
            "let bool x=true; x ^^=false; return 0;",
            "let int x=12; x ^^=true; return x;",
            "let int x=12; x ^^ =10; return x;",
            "let int x=12; x ^/*gap*/^=10; return x;",
            "return true ^^ false ? 1 : 0;",
            "return 11 ^^ 4 eq 15 ? 42 : 0;",
            "return Xor(1,2) ? 1 : 0;",
            "return Xor(true) ? 1 : 0;"
        })
            Reject("fnc main():int{" + body + "}", "canonical XOR rejection " + body);
        Check("fnc main():int{if true != false{return 42;}else{return 7;}}",
            42, "typed bool inequality");
        Check("fnc main():int{let int n=0;while (n<3) and (n<10) {n=n+1;}return n+39;}",
            42, "loop condition short-circuits through SSA backedges");
        Check("fnc inc(int x):int{return x+1;}" +
            "fnc main():int{return inc((false xor true) ? 41 : 0);}",
            42, "ternary and eager xor inside typed call argument");
        RunExplicitTernaryArms();
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
        Check("fnc main():int{return -7/3;}", -2, "checked signed integer quotient");
        Check("fnc main():int{const int quotient=7/2; return quotient;}", 3,
            "compile-time division agrees with the runtime quotient");

        Reject("fnc main():int{val int x=1;x=2;return x;}", "val reassignment");
        Reject("fnc main():int{const int x=1;x=2;return x;}", "const reassignment");
        Reject("fnc main():int{val int x=1;if true{x=2;}return x;}",
            "val cannot be mutated inside nested block");
        Check("fnc main():int{let int x=40;val int y=x;return y+2;}",
            42, "val snapshots current mutable binding value");
        Check("fnc main():int{val int x=40;val int y=x;return y+2;}",
            42, "val accepts runtime single-assignment source value");
        Reject("fnc main():int{let int x=40;const int y=x;return y+2;}",
            "const must not snapshot runtime mutable local");
        Reject("fnc main():int{val int x=40;int y=x;return y+2;}",
            "implicit const must not copy runtime val as constexpr");
        Reject("fnc main():int{const int x=9223372036854775807+1;return x;}",
            "const overflow is rejected before CIL");

        CheckOverflow("fnc main():int{return -(-9223372036854775808);}",
            "checked negation");
        CheckOverflow("fnc main():int{val int n=9223372036854775807;return n+1;}",
            "runtime val checked overflow in emitted CIL");
        RejectWithDiagnostic("fnc main():int{int unused=9223372036854775807+1;return 7;}",
            "E_CONST_OVERFLOW", "unused implicit const must reject before CIL, even if unread");
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
