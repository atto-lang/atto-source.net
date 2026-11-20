using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class Program
{
    private static int Main()
    {
        const string source = """
            pub gen* values(): Iterator<int> {
              yield 1;
              yield -9223372036854775808;
              yield 9223372036854775807;
              return;
            }
            """;

        var compiled = OresGeneratorV1Compiler.Compile(source);
        Require(compiled.IsPublic, "visibility");
        Require(compiled.Name == "values", "name");
        Require(compiled.YieldCount == 3, "yield count");
        Require(compiled.YieldAt(0) == 1, "first yield");
        Require(compiled.YieldAt(1) == long.MinValue, "INT64_MIN");
        Require(compiled.YieldAt(2) == long.MaxValue, "INT64_MAX");

        using (var activation = new LiteralGeneratorActivationV1(compiled.CopyYields()))
        {
            foreach (long expected in new[] { 1L, long.MinValue, long.MaxValue })
            {
                Require(activation.Next(out long value) == 1, "missing yield");
                Require(value == expected, "yield value/order");
            }
            Require(activation.Next(out _) == 0, "missing DONE");
            Require(activation.Next(out _) == 0, "DONE not sticky");
            Require(activation.Close() == 0, "close after DONE changed state");
            Require(activation.IsTerminal, "terminal state");
        }

        using (var early = new LiteralGeneratorActivationV1(new long[] { 7, 8 }))
        {
            Require(early.Next(out long first) == 1 && first == 7, "early-close setup");
            Require(early.Close() == 1, "early close");
            Require(early.Close() == 0, "close not idempotent");
            Require(early.Next(out _) == 0, "pull after close not DONE");
        }

        var frame = default(GeneratorFrameV1);
        Require(frame.Pull(1, out _) == -1, "fresh frame pull accepted");
        Require(frame.Close() == -1, "fresh frame close accepted");
        Require(frame.Initialize() == 1 && frame.Initialize() == 0, "frame init contract");
        Require(frame.Pull(1, out int slot) == 1 && slot == 0, "frame slot");
        Require(frame.Pull(1, out _) == 0 && frame.IsTerminal, "frame terminal");

        foreach (string reserved in new[] {
            "define", "class", "enum", "pattern", "module", "contract", "conforms",
            "namespace", "gpu", "cpu", "quantum", "over", "structural", "bool", "pure",
            "Some", "None", "Ok", "Err", "using", "requires", "serial"
        })
        {
            ExpectFormat(
                () => OresGeneratorV1Compiler.Compile(
                    $"gen* {reserved}(): Iterator<int> {{ yield 1; return; }}"),
                "Reserved generator identifier");
        }

        ExpectFormat(
            () => OresGeneratorV1Compiler.Compile(new string('x', 1024 * 1024 + 1)),
            "Source exceeds 1 MiB UTF-8 generator compiler limit");

        ExpectFormat(
            () => OresGeneratorV1Compiler.Compile(
                "gen* values(int n): Iterator<int> { yield 1; return; }"),
            "zero-argument");
        ExpectFormat(
            () => OresGeneratorV1Compiler.Compile(
                "async gen* values(): AsyncIterator<int> { yield 1; return; }"),
            "synchronous gen* only");
        ExpectFormat(
            () => OresGeneratorV1Compiler.Compile(
                "gen* values(): Iterator<int> { yield 9223372036854775808; return; }"),
            "signed 64-bit");

        Console.WriteLine("DOTNET_GENERATOR_V1_PASS");
        return 0;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("generator v1 verification failed: " + reason);
    }

    private static void ExpectFormat(Action action, string diagnostic)
    {
        try
        {
            action();
        }
        catch (FormatException ex) when (ex.Message.Contains(diagnostic, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException(
            "generator v1 verification unexpectedly admitted invalid input: " + diagnostic);
    }
}
