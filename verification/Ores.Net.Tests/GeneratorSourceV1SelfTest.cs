using System.Runtime.CompilerServices;
using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class GeneratorSourceV1SelfTest
{
    [ModuleInitializer]
    internal static void Run()
    {
        const string source = """
            // canonical source, not a synthetic host callback
            pub gen* values(): Iterator<int> {
              yield 20;
              /* nested /* comment */ stays lexical */
              yield -2;
              yield 24;
              return;
            }
            """;

        var compiled = OresGeneratorV1Compiler.Compile(source);
        Require(compiled.IsPublic, "generator visibility");
        Require(compiled.Name == "values", "generator name");
        Require(compiled.YieldCount == 3, "generator yield count");
        Require(compiled.YieldAt(0) == 20 && compiled.YieldAt(1) == -2 &&
            compiled.YieldAt(2) == 24, "compiled yield table");

        using (var activation = new LiteralGeneratorActivationV1(compiled.CopyYields()))
        {
            long total = 0;
            foreach (var expected in new long[] { 20, -2, 24 })
            {
                Require(activation.Next(out long value) == 1, "missing generator yield");
                Require(value == expected, "generator value/order");
                total = checked(total + value);
            }
            Require(total == 42, "source generator semantic oracle");
            Require(activation.Next(out _) == 0, "missing generator DONE");
            Require(activation.IsTerminal, "terminal state");
            Require(activation.Next(out _) == 0, "DONE not sticky");
            Require(activation.Close() == 0, "close after DONE changed state");
        }

        var bounds = OresGeneratorV1Compiler.Compile(
            "gen* bounds(): Iterator<int> { yield 9223372036854775807; yield -9223372036854775808; }");
        Require(bounds.YieldAt(0) == long.MaxValue && bounds.YieldAt(1) == long.MinValue,
            "full signed i64 yield range");

        var triviaSigned = OresGeneratorV1Compiler.Compile(
            "gen* spaced(): Iterator<int> { yield - /* trivia */ 2; return; }");
        Require(triviaSigned.YieldAt(0) == -2,
            "unary minus must preserve ordinary trivia/token boundaries");

        // One compiled declaration may create independent lazy activations.
        using (var first = new LiteralGeneratorActivationV1(compiled.CopyYields()))
        using (var second = new LiteralGeneratorActivationV1(compiled.CopyYields()))
        {
            Require(first.Next(out long a) == 1 && a == 20, "first activation");
            Require(second.Next(out long b) == 1 && b == 20, "second activation");
            Require(first.Next(out long c) == 1 && c == -2, "activation state leaked");
            Require(second.Close() == 1, "early close");
            Require(second.Next(out _) == 0 && second.Close() == 0,
                "early close not terminal/idempotent");
        }

        // The ordinary scalar frontend remains fail-closed. Generator support
        // is not silently smuggled into OresParser's scalar Expression return.
        ExpectReject(() => OresParser.Parse(source),
            "generator declarations require iterator-frame and continuation lowering");

        foreach (var invalid in new[] {
            "pub async gen* values(): AsyncIterator<int> { yield 1; return; }",
            "pub suspend gen* values(): AsyncIterator<int> { yield 1; return; }",
            "pub gen* values(int n): Iterator<int> { yield 1; return; }",
            "pub gen* values(): Iterator<int> { yield* other; return; }",
            "pub gen* values(): Iterator<int> { return 1; }",
            "pub gen* values(): AsyncIterator<int> { yield 1; return; }",
            "pub fnc gen* values(): Iterator<int> { yield 1; return; }",
            "pub gen* values(): Iterator<int> { yield 1 + 2; return; }",
            "pub gen* if(): Iterator<int> { yield 1; return; }",
            "pub gen* routine(): Iterator<int> { yield 1; return; }"
        })
            ExpectReject(() => OresGeneratorV1Compiler.Compile(invalid), null);
    }

    private static void Require(bool condition, string why)
    {
        if (!condition) throw new Exception("generator source v1 failed: " + why);
    }

    private static void ExpectReject(Action action, string? diagnostic)
    {
        try
        {
            action();
        }
        catch (FormatException ex) when (
            diagnostic is null || ex.Message.Contains(diagnostic, StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("generator source v1 unexpectedly admitted invalid source");
    }
}
