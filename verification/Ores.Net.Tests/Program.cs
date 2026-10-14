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

string folder = Path.Combine(Path.GetTempPath(), "oresnet-tests-" + Guid.NewGuid().ToString("N"));
try
{
    string generated = Path.Combine(folder, "OresGenerated.dll");
    CilEmitter.EmitLibrary(positive, generated);
    Require(File.Exists(generated), "assembly emission");

    // Compiler-time metadata inspection only, never Assembly.Load or Reflection.Emit.
    using var assembly = AssemblyDefinition.ReadAssembly(generated);
    var entry = assembly.MainModule.Types.Single(t =>
        t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
    Require(entry.IsStatic && entry.IsPublic && entry.ReturnType.MetadataType == MetadataType.Int64,
        "static entry signature");
    var ops = entry.Body.Instructions.Select(i => i.OpCode.Code).ToArray();
    Require(ops.Contains(Code.Add_Ovf) && ops.Contains(Code.Mul_Ovf), "checked CIL operations");
    Require(ops[^1] == Code.Ret, "CIL return");
}
finally
{
    if (Directory.Exists(folder)) Directory.Delete(folder, true);
}

var mailbox = new ActorMailbox(42, maxMessages: 1, maxQueuedBytes: 4);
var before = mailbox.CurrentRef;
var value = new byte[] { 1, 2, 3 };
mailbox.Send(before, value);
value[0] = 99;
InvalidSend(() => mailbox.Send(before, value), "bounded queue");
Require(mailbox.TryReceive(out var copy) && copy is not null && copy[0] == 1,
    "mailbox owns payload bytes");
Require(!mailbox.TryReceive(out _), "queue drained");
InvalidSend(() => mailbox.Send(new ActorRef(43, before.Generation), value), "wrong actor");
mailbox.Stop();
InvalidSend(() => mailbox.Send(before, value), "stopped");
var after = mailbox.Restart();
Require(after.Generation > before.Generation, "new generation");
InvalidSend(() => mailbox.Send(before, value), "stale reference");
mailbox.Send(after, value);
Require(mailbox.QueuedMessages == 1, "new generation send");

int count = 0;
Require(RuntimeHost.Execute(() => { count++; return 7; }) == 0 && count == 1,
    "runtime host invokes entry once");
Require(RuntimeHost.Execute(() => throw new OverflowException()) == 70,
    "overflow error returned");
Require(RuntimeHost.Execute(() => throw new DivideByZeroException()) == 70,
    "divide zero error returned");

Console.WriteLine("Oreslang .NET bootstrap tests passed");
