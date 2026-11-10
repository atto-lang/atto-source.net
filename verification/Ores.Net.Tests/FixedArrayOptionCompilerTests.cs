using Mono.Cecil;
using Ores.Net.Compiler;

internal static class FixedArrayOptionCompilerTests
{
    private static void Check(string source, long expected, string label)
    {
        var inline = OresStructuredCfgLowering.Lower(source);
        if (CfgReferenceEvaluator.Evaluate(inline) != expected)
            throw new Exception("Array<int> inline verified CFG mismatch: " + label);
        var linked = OresStructuredCfgLowering.LowerModule(source);
        if (CfgReferenceEvaluator.Evaluate(linked.Main) != expected)
            throw new Exception("Array<int> linked verified CFG mismatch: " + label);

        var dir = Path.Combine(Path.GetTempPath(),
            "atto-fixed-array-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var first = Path.Combine(dir, "OresGenerated.dll");
            CilEmitter.EmitLibrary(linked, first);
            using var assembly = AssemblyDefinition.ReadAssembly(first);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            if (CfgCilTestInterpreter.Evaluate(entry) != expected)
                throw new Exception("Array<int> emitted CIL mismatch: " + label);
            var second = Path.Combine(dir, "OresGenerated.again.dll");
            CilEmitter.EmitLibrary(linked, second);
            if (!File.ReadAllBytes(first).AsSpan().SequenceEqual(
                File.ReadAllBytes(second)))
                throw new Exception("Array<int> emitted CIL is nondeterministic: " + label);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void CheckHelper()
    {
        var module = OresStructuredCfgLowering.LowerModule(
            "fnc answer():int{val Array<int> values=Array.of(42);return values.get(0).unwrap_or(0);}" +
            "fnc main():int{return answer();}");
        if (module.Helpers.Count != 1)
            throw new Exception("Array<int> helper was not linked as a separate MethodDef");
        var dir = Path.Combine(Path.GetTempPath(),
            "atto-fixed-array-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "OresGenerated.dll");
            CilEmitter.EmitLibrary(module, path);
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            if (CfgCilTestInterpreter.Evaluate(entry) != 42)
                throw new Exception("Array<int> static helper CIL result mismatch");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void Deny(string source, string label)
    {
        try { _ = OresStructuredCfgLowering.LowerModule(source); }
        catch (FormatException) { return; }
        throw new Exception("Unverified mutable/unsafe array source admitted: " + label);
    }

    private static void DenyDiagnostic(string source, string diagnostic)
    {
        try { _ = OresStructuredCfgLowering.LowerModule(source); }
        catch (FormatException error) when
            (error.Message.Contains(diagnostic, StringComparison.Ordinal))
        { return; }
        throw new Exception(
            "Array<int> get_copy must reject with compiler-owned diagnostic: " +
            diagnostic);
    }

    private static CfgFunction Forge(CfgInstruction probe)
    {
        return new CfgFunction("main", IrValueType.I64, "entry",
            new[] { new CfgBlock("entry", Array.Empty<CfgParameter>(),
                new CfgInstruction[]
                {
                    new(0, CfgOpcode.ConstantI64, IrValueType.I64, I64Constant: 0),
                    new(1, CfgOpcode.ConstantBool, IrValueType.Bool, BoolConstant: true),
                    probe,
                    new(3, CfgOpcode.ConstantI64, IrValueType.I64, I64Constant: 42)
                }, new CfgReturn(3)) });
    }

    private static void DenyForged(CfgInstruction probe, string label)
    {
        try { _ = OresCfgVerifier.Verify(Forge(probe)); }
        catch (FormatException) { return; }
        throw new Exception("Array<int> forged SSA index survived verifier: " + label);
    }

    public static void Run()
    {
        var positives = new (string Name, string Source, long Expected)[]
        {
            ("single", "fnc main():int{val Array<int> a=Array.of(42);return a.get(0).unwrap_or(0);}", 42),
            ("copy-first", "fnc main():int{val Array<int> a=Array.of(42);return a.get_copy(0).unwrap_or(0);}", 42),
            ("copy-zero-present", "fnc main():int{val Array<int> a=Array.of(0);if a.get_copy(0).is_some(){return 42;}return 0;}", 42),
            ("copy-empty", "fnc main():int{val Array<int> a=Array.of();return a.get_copy(0).unwrap_or(42);}", 42),
            ("copy-negative-index", "fnc main():int{val Array<int> a=Array.of(11);return a.get_copy(-1).unwrap_or(42);}", 42),
            ("copy-huge-index", "fnc main():int{val Array<int> a=Array.of(11);return a.get_copy(9223372036854775807).unwrap_or(42);}", 42),
            ("copy-min-index", "fnc main():int{val Array<int> a=Array.of(11);return a.get_copy(-9223372036854775808).unwrap_or(42);}", 42),
            ("copy-negative-high-bit", "fnc main():int{val Array<int> a=Array.of(11);return a.get_copy(-2147483649).unwrap_or(42);}", 42),
            ("copy-equal-to-length-eight", "fnc main():int{val Array<int> a=Array.of(0,1,2,3,4,5,6,7);return a.get_copy(8).unwrap_or(42);}", 42),
            ("copy-last-element-eight", "fnc main():int{val Array<int> a=Array.of(0,1,2,3,4,5,6,42);return a.get_copy(7).unwrap_or(0);}", 42),
            ("copy-min-payload", "fnc main():int{val Array<int> a=Array.of(-9223372036854775808);return a.get_copy(0).unwrap_or(0);}", long.MinValue),
            ("copy-max-payload", "fnc main():int{val Array<int> a=Array.of(9223372036854775807);return a.get_copy(0).unwrap_or(0);}", long.MaxValue),
            ("copy-computed-index", "fnc main():int{val Array<int> a=Array.of(11,21,42);return a.get_copy(1+1).unwrap_or(0);}", 42),
            ("copy-option-local", "fnc main():int{val Array<int> a=Array.of(42);val Option<int> snapshot=a.get_copy(0);return snapshot.unwrap_or(0);}", 42),
            ("copy-and-get-agree", "fnc main():int{val Array<int> a=Array.of(7);return a.get_copy(0).unwrap_or(0)-a.get(0).unwrap_or(0)+42;}", 42),
            ("copy-inline", "fnc main():int{return Array.of(42).get_copy(0).unwrap_or(0);}", 42),
            ("copy-empty-local", "fnc main():int{val Array<int> a=Array.of();val Option<int> r=a.get_copy(0);if r.is_some(){return 0;}return 42;}", 42),
            ("second", "fnc main():int{val Array<int> a=Array.of(0,42);return a.get(1).unwrap_or(0);}", 42),
            ("empty", "fnc main():int{val Array<int> a=Array.of();return a.get(0).unwrap_or(42);}", 42),
            ("negative-index", "fnc main():int{val Array<int> a=Array.of(5);return a.get(-1).unwrap_or(42);}", 42),
            ("min-index", "fnc main():int{val Array<int> a=Array.of(5);return a.get(-9223372036854775808).unwrap_or(42);}", 42),
            ("max-index", "fnc main():int{val Array<int> a=Array.of(5);return a.get(9223372036854775807).unwrap_or(42);}", 42),
            ("equal-length", "fnc main():int{val Array<int> a=Array.of(1,2);return a.get(2).unwrap_or(42);}", 42),
            ("computed-index", "fnc main():int{val Array<int> a=Array.of(1,7,42);return a.get(1+1).unwrap_or(0);}", 42),
            ("computed-value", "fnc main():int{val Array<int> a=Array.of(20+22);return a.get(0).unwrap_or(0);}", 42),
            ("some-zero-tag", "fnc main():int{val Array<int> a=Array.of(0);if a.get(0).is_some(){return 42;}return 0;}", 42),
            ("missing-tag", "fnc main():int{val Array<int> a=Array.of();if a.get(0).is_some(){return 0;}return 42;}", 42),
            ("min-payload", "fnc main():int{val Array<int> a=Array.of(-9223372036854775808);return a.get(0).unwrap_or(0);}", long.MinValue),
            ("max-payload", "fnc main():int{val Array<int> a=Array.of(9223372036854775807);return a.get(0).unwrap_or(0);}", long.MaxValue),
            ("eight-elements", "fnc main():int{val Array<int> a=Array.of(0,1,2,3,4,5,6,42);return a.get(7).unwrap_or(0);}", 42),
            ("copy-immutable", "fnc main():int{val Array<int> a=Array.of(42);val Array<int> b=a;return b.get(0).unwrap_or(0);}", 42),
            ("option-lookup", "fnc main():int{val Array<int> a=Array.of(42);val Option<int> r=a.get(0);return r.unwrap_or(0);}", 42),
            ("option-absent", "fnc main():int{val Array<int> a=Array.of();val Option<int> r=a.get(0);return r.unwrap_or(42);}", 42),
            ("inline-array", "fnc main():int{return Array.of(42).get(0).unwrap_or(0);}", 42),
            ("branch-domination", "fnc main():int{val Array<int> a=Array.of(42);if true{return a.get(0).unwrap_or(0);}return 0;}", 42),
            ("loop-domination", "fnc main():int{val Array<int> a=Array.of(21);let int i=0;while i<2{i=i+1;}return a.get(0).unwrap_or(0)+21;}", 42),
            ("two-reads", "fnc main():int{val Array<int> a=Array.of(21);return a.get(0).unwrap_or(0)+a.get(0).unwrap_or(0);}", 42)
        };
        foreach (var (name, text, expected) in positives) Check(text, expected, name);
        CheckHelper();

        var negatives = new (string Name, string Source)[]
        {
            ("wrong-element", "fnc main():int{val Array<int> a=Array.of(true);return 42;}"),
            ("wrong-element-option", "fnc main():int{val Array<int> a=Array.of(Some(1));return 42;}"),
            ("wrong-generic", "fnc main():int{val Array<bool> a=Array.of(1);return 42;}"),
            ("nested-array", "fnc main():int{val Array<Array<int>> a=Array.of(1);return 42;}"),
            ("nine-elements", "fnc main():int{val Array<int> a=Array.of(0,1,2,3,4,5,6,7,8);return 42;}"),
            ("mutable-array", "fnc main():int{let Array<int> a=Array.of(1);return 42;}"),
            ("const-array", "fnc main():int{const Array<int> a=Array.of(1);return 42;}"),
            ("array-direct-return", "fnc main():int{return Array.of(1);}"),
            ("index-not-int", "fnc main():int{val Array<int> a=Array.of(42);return a.get(true).unwrap_or(0);}"),
            ("copy-index-not-int", "fnc main():int{val Array<int> a=Array.of(42);return a.get_copy(true).unwrap_or(0);}"),
            ("copy-index-no-args", "fnc main():int{val Array<int> a=Array.of(42);return a.get_copy().unwrap_or(0);}"),
            ("copy-index-extra-args", "fnc main():int{val Array<int> a=Array.of(42);return a.get_copy(0,1).unwrap_or(0);}"),
            ("copy-implicit-option-to-int", "fnc main():int{val Array<int> a=Array.of(42);return a.get_copy(0);}"),
            ("copy-no-mutation", "fnc main():int{val Array<int> a=Array.of(42);a.set(0,9);return a.get_copy(0).unwrap_or(0);}"),
            ("copy-no-unsafe-brackets", "fnc main():int{val Array<int> a=Array.of(42);return a[0]+a.get_copy(0).unwrap_or(0);}"),
            ("copy-no-unknown-method", "fnc main():int{val Array<int> a=Array.of(42);return a.get_copy_unchecked(0).unwrap_or(0);}"),
            ("copy-no-map", "fnc main():int{val Map<int,int> m=Map.of();return m.get_copy(0).unwrap_or(0);}"),
            ("index-no-args", "fnc main():int{val Array<int> a=Array.of(42);return a.get().unwrap_or(0);}"),
            ("index-extra-args", "fnc main():int{val Array<int> a=Array.of(42);return a.get(0,1).unwrap_or(0);}"),
            ("unchecked-index", "fnc main():int{val Array<int> a=Array.of(42);return a[0];}"),
            ("unchecked-array-literal", "fnc main():int{return [42][0];}"),
            ("array-append", "fnc main():int{val Array<int> a=Array.of(42);return a.append(1);}"),
            ("array-resize", "fnc main():int{val Array<int> a=Array.of(42);return a.resize(1);}"),
            ("array-set", "fnc main():int{val Array<int> a=Array.of(42);return a.set(0,1);}"),
            ("borrow-array", "fnc main():int{val Array<int> a=Array.of(42);val int x=rt borrow a;return x;}"),
            ("mut-borrow-array", "fnc main():int{val Array<int> a=Array.of(42);val int x=rt mut borrow a;return x;}"),
            ("array-parameter", "fnc helper(Array<int> x):int{return 42;}fnc main():int{return 42;}"),
            ("array-return", "fnc helper():Array<int>{return Array.of(42);}fnc main():int{return 42;}"),
            ("implicit-array-i64", "fnc main():int{val int x=Array.of(42);return x;}"),
            ("implicit-i64-array", "fnc main():int{val Array<int> x=42;return 42;}"),
            ("implicit-option-int", "fnc main():int{val Array<int> a=Array.of(42);return a.get(0);}"),
            ("bad-index-placeholder", "fnc main():int{val Array<int> a=Array.of(42);return a.get(None).unwrap_or(0);}")
        };
        foreach (var (name, text) in negatives) Deny(text, name);
        DenyDiagnostic(
            "fnc main():int{val Array<int> a=Array.of(42);return a.get_copy(true).unwrap_or(0);}",
            "Array<int>.get_copy index must be int");

        var valid = OresCfgVerifier.Verify(Forge(new CfgInstruction(2,
            CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 0, I64Constant: 8)));
        if (valid.Blocks[0].Instructions[2].Type != IrValueType.Bool)
            throw new Exception("Array bound verifier changed result type");
        var mutants = new (string Name, CfgInstruction Probe)[]
        {
            ("negative-length", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 0, I64Constant: -1)),
            ("oversized-length", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 0, I64Constant: 9)),
            ("missing-length", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 0)),
            ("wrong-index", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 1, I64Constant: 1)),
            ("future-index", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 3, I64Constant: 1)),
            ("missing-index", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, I64Constant: 1)),
            ("extra-right", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 0, Right: 0, I64Constant: 1)),
            ("extra-args", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 0, Arguments: new[] { 1 }, I64Constant: 1)),
            ("extra-bool", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 0, BoolConstant: true, I64Constant: 1)),
            ("extra-target", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.Bool, Left: 0, Target: "evil", I64Constant: 1)),
            ("wrong-output", new(2, CfgOpcode.ArrayIndexValidI64, IrValueType.I64, Left: 0, I64Constant: 1))
        };
        foreach (var (name, probe) in mutants) DenyForged(probe, name);

        // Comments do not grant array capabilities, and the production
        // shipping parser continues rejecting this experimental feature.
        var ordinary = "fnc main():int{ /* Array.of(42).get(0) */ return 42; }";
        _ = OresParser.Parse(ordinary);
        _ = OresStructuredCfgLowering.LowerModule(ordinary);
        bool shippingRejected = false;
        try
        {
            _ = OresParser.Parse(
                "fnc main():int{val Array<int> a=Array.of(42);return a.get(0).unwrap_or(0);}");
        }
        catch (FormatException) { shippingRejected = true; }
        if (!shippingRejected)
            throw new Exception("Experimental Array<int> admitted in shipping i64 frontend");

        Console.WriteLine("NET_FIXED_ARRAY_OPTION_GET_TESTS_EXECUTED|positive=" +
            positives.Length + "|negative=" + negatives.Length + "|ssa_mutants=" +
            mutants.Length + "|nativeaot_guest=UNVERIFIED");
    }
}
