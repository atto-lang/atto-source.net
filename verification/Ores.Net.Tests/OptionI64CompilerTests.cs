using Mono.Cecil;
using Ores.Net.Compiler;

internal static class OptionI64CompilerTests
{
    private static void Check(string source, long expected, string label)
    {
        // Exercise the actual opt-in AttoLang source parser, typed CFG,
        // verifier, PE builder and independent test-only CIL interpreter.
        var graph = OresStructuredCfgLowering.Lower(source);
        if (CfgReferenceEvaluator.Evaluate(graph) != expected)
            throw new Exception("Option<int> verified CFG result mismatch: " + label);
        var module = OresStructuredCfgLowering.LowerModule(source);
        if (CfgReferenceEvaluator.Evaluate(module.Main) != expected)
            throw new Exception("Option<int> module CFG result mismatch: " + label);

        var temp = Path.Combine(Path.GetTempPath(),
            "atto-option-cil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string first = Path.Combine(temp, "OresGenerated.dll");
            CilEmitter.EmitLibrary(module, first);
            using var assembly = AssemblyDefinition.ReadAssembly(first);
            var method = assembly.MainModule.Types.Single(type =>
                type.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(candidate => candidate.Name == "Run");
            if (CfgCilTestInterpreter.Evaluate(method) != expected)
                throw new Exception("Option<int> emitted CIL result mismatch: " + label);
            string second = Path.Combine(temp, "OresGenerated.copy.dll");
            CilEmitter.EmitLibrary(module, second);
            if (!File.ReadAllBytes(first).AsSpan().SequenceEqual(
                File.ReadAllBytes(second)))
                throw new Exception("Option<int> CIL not deterministic: " + label);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static void CheckModuleHelper(string source, long expected, string label)
    {
        // Statement-bodied helpers are real independent static MethodDefs;
        // the legacy inlining-only reference evaluator intentionally cannot
        // handle their call instruction. Verify the emitted method tokens.
        var module = OresStructuredCfgLowering.LowerModule(source);
        if (module.Helpers.Count != 1)
            throw new Exception("Option helper definition was not independently linked");
        var temp = Path.Combine(Path.GetTempPath(),
            "atto-option-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var output = Path.Combine(temp, "OresGenerated.dll");
            CilEmitter.EmitLibrary(module, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var program = assembly.MainModule.Types.Single(type =>
                type.FullName == "Oreslang.Generated.OresProgram");
            var run = program.Methods.Single(method => method.Name == "Run");
            if (CfgCilTestInterpreter.Evaluate(run) != expected)
                throw new Exception("Option module helper CIL mismatch: " + label);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static void CheckEagerFallbackOverflow()
    {
        // unwrap_or is eager in AttoLang. Both the verified CFG oracle and
        // the exact emitted CIL must trap even when Some would be selected.
        const string source =
            "fnc main():int{return Some(42).unwrap_or(9223372036854775807+1);}";
        var graph = OresStructuredCfgLowering.LowerModule(source);
        bool referenceOverflow = false;
        try { _ = CfgReferenceEvaluator.Evaluate(graph.Main); }
        catch (OverflowException) { referenceOverflow = true; }
        if (!referenceOverflow)
            throw new Exception("Option<int> fallback was incorrectly evaluated lazily in CFG");

        var temp = Path.Combine(Path.GetTempPath(),
            "atto-option-eager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var output = Path.Combine(temp, "OresGenerated.dll");
            CilEmitter.EmitLibrary(graph, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var method = assembly.MainModule.Types.Single(type =>
                type.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(candidate => candidate.Name == "Run");
            bool emittedOverflow = false;
            try { _ = CfgCilTestInterpreter.Evaluate(method); }
            catch (OverflowException) { emittedOverflow = true; }
            if (!emittedOverflow)
                throw new Exception("Option<int> fallback was incorrectly evaluated lazily in CIL");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static void CheckTrapOverflowBypassesOption()
    {
        // trap is not catch-all. A checked i64 overflow is terminal and must
        // not become a successful Option<int> or an ordinary None.
        const string source =
            "fnc main():int{val Option<int> x=trap (9223372036854775807+1);return x.unwrap_or(0);}";
        var module = OresStructuredCfgLowering.LowerModule(source);
        bool referenceFailed = false;
        try { _ = CfgReferenceEvaluator.Evaluate(module.Main); }
        catch (OverflowException) { referenceFailed = true; }
        if (!referenceFailed)
            throw new Exception("trap swallowed checked overflow in verified CFG");
        var temp = Path.Combine(Path.GetTempPath(), "atto-trap-cil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string output = Path.Combine(temp, "OresGenerated.dll");
            CilEmitter.EmitLibrary(module, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var method = assembly.MainModule.Types.Single(type =>
                type.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(candidate => candidate.Name == "Run");
            bool cilFailed = false;
            try { _ = CfgCilTestInterpreter.Evaluate(method); }
            catch (OverflowException) { cilFailed = true; }
            if (!cilFailed)
                throw new Exception("trap swallowed checked overflow in emitted CIL");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static void Reject(string source, string label)
    {
        try { _ = OresStructuredCfgLowering.LowerModule(source); }
        catch (FormatException) { return; }
        throw new Exception("Unverified Option source reached CFG: " + label);
    }

    private static void RejectForged(CfgFunction function, string label)
    {
        try { _ = OresCfgVerifier.Verify(function); }
        catch (FormatException) { return; }
        throw new Exception("Forged Option selector escaped CFG verifier: " + label);
    }

    private static CfgFunction Forged(CfgInstruction selector)
    {
        var ops = new CfgInstruction[]
        {
            new(0, CfgOpcode.ConstantBool, IrValueType.Bool, BoolConstant: true),
            new(1, CfgOpcode.ConstantI64, IrValueType.I64, I64Constant: 42),
            new(2, CfgOpcode.ConstantI64, IrValueType.I64, I64Constant: 7),
            selector
        };
        return new CfgFunction("main", IrValueType.I64, "entry",
            new[] { new CfgBlock("entry", Array.Empty<CfgParameter>(),
                ops, new CfgReturn(3)) });
    }

    private static CfgFunction ForgedBoolean(CfgInstruction operation)
    {
        var ops = new CfgInstruction[]
        {
            new(0, CfgOpcode.ConstantBool, IrValueType.Bool, BoolConstant: true),
            new(1, CfgOpcode.ConstantI64, IrValueType.I64, I64Constant: 42),
            operation
        };
        return new CfgFunction("main", IrValueType.Bool, "entry",
            new[] { new CfgBlock("entry", Array.Empty<CfgParameter>(),
                ops, new CfgReturn(2)) });
    }

    private static CfgFunction ForgedOptionEquality(CfgInstruction operation,
        bool leftTag = true, long leftPayload = 42,
        bool rightTag = true, long rightPayload = 42)
    {
        var ops = new CfgInstruction[]
        {
            new(0, CfgOpcode.ConstantBool, IrValueType.Bool, BoolConstant: leftTag),
            new(1, CfgOpcode.ConstantI64, IrValueType.I64, I64Constant: leftPayload),
            new(2, CfgOpcode.ConstantBool, IrValueType.Bool, BoolConstant: rightTag),
            new(3, CfgOpcode.ConstantI64, IrValueType.I64, I64Constant: rightPayload),
            operation
        };
        // Standalone generated Run must return i64. Branch on the verified
        // Bool selector rather than trying to emit a Bool-returning entry.
        // This keeps malformed mutations on the same verifier path and
        // permits checking the *actual* emitted CIL, not just the oracle.
        return new CfgFunction("main", IrValueType.I64, "entry",
            new[]
            {
                new CfgBlock("entry", Array.Empty<CfgParameter>(),
                    ops, new CfgBranch(4, "equal", Array.Empty<int>(),
                        "unequal", Array.Empty<int>())),
                new CfgBlock("equal", Array.Empty<CfgParameter>(),
                    new[] { new CfgInstruction(5, CfgOpcode.ConstantI64,
                        IrValueType.I64, I64Constant: 42) },
                    new CfgReturn(5)),
                new CfgBlock("unequal", Array.Empty<CfgParameter>(),
                    new[] { new CfgInstruction(6, CfgOpcode.ConstantI64,
                        IrValueType.I64, I64Constant: 0) },
                    new CfgReturn(6))
            });
    }

    private static void CheckOptionEqualityBoundary(bool leftTag, long leftPayload,
        bool rightTag, long rightPayload, bool expected, string label)
    {
        // These deliberately allow nonzero inactive None slots. This is
        // verifier-admitted host-side IR, never guest construction. The
        // generated CIL must ignore inactive payloads at runtime.
        var graph = OresCfgVerifier.Verify(ForgedOptionEquality(
            new CfgInstruction(4, CfgOpcode.OptionEqualI64, IrValueType.Bool,
                Left: 0, Right: 1, Arguments: new[] { 2, 3 }),
            leftTag, leftPayload, rightTag, rightPayload));
        long expectedValue = expected ? 42L : 0L;
        if (CfgReferenceEvaluator.Evaluate(graph) != expectedValue)
            throw new Exception("Option inactive-payload reference mismatch: " + label);
        var temp = Path.Combine(Path.GetTempPath(),
            "atto-option-equality-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var output = Path.Combine(temp, "OresGenerated.dll");
            CilEmitter.EmitLibrary(graph, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var method = assembly.MainModule.Types.Single(type =>
                type.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(candidate => candidate.Name == "Run");
            if (CfgCilTestInterpreter.Evaluate(method) != expectedValue)
                throw new Exception("Option inactive-payload emitted CIL mismatch: " + label);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    public static void Run()
    {
        const string some = "pub fnc main():int{val Option<int> value=Some(42);return value.unwrap_or(0);}";
        const string none = "pub fnc main():int{val Option<int> value=None;return value.unwrap_or(42);}";
        Check(some, 42, "Some(42) tagged payload");
        Check("pub fnc main():int{val Option<int> wrapped=trap 42;return wrapped.unwrap_or(0);}",
            42, "trap int literal emits actual tagged Some");
        Check("fnc main():int{val Option<int> wrapped=trap (40+2);return wrapped.unwrap_or(0);}",
            42, "trap parenthesized expression retains all operands");
        Check("fnc main():int{if (trap 0).is_some(){return 42;}return 0;}",
            42, "trap Some(0) uses the tag, not nonzero payload");
        CheckModuleHelper("fnc add_one(int x):int{return x+1;}fnc main():int{val Option<int> wrapped=trap add_one(41);return wrapped.unwrap_or(0);}",
            42, "trap typed helper exactly once through native CIL");

        Check(none, 42, "None fallback");
        // Canonical AttoLang type sugar uses exactly the same verified
        // two-word Option<int> local representation and CIL lowering.
        Check("fnc main():int{val int? value=Some(42);return value.unwrap_or(0);}",
            42, "int? readonly Option local (Some)");
        Check("fnc main():int{val int? value=None;return value.unwrap_or(42);}",
            42, "int? readonly Option local (None)");
        Check("fnc main():int{let int? value=None;value=Some(42);return value.unwrap_or(0);}",
            42, "int? mutable Option local pair assignment");
        Check("fnc main():int{int? value=Some(42);return value.unwrap_or(0);}",
            42, "int? type-first immutable Option local");
        Check("fnc main():int{if None eq None{return 42;}return 0;}",
            42, "None equals None");
        Check("fnc main():int{if Some(0) eq None{return 0;}return 42;}",
            42, "Some(0) does not equal None");
        Check("fnc main():int{if None neq Some(0){return 42;}return 0;}",
            42, "None neq Some(0)");
        Check("fnc main():int{if Some(0) eq Some(0){return 42;}return 0;}",
            42, "Some(0) equals Some(0)");
        Check("fnc main():int{if Some(0) eq Some(1){return 0;}return 42;}",
            42, "Some payload inequality");
        Check("fnc main():int{if Some(-9223372036854775808) eq Some(-9223372036854775808){return 42;}return 0;}",
            42, "minimum signed i64 equality");
        Check("fnc main():int{if Some(9223372036854775807) neq Some(-9223372036854775808){return 42;}return 0;}",
            42, "full i64 comparison without floating conversion");
        Check("fnc main():int{let Option<int> a=None;a=Some(0);if a eq None{return 0;}if a eq Some(0){return 42;}return 0;}",
            42, "mutable Option full-tag equality after reassignment");
        Check("fnc main():int{if None.is_none(){return 42;}return 0;}",
            42, "None.is_none true");
        Check("fnc main():int{if Some(0).is_none(){return 0;}return 42;}",
            42, "Some(0).is_none false independent of zero payload");
        Check("fnc main():int{val Option<int> x=None;if x.is_none(){return 42;}return 0;}",
            42, "named Option local .is_none()");
        Check("fnc main():int{let Option<int> x=None;x=Some(0);if x.is_none(){return 0;}return 42;}",
            42, "mutable Option full tag replacement .is_none()");

        Check("fnc main():int{if Some(0).is_some(){return 42;}return 0;}",
            42, "Some(0) tag is present, independent of payload");
        Check("fnc main():int{if None.is_some(){return 0;}return 42;}",
            42, "None tag absent");
        Check("fnc main():int{return Some(-9223372036854775808).unwrap_or(0);}",
            long.MinValue, "minimum i64 payload preserved");
        Check("fnc main():int{return Some(9223372036854775807).unwrap_or(0);}",
            long.MaxValue, "maximum i64 payload preserved");
        Check("fnc main():int{return None.unwrap_or(-9223372036854775808);}",
            long.MinValue, "minimum i64 fallback preserved");
        Check("fnc main():int{let Option<int> x=None;x=Some(42);return x.unwrap_or(0);}",
            42, "mutable Option variable, complete tag/payload replacement");
        Check("fnc main():int{let Option<int> x=None;if true{x=Some(42);}else{x=Some(0);}return x.unwrap_or(0);}",
            42, "both-branch Option pair join");
        Check("fnc main():int{let Option<int> x=Some(42);if false{x=None;}return x.unwrap_or(0);}",
            42, "one-branch Option pair join");
        Check("fnc main():int{let Option<int> x=None;let int n=0;while n<2{x=Some(21);n=n+1;}return x.unwrap_or(0)+21;}",
            42, "loop-carried Option pair with two scalar phi values");
        CheckModuleHelper("fnc answer():int{val Option<int> x=Some(42);return x.unwrap_or(0);}fnc main():int{return answer();}",
            42, "Option local inside statically linked helper");
        CheckEagerFallbackOverflow();
        CheckTrapOverflowBypassesOption();
        CheckOptionEqualityBoundary(false, 42, false, 7, true,
            "None equals None even with different inactive payloads");
        CheckOptionEqualityBoundary(false, long.MinValue, false, long.MaxValue, true,
            "None equality never compares inactive full-i64 payloads");
        CheckOptionEqualityBoundary(false, 0, true, 0, false,
            "Some(0) differs from None even with matching payload bits");
        CheckOptionEqualityBoundary(true, long.MinValue, true, long.MinValue, true,
            "Some exact signed minimum equality");
        CheckOptionEqualityBoundary(true, long.MinValue, true, long.MaxValue, false,
            "Some full-width i64 values differ");

        // No unexpected re-entry into production scalar compiler.
        bool scalarRejected = false;
        try { _ = OresParser.Parse(some); }
        catch (FormatException e) when (
            e.Message.Contains("Option/Result/trap", StringComparison.Ordinal))
        {
            scalarRejected = true;
        }
        if (!scalarRejected)
            throw new Exception("Option<int> silently admitted in shipping scalar frontend");

        foreach (var (label, text) in new (string, string)[]
        {
            ("Some expects int", "fnc main():int{return Some(true).unwrap_or(0);}"),
            ("no implicit Option->int", "fnc main():int{return Some(42);}"),
            ("None cannot become int", "fnc main():int{return None;}"),
            ("no implicit int->Option", "fnc main():int{val Option<int> x=42;return 0;}"),
            ("no implicit int->int?", "fnc main():int{val int? x=42;return 0;}"),
            ("no bool? layout alias", "fnc main():int{val bool? x=None;return 0;}"),
            ("no int?? unproved nesting", "fnc main():int{val int?? x=None;return 0;}"),
            ("no Option<int>? implicit flatten", "fnc main():int{val Option<int>? x=None;return 0;}"),
            ("no int? helper parameter ABI", "fnc get(int? x):int{return 42;}fnc main():int{return 42;}"),
            ("no int? helper result ABI", "fnc get():int?{return Some(42);}fnc main():int{return 42;}"),
            ("no bare scalar omission", "fnc get(int x?):int{return x;}fnc main():int{return 42;}"),
            ("no unlowered parameter default", "fnc get(int x?=42):int{return x;}fnc main():int{return 42;}"),
            ("no tagged/untagged assignment", "fnc main():int{let Option<int> x=None;x=42;return 0;}"),
            ("no wrong fallback", "fnc main():int{return Some(42).unwrap_or(false);}"),
            ("no unsafe unwrap", "fnc main():int{return Some(42).unwrap();}"),
            ("no unknown Option method", "fnc main():int{return None.flatten();}"),
            ("is_none rejects arguments", "fnc main():int{if None.is_none(42){return 1;}return 0;}"),
            ("is_none is bool, not i64", "fnc main():int{return None.is_none();}"),
            ("Option cannot compare to i64", "fnc main():int{if Some(42) eq 42{return 1;}return 0;}"),
            ("i64 cannot compare to Option", "fnc main():int{if 42 eq Some(42){return 1;}return 0;}"),
            ("Option cannot compare to bool", "fnc main():int{if None eq true{return 1;}return 0;}"),
            ("Option cannot order by payload", "fnc main():int{if Some(1)<Some(2){return 1;}return 0;}"),
            ("no nested Option flatten", "fnc main():int{val Option<Option<int>> x=Some(None);return 0;}"),
            ("trap refuses nested Option", "fnc main():int{val Option<int> x=trap Some(7);return 0;}"),
            ("trap refuses bool payload", "fnc main():int{val Option<int> x=trap true;return 0;}"),
            ("trap does not implicitly convert Option to int", "fnc main():int{return trap 7;}"),
            ("trap unary precedence is explicit", "fnc main():int{return (trap 7)+2;}"),
            ("no nested Some coercion", "fnc main():int{return Some(None).unwrap_or(0);}"),
            ("no Option<bool> erasure", "fnc main():int{val Option<bool> x=None;return 0;}"),
            ("no Option helper argument ABI", "fnc get(Option<int> x):int{return 42;}fnc main():int{return 42;}"),
            ("no Option helper result ABI", "fnc get():Option<int>{return Some(42);}fnc main():int{return 42;}"),
            ("no Option const folding", "fnc main():int{const Option<int> x=Some(42);return x.unwrap_or(0);}"),
            ("no mutable borrowed Option", "fnc main():int{let Option<int> x=Some(42);let y=rt borrow x;return 0;}"),
            ("no implicit Some-as-bool", "fnc main():int{if Some(42){return 1;}return 42;}")
        })
            Reject(text, label);

        // Mutant CFG selectors must be rejected by the real SSA verifier:
        // checking only source syntax would leave this CIL entry forgeable.
        RejectForged(Forged(new CfgInstruction(3, CfgOpcode.OptionOrI64,
            IrValueType.I64, Left: 1, Right: 0, Arguments: new[] { 2 })),
            "swapped tag and payload types");
        RejectForged(Forged(new CfgInstruction(3, CfgOpcode.OptionOrI64,
            IrValueType.I64, Left: 0, Right: 1, Arguments: new[] { 0 })),
            "boolean fallback");
        RejectForged(Forged(new CfgInstruction(3, CfgOpcode.OptionOrI64,
            IrValueType.I64, Left: 0, Right: 1, Arguments: Array.Empty<int>())),
            "missing fallback");
        RejectForged(Forged(new CfgInstruction(3, CfgOpcode.OptionOrI64,
            IrValueType.I64, Left: 0, Right: 1, Arguments: new[] { 2, 2 })),
            "duplicate fallback");
        RejectForged(Forged(new CfgInstruction(3, CfgOpcode.OptionOrI64,
            IrValueType.I64, Left: 0, Right: 1, Arguments: new[] { 2 },
            I64Constant: 1)),
            "smuggled constant");
        RejectForged(Forged(new CfgInstruction(3, CfgOpcode.OptionOrI64,
            IrValueType.Bool, Left: 0, Right: 1, Arguments: new[] { 2 })),
            "forged boolean result");
        RejectForged(Forged(new CfgInstruction(3, CfgOpcode.OptionOrI64,
            IrValueType.I64, Left: 0, Right: 1, Arguments: new[] { 4 })),
            "future SSA fallback");
        RejectForged(ForgedBoolean(new CfgInstruction(2,
            CfgOpcode.BoolNot, IrValueType.Bool, Left: 1)),
            "BoolNot rejected i64 operand");
        RejectForged(ForgedBoolean(new CfgInstruction(2,
            CfgOpcode.BoolNot, IrValueType.Bool)),
            "BoolNot requires one operand");
        RejectForged(ForgedBoolean(new CfgInstruction(2,
            CfgOpcode.BoolNot, IrValueType.Bool, Left: 0, Right: 0)),
            "BoolNot cannot read unverified second operand");
        RejectForged(ForgedBoolean(new CfgInstruction(2,
            CfgOpcode.BoolNot, IrValueType.Bool, Left: 0, Arguments: Array.Empty<int>())),
            "BoolNot cannot smuggle extra operands");
        RejectForged(ForgedBoolean(new CfgInstruction(2,
            CfgOpcode.BoolNot, IrValueType.I64, Left: 0)),
            "BoolNot must result in Bool");
        _ = OresCfgVerifier.Verify(ForgedBoolean(new CfgInstruction(2,
            CfgOpcode.BoolNot, IrValueType.Bool, Left: 0)));
        RejectForged(ForgedOptionEquality(new CfgInstruction(4,
            CfgOpcode.OptionEqualI64, IrValueType.Bool,
            Left: 1, Right: 0, Arguments: new[] { 2, 3 })),
            "Option equality rejects scalar left tag");
        RejectForged(ForgedOptionEquality(new CfgInstruction(4,
            CfgOpcode.OptionEqualI64, IrValueType.Bool,
            Left: 0, Right: 2, Arguments: new[] { 2, 3 })),
            "Option equality rejects bool payload");
        RejectForged(ForgedOptionEquality(new CfgInstruction(4,
            CfgOpcode.OptionEqualI64, IrValueType.Bool,
            Left: 0, Right: 1, Arguments: new[] { 3, 2 })),
            "Option equality rejects swapped right tag and payload");
        RejectForged(ForgedOptionEquality(new CfgInstruction(4,
            CfgOpcode.OptionEqualI64, IrValueType.Bool,
            Left: 0, Right: 1, Arguments: Array.Empty<int>())),
            "Option equality requires four typed operands");
        RejectForged(ForgedOptionEquality(new CfgInstruction(4,
            CfgOpcode.OptionEqualI64, IrValueType.Bool,
            Left: 0, Right: 1, Arguments: new[] { 2, 3, 3 })),
            "Option equality rejects extra payload");
        RejectForged(ForgedOptionEquality(new CfgInstruction(4,
            CfgOpcode.OptionEqualI64, IrValueType.I64,
            Left: 0, Right: 1, Arguments: new[] { 2, 3 })),
            "Option equality result must be bool");
        RejectForged(ForgedOptionEquality(new CfgInstruction(4,
            CfgOpcode.OptionEqualI64, IrValueType.Bool,
            Left: 0, Right: 1, Arguments: new[] { 2, 3 }, Target: "forge")),
            "Option equality cannot contain call target metadata");
        RejectForged(ForgedOptionEquality(new CfgInstruction(4,
            CfgOpcode.OptionEqualI64, IrValueType.Bool,
            Left: 0, Right: 1, Arguments: new[] { 2, 5 })),
            "Option equality rejects future SSA payload");
        var eqValid = OresCfgVerifier.Verify(ForgedOptionEquality(
            new CfgInstruction(4, CfgOpcode.OptionEqualI64, IrValueType.Bool,
                Left: 0, Right: 1, Arguments: new[] { 2, 3 })));
        if (CfgReferenceEvaluator.Evaluate(eqValid) != 42)
            throw new Exception("Verified Option equality did not match two Some(42) values");
        var valid = OresCfgVerifier.Verify(Forged(new CfgInstruction(
            3, CfgOpcode.OptionOrI64, IrValueType.I64,
            Left: 0, Right: 1, Arguments: new[] { 2 })));
        if (CfgReferenceEvaluator.Evaluate(valid) != 42)
            throw new Exception("Verified Option selector did not preserve Some(42)");

        // Trivia is never Option source. Comments remain accepted by both
        // compiler frontends and do not create ownership/Option proofs.
        _ = OresParser.Parse("fnc main():int{/* Some(None) /* Option<int> */ */return 42;}");
        _ = OresStructuredCfgLowering.LowerModule(
            "fnc main():int{// Some(42).unwrap_or(0)\\nreturn 42;}".Replace("\\n", "\n"));
        Console.WriteLine("NET_OPTION_I64_CFG_CIL_TESTS_EXECUTED|tag_payload=two_verified_ssa_primitives|guest_aot=UNVERIFIED");
    }
}
