using Ores.Net.Compiler;
using System.Text.Json;

internal static class ValueBoundaryTests
{
    public static void Run()
    {
        const string control = "pub fnc main():int{return (5+3)*7-14;}";
        _ = OresParser.Parse(control);
        _ = OresStructuredParser.Parse(control);
        var path = Path.Combine(AppContext.BaseDirectory, "value-boundary-rejections.json");
        var cases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new Exception("Missing boundary corpus");
        if (cases.Count != 13) throw new Exception("Incomplete boundary corpus");
        foreach (var (name, source) in cases)
        {
            Reject(() => { _ = OresParser.Parse(source); }, name + "/shipping");
            Reject(() => { _ = OresStructuredParser.Parse(source); }, name + "/structured");
        }
        Console.WriteLine($"{cases.Count} value boundary cases rejected by both frontends");
    }

    private static void Reject(Action parse, string name)
    {
        try { parse(); }
        catch (FormatException error)
        {
            if (string.IsNullOrWhiteSpace(error.Message))
                throw new Exception(name + ": missing diagnostic");
            return;
        }
        throw new Exception(name + ": unsupported source admitted");
    }
}
