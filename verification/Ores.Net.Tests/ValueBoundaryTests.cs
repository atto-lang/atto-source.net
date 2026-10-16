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
        // Positive control: comments containing reserved words remain comments.
        const string ignored = "fnc main():int{return 42;} // cpu gpu nb";
        _ = OresParser.Parse(ignored);
        _ = OresStructuredParser.Parse(ignored);
        var domains = new (string Name, string Source, string Keyword)[]
        {
            ("cpu expression", "fnc main():int{return cpu { 1 };}", "cpu"),
            ("gpu expression", "fnc main():int{return gpu { 1 };}", "gpu"),
            ("nested cpu-gpu", "fnc main():int{return cpu { gpu { 1 } };}", "cpu"),
            ("nested gpu-cpu-gpu", "fnc main():int{return gpu { cpu { gpu { 1 } } };}", "gpu"),
            ("nb gpu", "fnc main():int{return nb gpu { 1 };}", "nb"),
            ("nb cpu", "fnc main():int{return nb cpu { 1 };}", "nb"),
            ("reserved identifier", "fnc main():int{return gpu;}", "gpu")
        };
        foreach (var (name, source, keyword) in domains)
        {
            RejectDomain(() => { _ = OresParser.Parse(source); }, name + "/shipping", keyword);
            RejectDomain(() => { _ = OresStructuredParser.Parse(source); }, name + "/structured", keyword);
        }
        Console.WriteLine($"{cases.Count} value boundary cases and {domains.Length} execution-region cases rejected by both frontends");
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
