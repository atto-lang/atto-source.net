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
        if (cases.Count != 15) throw new Exception("Incomplete boundary corpus");
        foreach (var (name, source) in cases)
        {
            Reject(() => { _ = OresParser.Parse(source); }, name + "/shipping");
            Reject(() => { _ = OresStructuredParser.Parse(source); }, name + "/structured");
        }
        // Separate, identical v1 domain corpus is shared with all five backends.
        // These are REJECTION tests only until typed region lowering is implemented.
        var domainPath = Path.Combine(AppContext.BaseDirectory, "cpu-gpu-nb-unsupported-v1.json");
        var domainCases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(domainPath))
            ?? throw new Exception("Missing execution-domain corpus");
        if (domainCases.Count != 10) throw new Exception("Incomplete execution-domain corpus");
        foreach (var (name, source) in domainCases)
        {
            RejectDomain(() => { _ = OresParser.Parse(source); }, name + "/shipping");
            Reject(() => { _ = OresStructuredParser.Parse(source); }, name + "/structured");
        }
        Console.WriteLine($"{cases.Count} value boundary cases and {domainCases.Count} CPU/GPU/nb domain cases rejected by both frontends");
    }

    private static void RejectDomain(Action parse, string name)
    {
        try { parse(); }
        catch (FormatException error) when
            (error.Message.Contains("unsupported execution domain", StringComparison.Ordinal) &&
             error.Message.Contains("no implicit CPU fallback", StringComparison.Ordinal))
        { return; }
        throw new Exception(name + ": missing typed execution-domain diagnostic");
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
