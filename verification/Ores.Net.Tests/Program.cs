using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;
using Ores.Net.Runtime;

static void Require(bool value, string why)
{
    if (!value) throw new Exception("FAILED: " + why);
}

static void Reject(Action action, string why)
{
    try { action(); }
    catch (FormatException) { return; }
    throw new Exception("Expected parser rejection: " + why);
}

static void RejectDiagnostic(Action action, string diagnostic, string why)
{
    try { action(); }
    catch (FormatException ex) when
        (ex.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new Exception("Expected parser rejection with diagnostic '" + diagnostic + "': " + why);
}

static void InvalidSend(Action action, string why)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Expected mailbox rejection: " + why);
}

// Opaque top types and value-producing conditionals are language-reserved,
 // but this i64-only bootstrap has no tagged dynamic-value ABI or typed CFG
 // lowering for them yet. Reject before OresIR/CIL emission.
foreach (var opaque in new[] { "any", "unknown", "Any", "Unknown" })
    RejectDiagnostic(
        () => OresParser.Parse("fnc main():" + opaque + "{return 1;}"),
        "tagged opaque-value ABI",
        "opaque top type " + opaque);
RejectDiagnostic(
    () => OresParser.Parse("fnc main():int{return 1 ? 2 : 3;}"),
    "ternary expression requires bool typing and CFG branch-merge lowering",
    "ternary must not fall through to i64 CIL lowering");
Require(OresParser.Parse("fnc main():int{return 1;} // any unknown ?") is Literal,
    "opaque-top and ternary tokens in comments remain lexically invisible");

SourceByteBudgetTests.Run();

// The canonical actor negative includes a later assignment. Lex its punctuation
// while preserving the precise unsupported top-level declaration diagnostic.
try
{
    OresParser.Parse("define actor Worker as { pub fnc on_message(int n): int { return n; } }\n" +
        "pub fnc main(): int { const worker = spawn Worker(); stop worker; return 43; }\n");
    throw new Exception("Unsupported actor declaration was admitted");
}
catch (FormatException ex) when (ex.Message == "Expected 'fnc' at offset 7") { }
Reject(() => OresParser.Parse("fnc main():int { return 1=2; }"), "assignment expression");
Reject(() => OresParser.Parse("fnc main():int { const x = 1; return x; }"), "local assignment");

ActorRefNoWireSelfTest.Run();
ActorMailboxBoundsSelfTest.Run();
ActorMailboxRestartAtomicitySelfTest.Run();
ActorMailboxAuthorizationPrioritySelfTest.Run();
ActorMailboxPrivateDataSelfTest.Run();
ActorArenaSelfTest.Run();
ActorShardColumnsSelfTest.Run();
ActorDispatchDomainsSelfTest.Run();

// Import parity is deliberately not claimed for the .NET scalar compiler.
// Each form must fail at lexical admission with a specific import diagnostic,
// before either typed IR verification or Native AOT CIL emission.
foreach (var path in new[] {
    "+/src/helper", "+src/helper", "@/src/helper", "pkg:math/src/helper", "./helper",
    "../helper", "common/helper", ".hidden/helper",
    "+/src//helper", "+/src/./helper", "+/src/../helper",
    "pkg:math/src/../helper", @"pkg:math\src\helper"
})
{
    try
    {
        _ = OresParser.Parse("import fnc helper from " + (char)34 + path +
            (char)34 + "; fnc main():int{return 42;}");
    }
    catch (FormatException ex) when
        (ex.Message.Contains("source imports require verified module graph lowering",
            StringComparison.Ordinal)) { continue; }
    throw new Exception("Unsupported import was not rejected for the right reason: " + path);
}
Require(OresParser.Parse("fnc main():int{return 42;} // import not code") is Literal,
    "import in a comment must not be treated as an import declaration");


foreach (var operation in new[] { "ref", "unref", "deref", "deref mut", "raw" })
{
    try { _ = OresParser.Parse($"fnc main():int{{return rt {operation} value;}}"); }
    catch (FormatException ex) when
        (ex.Message.Contains("requires provenance-checked pointer lowering",
            StringComparison.Ordinal)) { continue; }
    throw new Exception("Unsupported pointer operation did not fail closed: rt " + operation);
}


