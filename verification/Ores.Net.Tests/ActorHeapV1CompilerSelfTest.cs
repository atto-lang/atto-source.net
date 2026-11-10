using Ores.Net.Compiler;

internal static class ActorHeapV1CompilerSelfTest
{
    public static void Run()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "contracts",
            "actor-guest-heap", "v1", "node_graph.atto");
        string source = File.ReadAllText(fixture);
        var plan = ActorHeapV1Compiler.Parse(source);
        Require(plan.ClassName == "Node" &&
                plan.ActorName == "HeapGraphProbe" &&
                plan.ValueField == "value" && plan.NextField == "next" &&
                plan.MessageName == "message" &&
                plan.FirstVariable == "first" &&
                plan.SecondVariable == "second" &&
                plan.FirstValue == 42 && plan.SecondValue == 99 &&
                plan.ExpectedValue == 42,
            "real .atto fixture produces checked nominal graph plan");

        string generated = ActorHeapV1Compiler.EmitCSharp(source);
        Require(generated == ActorHeapV1Compiler.EmitCSharp(source),
            "source emission is deterministic");
        Require(generated.Contains("namespace Ores.Net.Generated;", StringComparison.Ordinal) &&
                generated.Contains("public static void receive(ActorContext context, int message)",
                    StringComparison.Ordinal) &&
                generated.Contains("AllocateTypedGuestObject32(layout)", StringComparison.Ordinal) &&
                generated.Contains("WriteGuestReference32(second, 8, first)", StringComparison.Ordinal) &&
                generated.Contains("ReadGuestReference32(second, 8)", StringComparison.Ordinal) &&
                generated.Contains("WriteInt64LittleEndian(value, 42L)", StringComparison.Ordinal) &&
                generated.Contains("WriteInt64LittleEndian(value, 99L)", StringComparison.Ordinal) &&
                generated.Contains("ReadInt64LittleEndian(value) != 42L", StringComparison.Ordinal) &&
                generated.Contains("finally", StringComparison.Ordinal) &&
                generated.Contains("DropGuestObject32(second)", StringComparison.Ordinal) &&
                generated.Contains("DropGuestObject32(first)", StringComparison.Ordinal),
            "AOT C# is derived from typed AttoLang source and scoped cleanup");
        Require(generated.Split("finally", StringSplitOptions.None).Length >= 4,
            "generated nested finally guards must attempt both native drops");

        string changed = source.Replace("new Node(42, none)", "new Node(-7, none)",
                StringComparison.Ordinal)
            .Replace("new Node(99, first)", "new Node(101, first)", StringComparison.Ordinal)
            .Replace("== 42", "== -7", StringComparison.Ordinal);
        string changedOutput = ActorHeapV1Compiler.EmitCSharp(changed);
        Require(changedOutput.Contains("WriteInt64LittleEndian(value, -7L)",
                    StringComparison.Ordinal) &&
                changedOutput.Contains("WriteInt64LittleEndian(value, 101L)",
                    StringComparison.Ordinal) &&
                changedOutput.Contains("ReadInt64LittleEndian(value) != -7L",
                    StringComparison.Ordinal) &&
                changedOutput != generated,
            "compiler output follows source value semantics, not a hard-coded Node fixture");
        Require(ActorHeapV1Compiler.EmitCSharp(
                source.Replace("42, none", "-9223372036854775808, none",
                    StringComparison.Ordinal)
                    .Replace("== 42", "== -9223372036854775808",
                        StringComparison.Ordinal))
                .Contains("long.MinValue", StringComparison.Ordinal),
            "minimal signed i64 literal is emitted without an overflowing token");

        // The *production* structured compiler must continue refusing this
        // nominal source until its general nominal CFG/CIL path is implemented.
        RejectStructured(source);

        foreach (string malicious in new[]
        {
            source.Replace("Node? next", "Other? next", StringComparison.Ordinal),
            source.Replace("let second =", "let first =", StringComparison.Ordinal),
            source.Replace("let first =", "let return =", StringComparison.Ordinal)
                  .Replace("new Node(99, first)", "new Node(99, return)",
                      StringComparison.Ordinal),
            source.Replace("new Node(99, first)", "new Node(99, message)",
                StringComparison.Ordinal),
            source.Replace("assert(second.next?.value == 42)",
                "assert(first.next?.value == 42)", StringComparison.Ordinal),
            source.Replace("assert(second.next?.value == 42)",
                "assert(second.next?.next == 42)", StringComparison.Ordinal),
            source.Replace("assert(second.next?.value == 42)",
                "assert(second.next?.value == 42); write_global(secret)", StringComparison.Ordinal),
            source.Replace("new Node(42, none)", "new Node(42, none); actor_ref.send(42)",
                StringComparison.Ordinal),
            source.Replace("new Node(42, none)", "new Node(42, none) /* unclosed",
                StringComparison.Ordinal),
            source + "\ndefine class Rogue as int value end",
            source.Replace("new Node(99, first)", "new Node(9223372036854775808, first)",
                StringComparison.Ordinal),
            source.Replace("new Node(42, none)", "new Node(0xFFFF, none)",
                StringComparison.Ordinal),
            source.Replace("define class Node", "define class Node<T>",
                StringComparison.Ordinal),
            source.Replace("receive(int message)", "receive(int message, int other)",
                StringComparison.Ordinal),
            source.Replace("Node? next", "Node next", StringComparison.Ordinal),
            source.Replace("Node? next", "Node? next\nint extra", StringComparison.Ordinal),
            source.Replace("define actor HeapGraphProbe", "define pub actor HeapGraphProbe",
                StringComparison.Ordinal)
        })
            RejectV1(malicious);

        var withComment = "// fake: define actor Other as end\n" +
            source.Replace("define actor HeapGraphProbe",
                "/* ordinary /* nested */ comment */\ndefine actor HeapGraphProbe",
                StringComparison.Ordinal);
        Require(ActorHeapV1Compiler.EmitCSharp(withComment) == generated,
            "nested comments never become grammar tokens or emitted guest operations");

        RejectV1(new string('x', ActorHeapV1Compiler.MaxSourceBytes + 1));
        Console.WriteLine(
            "PASS actor heap v1 .atto checked compiler parser/emitter (source-only; no guest execution)");
    }

    private static void RejectV1(string source)
    {
        try { _ = ActorHeapV1Compiler.EmitCSharp(source); }
        catch (FormatException) { return; }
        throw new Exception("ActorHeapV1Compiler unexpectedly admitted unsupported syntax");
    }

    private static void RejectStructured(string source)
    {
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException ex)
            when (ex.Message.Contains("Nominal class/actor methods require",
                StringComparison.Ordinal)) { return; }
        throw new Exception("Production structured compiler bypassed nominal feature gate");
    }

    private static void Require(bool condition, string why)
    {
        if (!condition)
            throw new Exception("ActorHeapV1CompilerSelfTest FAILED: " + why);
    }
}
