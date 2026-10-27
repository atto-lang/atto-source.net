using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

internal static class GeneratorV1FailClosedTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        foreach (var source in new[] {
            "pub gen* values(): Iterator<int> { yield 1; }",
            "pub async gen* values(): AsyncIterator<int> { yield 1; }",
            "pub suspend gen* values(): AsyncIterator<int> { yield 1; }",
            "generator fnc values(): int { yield 1; }"
        })
        {
            try
            {
                _ = OresParser.Parse(source);
            }
            catch (FormatException ex) when (
                ex.Message.Contains(
                    "generator declarations require iterator-frame and continuation lowering",
                    StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("generator-v1 syntax was not rejected by the explicit Native AOT gate");
        }
    }
}