foreach (var operation in new[] { "copy", "share", "take", "borrow", "mut borrow" })
{
    try { _ = OresParser.Parse($"fnc main():int{{return rt {operation} value;}}"); }
    catch (FormatException ex) when
        (ex.Message.Contains("requires typed actor-arena lowering",
            StringComparison.Ordinal)) { continue; }
    throw new Exception("Unsupported ownership operation did not fail closed: rt " + operation);
}
try
{
    _ = OresParser.Parse("fnc main():int{return rt borrow mut value;}");
    throw new Exception("Legacy rt borrow mut spelling was admitted");
}
catch (FormatException ex) when
    (ex.Message.Contains("use 'rt mut borrow'", StringComparison.Ordinal)) { }


foreach (var invalid in new[] { "\0", "\ud800", "\udc00", "\ud800x" })
    Reject(() => OresParser.Parse("fnc main():int{return 1;} // " + invalid),
        "invalid encoding in ignored comment");
Require(OresParser.Parse("fnc main():int{return 1;} // \ud83d\ude00") is Literal,
    "valid surrogate pair in comment");

foreach (var division in new[] { "3/2", "4/2", "5/0", "-9223372036854775808/-1" })
{
    try { OresParser.Parse("fnc main():int{return " + division + ";}"); }
    catch (FormatException ex) when (ex.Message.Contains("float result lowering")) { continue; }
    throw new Exception("Source division must fail closed until float lowering exists");
}
// Explicit internal checked-i64 IR remains available; it is not source /.
foreach (var pair in new[] { (long.MinValue, -1L), (5L, 0L) })
{
    var divisionIr = OresIrLowering.Lower(new Binary('/', new Literal(pair.Item1), new Literal(pair.Item2)));
    try { OresIrReferenceEvaluator.Evaluate(divisionIr); }
    catch (OverflowException) when (pair.Item2 == -1) { continue; }
    catch (DivideByZeroException) when (pair.Item2 == 0) { continue; }
    throw new Exception("Internal checked-i64 division must preserve traps");
}

// The i64-only source frontend cannot invoke named functions. Recognize |>
 // as one token and reject every pipeline (including nested expressions)
 // before CIL emission. Never reinterpret it as bitwise-or or comparison.
foreach (var expr in new[] {
    "1 |> twice", "(1 |> twice)", "1 + 2 |> plus(3)",
    "1 |> Option.map(inc)", "1 |> Result.map_err(annotate)",
    "1 |> Option /*gap*/ . map(inc)",
    "1 |> Result /*one /*nested*/ end*/ . map_err(annotate)",
    "1 |> plus(2 |> Option.map(inc))"
})
{
    try { OresParser.Parse("fnc main():int{return " + expr + ";}"); }
    catch (FormatException ex) when (ex.Message.Contains("pipeline requires named callable lowering",
            StringComparison.OrdinalIgnoreCase)) { continue; }
    throw new Exception("Unsupported value pipeline must fail closed: " + expr);
}
Require(OresParser.Parse("fnc main():int{return 1;} // 1 |> unsupported") is Literal,
    "pipe tokens in comments are not executable operators");
Require(OresParser.Parse("fnc main():int{return 1/*outer /*inner*/ end*/;}") is Literal,
    "nested block comments must not change a scalar literal");
Reject(() => OresParser.Parse("fnc main():int{return 1.foo;}"),
    "qualified member syntax without a pipeline is not admitted");
try
{
    OresParser.Parse("fnc main():int{return 1; /*unterminated");
    throw new Exception("Expected unterminated block comment diagnostic");
}
catch (FormatException ex) when (ex.Message.Contains("Unterminated block comment",
        StringComparison.Ordinal)) { }
Reject(() => OresParser.Parse("fnc main():int{return 1; /*".PadRight(150, '/') + "/*x*/;}"),
    "malformed unclosed nested comments");

var positive = OresParser.Parse("define pub fnc main() : int { return 2 + 3 * 4; }");
Require(positive is Binary { Operator: '+' }, "top-level expression shape");
Require(OresParser.Parse("define pub fnc main() -> int { return 2 + 3 * 4; }")
    is Binary { Operator: '+' }, "canonical formatter arrow");
