using System.Text.Json;
using Ores.Net.Compiler;

// Compiler admission test, not host simulation of Oreslang monads.
internal static class ElmCompositionAdmissionTests
{
    public static void Run()
    {
        const string control = "pub fnc main():int{return (5+3)*7-14;}";
        _ = OresParser.Parse(control);
        _ = OresStructuredParser.Parse(control);

        var path = Path.Combine(AppContext.BaseDirectory, "elm-composition-rejections.json");
        var cases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new Exception("Missing Elm composition admission corpus");
        if (cases.Count != 16)
            throw new Exception("Expected sixteen distinct composition and failure admission cases");

        foreach (var (name, source) in cases)
        {
            if (!name.Contains('_') || source.Contains(".flatMap(", StringComparison.Ordinal)
                || source.Contains(".onSuccess(", StringComparison.Ordinal))
                throw new Exception("Noncanonical composition fixture: " + name);

            Reject(() => { _ = OresParser.Parse(source); }, name + "/shipping");
            Reject(() => { _ = OresStructuredParser.Parse(source); }, name + "/structured");
        }

        Console.WriteLine($"{cases.Count} reactive composition source cases rejected by both .NET frontends");
    }

    private static void Reject(Action operation, string name)
    {
        try { operation(); }
        catch (FormatException failure)
        {
            if (string.IsNullOrWhiteSpace(failure.Message))
                throw new Exception(name + ": expected actionable parser diagnostic");
            return;
        }
        throw new Exception(name + ": unsupported source unexpectedly admitted");
    }
}
