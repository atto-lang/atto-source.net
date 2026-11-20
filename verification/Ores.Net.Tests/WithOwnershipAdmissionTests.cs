using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

// Real scalar+structured C# parser admission only; no Native AOT execution.
internal static class WithOwnershipAdmissionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        string[] rejected = {
            "pub fnc main():int{return 42 with {x => 1};}",
            "fnc main():int{return 42 with /* nested /* block */ comment */ {x => 1};}",
            "fnc main():int{return 42 with\n{x => 1};}"
        };
        foreach (string source in rejected)
        {
            Reject(() => OresParser.Parse(source), "scalar");
            Reject(() => OresStructuredParser.Parse(source), "structured");
        }
        const string inert = "fnc main():int{/* with {x => 1} */ return 42;} // with {x => 5}";
        if (OresParser.Parse(inert) is not Literal { Value: 42 })
            throw new Exception("Inert with comment changed scalar parsing");
        if (OresStructuredParser.Parse(inert).Body.Count == 0)
            throw new Exception("Inert with comment changed structured source");
        Console.WriteLine("WITH_NET_FRONTEND_GATE_PASS 6 strict rejections + 2 inert controls");
    }
    private static void Reject(Action parse, string frontend)
    {
        try { parse(); }
        catch (FormatException error)
            when (error.Message.Contains("WITH_BACKEND_UNSUPPORTED", StringComparison.Ordinal))
        { return; }
        throw new Exception("Missing WITH_BACKEND_UNSUPPORTED from " + frontend);
    }
}