Reject(() => OresParser.Parse("fnc main() - > int { return 1; }"), "split return arrow");
Reject(() => OresParser.Parse("fnc main() ->> int { return 1; }"), "malformed return arrow");
Require(OresParser.Parse("pub define fnc main():int {return -9223372036854775808;}") is Literal
    { Value: long.MinValue }, "signed minimum");
Require(OresParser.Parse("fnc main():int {return -(3 + 4);}") is Unary
    { Operator: '-' }, "parenthesized unary");

Reject(() => OresParser.Parse("fnc main():int{return foo;}"), "identifier");
Reject(() => OresParser.Parse("fnc main():int{return 3; } extra"), "trailing syntax");
Reject(() => OresParser.Parse("pub pub fnc main():int{return 3;}"), "duplicate pub");
Reject(() => OresParser.Parse("fnc main():int{return 9223372036854775808;}"), "i64 range");
Reject(() => OresParser.Parse("fnc main():int{return 1 @ 3;}"), "illegal character");
Reject(() => OresParser.Parse("fnc main():int{return (1+2;}"), "unmatched parentheses");

// Typed OresIR stage-one verification: no forward SSA references,
 // arbitrary opcodes, type confusion, or mutable IR after verification.
var lowered = OresIrLowering.Lower(positive);
Require(lowered.Name == "main" && lowered.ResultType == IrValueType.I64,
    "typed IR function signature");
Require(lowered.Instructions.Count == 5 && lowered.ReturnId == 4,
    "typed IR SSA arithmetic lowering");
Require(lowered.Instructions[3].Opcode == IrOpcode.CheckedMultiply &&
        lowered.Instructions[4].Opcode == IrOpcode.CheckedAdd,
    "typed IR retains operator precedence");
Require(lowered.Instructions[4].Left == 0 &&
        lowered.Instructions[4].Right == 3,
    "typed IR result operands");
Require(OresIrReferenceEvaluator.Evaluate(lowered) == 14,
    "independent reference interpreter computes i64 precedence");

var immutableSource = new List<IrInstruction> {
    new(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 123)
};
var immutableVerified = OresIrVerifier.Verify(
    new IrFunction("snapshot", IrValueType.I64, immutableSource, 0));
immutableSource[0] = new IrInstruction(0, IrOpcode.ConstantI64,
    IrValueType.I64, Constant: 999);
Require(immutableVerified.Instructions[0].Constant == 123,
    "verified IR is immutable after caller mutation");

Reject(() => OresIrVerifier.Verify(new IrFunction("missing", IrValueType.I64,
    Array.Empty<IrInstruction>(), 0)), "IR rejects empty instruction list");
Reject(() => OresIrVerifier.Verify(new IrFunction("bad-name!", IrValueType.I64,
    immutableSource, 0)), "IR rejects invalid function name");
Reject(() => OresIrVerifier.Verify(new IrFunction("bad-type", IrValueType.Bool,
    immutableSource, 0)), "IR rejects unsupported result type");
Reject(() => OresIrVerifier.Verify(new IrFunction("bad-id", IrValueType.I64,
    new[] { new IrInstruction(1, IrOpcode.ConstantI64, IrValueType.I64, Constant: 1) },
    0)), "IR rejects noncontiguous SSA IDs");
Reject(() => OresIrVerifier.Verify(new IrFunction("forward", IrValueType.I64,
    new[] { new IrInstruction(0, IrOpcode.CheckedAdd, IrValueType.I64,
        Left: 1, Right: 1) }, 0)), "IR rejects forward SSA references");
Reject(() => OresIrVerifier.Verify(new IrFunction("opcode", IrValueType.I64,
    new[] { new IrInstruction(0, (IrOpcode)999, IrValueType.I64) }, 0)),
    "IR rejects unknown arithmetic opcodes");
Reject(() => OresIrVerifier.Verify(new IrFunction("wrong-type", IrValueType.I64,
    new[] { new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.Bool,
        Constant: 1) }, 0)), "IR rejects type confusion");
Reject(() => OresIrVerifier.Verify(new IrFunction("bad-return", IrValueType.I64,
    immutableSource, 1)), "IR rejects nonexistent return IDs");

var unaryIr = OresIrLowering.Lower(OresParser.Parse(
    "fnc main():int{return -(3 + 4); }"));
