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

static void InvalidSend(Action action, string why)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Expected mailbox rejection: " + why);
}

var positive = OresParser.Parse("define pub fnc main() : int { return 2 + 3 * 4; }");
Require(positive is Binary { Operator: '+' }, "top-level expression shape");
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
RejectIrOverflow("fnc main():int{return -9223372036854775808 / -1;}", false);
RejectIrOverflow("fnc main():int{return 5 / 0;}", true);
RejectIrOverflow("fnc main():int{return -(-9223372036854775808);}", false);


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
    Require(entry.ReturnType.Scope is AssemblyNameReference { Name: "System.Runtime" },
        "primitive i64 CIL type points to System.Runtime");
    Require(entry.DeclaringType.BaseType?.MetadataType == MetadataType.Object,
        "System.Object CIL primitive type");

    var ops = entry.Body.Instructions.Select(i => i.OpCode.Code).ToArray();
    Require(ops.Contains(Code.Add_Ovf) && ops.Contains(Code.Mul_Ovf), "checked CIL operations");
    Require(ops[^1] == Code.Ret, "CIL return");
    Require(entry.Body.MaxStackSize is >= 2 and <= 256, "CIL stack bounds");

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

Console.WriteLine("Oreslang .NET bootstrap tests passed");