Require(unaryIr.Instructions[^1].Opcode == IrOpcode.CheckedSubtract &&
        unaryIr.Instructions[^1].Left.HasValue,
    "unary minus lowers with checked subtraction");
Require(OresIrReferenceEvaluator.Evaluate(unaryIr) == -7,
    "typed IR checked unary negation semantics");
var flatIr = OresIrLowering.Lower(OresParser.Parse(
    "fnc main():int{return " +
    string.Join("+", Enumerable.Repeat("1", 2048)) + ";}"));
Require(flatIr.Instructions.Count == 4095 && flatIr.ReturnId == 4094,
    "long left-associative arithmetic lowers iteratively to valid SSA");
Require(OresIrReferenceEvaluator.Evaluate(flatIr) == 2048,
    "long left-associative IR retains arithmetic semantics");

static void RejectIrOverflow(string source, bool divideByZero)
{
    var typed = OresIrLowering.Lower(OresParser.Parse(source));
    try
    {
        OresIrReferenceEvaluator.Evaluate(typed);
    }
    catch (DivideByZeroException) when (divideByZero)
    {
        return;
    }
    catch (OverflowException) when (!divideByZero)
    {
        return;
    }
    throw new Exception("Expected IR arithmetic exception: " + source);
}
RejectIrOverflow("fnc main():int{return 9223372036854775807 + 1;}", false);
RejectIrOverflow("fnc main():int{return 9223372036854775807 * 2;}", false);


RejectIrOverflow("fnc main():int{return -(-9223372036854775808);}", false);

// check and compile share exactly the same verifier instruction limit;
// oversized typed SSA cannot pass check and fail only during CIL emission.
Reject(() => OresIrLowering.Lower(OresParser.Parse(
    "fnc main():int{return " +
    string.Join("+", Enumerable.Repeat("1", 8193)) + ";}")),
    "OresIR rejects a source requiring more than 16384 CIL locals");



string folder = Path.Combine(Path.GetTempPath(), "oresnet-tests-" + Guid.NewGuid().ToString("N"));
try
{
    string generated = Path.Combine(folder, "OresGenerated.dll");
    CilEmitter.EmitLibrary(positive, generated);
    Require(File.Exists(generated), "assembly emission");
    var reproducible = Path.Combine(folder, "OresGenerated.repro.dll");
    CilEmitter.EmitLibrary(positive, reproducible);
    Require(File.ReadAllBytes(generated).AsSpan().SequenceEqual(File.ReadAllBytes(reproducible)),
        "generated CIL must be byte-for-byte deterministic");

    var illegalOutput = Path.Combine(folder, "OresGenerated.invalid.dll");
    Reject(() => CilEmitter.EmitLibrary(
        new Binary('%', new Literal(2), new Literal(3)), illegalOutput),
        "unknown AST opcode is rejected");
    Require(!File.Exists(illegalOutput), "failed lowering must not publish a partial DLL");


    // Compiler-time metadata inspection only, never Assembly.Load or Reflection.Emit.
    using var assembly = AssemblyDefinition.ReadAssembly(generated);
    var entry = assembly.MainModule.Types.Single(t =>
        t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
    Require(entry.IsStatic && entry.IsPublic && entry.ReturnType.MetadataType == MetadataType.Int64,
        "static entry signature");
    // The i64 return is encoded with the ECMA-335 primitive ELEMENT_TYPE_I8,
    // not an AssemblyRef token. Test that separately from the TypeRef scope.
    Require(assembly.MainModule.AssemblyReferences.Any(r => r.Name == "System.Runtime"),
        "generated CIL references the target framework");
    Require(entry.DeclaringType.BaseType?.Scope is AssemblyNameReference
        { Name: "System.Runtime" }, "System.Object TypeRef resolves via System.Runtime");
    Require(entry.DeclaringType.BaseType?.MetadataType == MetadataType.Object,
        "System.Object CIL primitive type");

    var ops = entry.Body.Instructions.Select(i => i.OpCode.Code).ToArray();
    Require(ops.Contains(Code.Add_Ovf) && ops.Contains(Code.Mul_Ovf), "checked CIL operations");
    Require(ops[^1] == Code.Ret, "CIL return");
    Require(entry.Body.MaxStackSize is >= 2 and <= 256, "CIL stack bounds");
    // The original direct AST path remains a separate reference implementation
    // until real .NET builds confirm the new production OresIR path.
    Require(CilReferenceEvaluator.Evaluate(entry) ==
        OresIrReferenceEvaluator.Evaluate(lowered),
        "legacy AST CIL matches verified OresIR semantics");

    var irGenerated = Path.Combine(folder, "OresGenerated.ir.dll");
    CilEmitter.EmitLibrary(lowered, irGenerated);
    using var irAssembly = AssemblyDefinition.ReadAssembly(irGenerated);
    var irEntry = irAssembly.MainModule.Types.Single(t =>
        t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
    Require(irEntry.Body.Variables.Count == lowered.Instructions.Count,
        "OresIR CIL has one typed local for every SSA definition");
    Require(irEntry.Body.Variables.All(v => v.VariableType.MetadataType == MetadataType.Int64),
        "OresIR local variable signatures are all i64");
    Require(irEntry.Body.MaxStackSize == 2, "SSA lowering uses a measured two-slot stack");
    Require(CilReferenceEvaluator.Evaluate(irEntry) == 14,
        "verified SSA CIL retains arithmetic semantics");
    Require(irEntry.Body.Instructions.Count(i => i.OpCode.Code == Code.Add_Ovf) == 1 &&
            irEntry.Body.Instructions.Count(i => i.OpCode.Code == Code.Mul_Ovf) == 1,
        "OresIR emits checked CIL operations");

    var irRepro = Path.Combine(folder, "OresGenerated.ir.repro.dll");
    CilEmitter.EmitLibrary(lowered, irRepro);
    Require(File.ReadAllBytes(irGenerated).AsSpan().SequenceEqual(File.ReadAllBytes(irRepro)),
        "verified IR PE emission is byte-for-byte deterministic");

    // Compare the independent IR reference interpreter with the emitted CIL
    // for normal arithmetic, divide-by-zero and signed overflow.
    var samples = new[] {
        "fnc main():int{return -123;}",
        "fnc main():int{return +1;}",
        "fnc main():int{return (8-3)*5 + 2;}",
        "fnc main():int{return 100-(3+2) - 19;}",
        "fnc main():int{return -(3 * (4-5));}",
        "fnc main():int{return -9223372036854775808;}",
        "fnc main():int{return 9223372036854775807-1;}",
        "fnc main():int{return 3*4-2*5-7;}"
    };
    for (int i = 0; i < samples.Length; i++)
    {
        var fixtureAst = OresParser.Parse(samples[i]);
        var typedFixture = OresIrLowering.Lower(fixtureAst);
        var fixturePath = Path.Combine(folder, $"OresGenerated.fixture.{i}.dll");
        CilEmitter.EmitLibrary(typedFixture, fixturePath);
        using var fixtureAssembly = AssemblyDefinition.ReadAssembly(fixturePath);
        var fixtureEntry = fixtureAssembly.MainModule.Types.Single(t =>
            t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
        Require(CilReferenceEvaluator.Evaluate(fixtureEntry) ==
            OresIrReferenceEvaluator.Evaluate(typedFixture),
            $"differential i64 fixture {i}");
    }

    var errors = new[] {
        ("fnc main():int{return 9223372036854775807+1;}", false),
        ("fnc main():int{return -(-9223372036854775808);}", false)
    };
    for (int i = 0; i < errors.Length; i++)
    {
        var (source, divideZero) = errors[i];
        var irError = OresIrLowering.Lower(OresParser.Parse(source));
        var errorPath = Path.Combine(folder, $"OresGenerated.error.{i}.dll");
        CilEmitter.EmitLibrary(irError, errorPath);
        using var errorAssembly = AssemblyDefinition.ReadAssembly(errorPath);
        var errorEntry = errorAssembly.MainModule.Types.Single(t =>
            t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
        try
        {
            _ = CilReferenceEvaluator.Evaluate(errorEntry);
            throw new Exception($"Expected checked arithmetic error fixture {i}");
        }
        catch (DivideByZeroException) when (divideZero) { }
        catch (OverflowException) when (!divideZero) { }
    }

    var reusedIr = OresIrVerifier.Verify(new IrFunction("reuse", IrValueType.I64,
        new[] {
            new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 7),
            new IrInstruction(1, IrOpcode.CheckedAdd, IrValueType.I64, Left: 0, Right: 0),
            new IrInstruction(2, IrOpcode.CheckedMultiply, IrValueType.I64, Left: 1, Right: 1),
            new IrInstruction(3, IrOpcode.ConstantI64, IrValueType.I64, Constant: 99)
        }, 2));
    var reusedPath = Path.Combine(folder, "OresGenerated.reuse.dll");
    CilEmitter.EmitLibrary(reusedIr, reusedPath);
    using var reusedAssembly = AssemblyDefinition.ReadAssembly(reusedPath);
    var reusedMethod = reusedAssembly.MainModule.Types.Single(t =>
        t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
    Require(CilReferenceEvaluator.Evaluate(reusedMethod) == 196 &&
        OresIrReferenceEvaluator.Evaluate(reusedIr) == 196,
        "shared SSA operands and unused definitions preserve semantics");


    // Flat left-associated expressions are valid without recursive emitter
    // stack overflow; 2048 additions should require only a small CIL stack.
    var longSource = "fnc main():int{return " +
        string.Join("+", Enumerable.Repeat("1", 2048)) + ";}";
    var deepFlat = Path.Combine(folder, "OresGenerated.flat.dll");
    CilEmitter.EmitLibrary(OresParser.Parse(longSource), deepFlat);
    using var flatAssembly = AssemblyDefinition.ReadAssembly(deepFlat);
    var flatMethod = flatAssembly.MainModule.Types.Single(t =>
        t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
    Require(flatMethod.Body.MaxStackSize <= 3, "flat expression stack bound");
    Require(flatMethod.Body.Instructions.Count(i => i.OpCode.Code == Code.Add_Ovf) == 2047,
        "flat expression lowering");
    Require(flatMethod.Body.Instructions.Last().OpCode.Code == Code.Ret,
        "flat expression must have terminal return");
    var longIrPath = Path.Combine(folder, "OresGenerated.flat.ir.dll");
    CilEmitter.EmitLibrary(flatIr, longIrPath);
    using var longIrAssembly = AssemblyDefinition.ReadAssembly(longIrPath);
    var longIrMethod = longIrAssembly.MainModule.Types.Single(t =>
        t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
    Require(longIrMethod.Body.Variables.Count == 4095 &&
        longIrMethod.Body.MaxStackSize <= 2,
        "4,095 SSA values are lowered with bounded CIL stack");
    Require(CilReferenceEvaluator.Evaluate(longIrMethod) == 2048,
        "4,095-value CIL agrees with independent reference semantics");



    // Artificially assembled right-deep trees cannot demand arbitrary CIL stack.
    Expression pathological = new Literal(1);
    for (var i = 0; i < 258; i++)
        pathological = new Binary('+', new Literal(1), pathological);
    Reject(() => CilEmitter.EmitLibrary(pathological,
        Path.Combine(folder, "OresGenerated.pathological.dll")), "CIL stack budget");

}
finally
{
    if (Directory.Exists(folder)) Directory.Delete(folder, true);
}

static void RequireInvalidLimit(Action action, string name)
{
    try
    {
        action();
    }
    catch (ArgumentOutOfRangeException ex) when (ex.ParamName == name)
    {
        return;
    }
    throw new Exception("Expected invalid mailbox parameter: " + name);
}
RequireInvalidLimit(() => new ActorMailbox(0), "id");
RequireInvalidLimit(() => new ActorMailbox(1, maxMessages: 0), "maxMessages");
RequireInvalidLimit(() => new ActorMailbox(1, maxQueuedBytes: 0), "maxQueuedBytes");

var mailbox = new ActorMailbox(42, maxMessages: 1, maxQueuedBytes: 4);
var before = mailbox.CurrentRef;
var value = new byte[] { 1, 2, 3 };
mailbox.Send(before, value);
value[0] = 99;
InvalidSend(() => mailbox.Send(before, value), "bounded queue");
Require(mailbox.TryReceive(out var copy) && copy is not null && copy[0] == 1,
    "mailbox owns payload bytes");
Require(!mailbox.TryReceive(out _), "queue drained");
// A different mailbox cannot spoof even a matching numeric actor ID/generation.
var wrongActor = new ActorMailbox(43, maxMessages: 1, maxQueuedBytes: 4);
InvalidSend(() => mailbox.Send(wrongActor.CurrentRef, value), "wrong actor");
var duplicateActor = new ActorMailbox(42, maxMessages: 1, maxQueuedBytes: 4);
InvalidSend(() => mailbox.Send(duplicateActor.CurrentRef, value),
    "same numeric ID and generation must not forge mailbox capability");
Require(!before.ToString().Contains("Nonce", StringComparison.OrdinalIgnoreCase),
    "actor reference string must not disclose nonce");
mailbox.Stop();
InvalidSend(() => mailbox.Send(before, value), "stopped");
var after = mailbox.Restart();
Require(after.Generation > before.Generation, "new generation");
Require(after != before, "restart rotates local actor capability");
Require(after.Id == before.Id, "restart preserves actor identity");
InvalidSend(() => mailbox.Send(default, value), "default actor ref must be rejected");
Require(after.ToString().Contains("Generation", StringComparison.Ordinal),
    "actor ref diagnostics retain safe generation field");
InvalidSend(() => mailbox.Send(duplicateActor.CurrentRef, value),
    "other mailbox capability remains invalid after restart");
InvalidSend(() => mailbox.Send(before, value), "stale reference");
mailbox.Send(after, value);
Require(mailbox.QueuedMessages == 1, "new generation send");
mailbox.Stop();
Require(mailbox.QueuedMessages == 0, "stop clears messages");
var concurrent = new ActorMailbox(99, maxMessages: 200, maxQueuedBytes: 200);
var concurrentRef = concurrent.CurrentRef;
Parallel.For(0, 200, i =>
{
    concurrent.Send(concurrentRef, new byte[] { (byte)i });
});
Require(concurrent.QueuedMessages == 200, "linearizable concurrent sends");
var seen = new bool[200];
for (int i = 0; i < 200; i++)
{
    Require(concurrent.TryReceive(out var received) && received is not null,
        "concurrent receive");
    seen[received![0]] = true;
}
Require(seen.All(x => x) && concurrent.QueuedMessages == 0,
    "concurrent delivery exactly once");

// Reflection is used ONLY by this managed test executable. It is not part of
// the Native AOT runtime: inspect the queued owned array after eviction.
static byte[] PendingOwnedBuffer(ActorMailbox actor)
{
    var field = typeof(ActorMailbox).GetField("_messages",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Require(field is not null, "mailbox test can inspect private queue");
    return ((Queue<byte[]>)field!.GetValue(actor)!).Peek();
}

var originalInput = new byte[] { 9, 8, 7, 6 };
var stopActor = new ActorMailbox(101);
stopActor.Send(stopActor.CurrentRef, originalInput);
var pendingOnStop = PendingOwnedBuffer(stopActor);
Require(!ReferenceEquals(originalInput, pendingOnStop), "send makes an owned copy");
stopActor.Stop();
Require(pendingOnStop.All(b => b == 0), "stop zeroizes undelivered bytes");
Require(originalInput.SequenceEqual(new byte[] { 9, 8, 7, 6 }),
    "stop never wipes sender input");

var restartActor = new ActorMailbox(102);
restartActor.Send(restartActor.CurrentRef, originalInput);
var pendingOnRestart = PendingOwnedBuffer(restartActor);
var stale = restartActor.CurrentRef;
restartActor.Restart();
Require(pendingOnRestart.All(b => b == 0), "restart zeroizes undelivered bytes");
InvalidSend(() => restartActor.Send(stale, originalInput),
    "restart rejects previously valid sender capability");
Require(restartActor.QueuedMessages == 0, "restart clears pending messages");



int count = 0;
Require(RuntimeHost.Execute(() => { count++; return 7; }) == 0 && count == 1,
    "runtime host invokes entry once");
Require(RuntimeHost.Execute(() => throw new OverflowException()) == 70,
    "overflow error returned");
Require(RuntimeHost.Execute(() => throw new DivideByZeroException()) == 70,
    "divide zero error returned");

SourceI64Conformance.Run();
CfgVerifierTests.Run();
CfgCilStressTests.Run();
StructuredSourceTests.Run();
ValueBoundaryTests.Run();
Console.WriteLine("Oreslang .NET bootstrap, CFG verifier, CIL stress and structured source tests passed");
