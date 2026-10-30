using Ores.Net.Compiler;

internal static class SingletonDeclarationAdmissionTests
{
    public static void Run()
    {
        foreach (var source in new[]
        {
            "define singleton Settings as end",
            "define pub singleton Settings as end",
            "pub define singleton Settings as end"
        })
        {
            RejectSingleton(() => { _ = OresParser.Parse(source); }, "scalar/" + source);
            RejectSingleton(() => { _ = OresStructuredParser.Parse(source); }, "structured/" + source);
        }

        // Comments must not be mistaken for executable declarations.
        _ = OresParser.Parse("fnc main():int{return 42;} // define singleton CommentOnly as end");
        _ = OresStructuredParser.Parse(
            "fnc singleton():int{return 42;} fnc main():int{return singleton();} " +
            "// define singleton CommentOnly as end");
    }

    private static void RejectSingleton(Action parse, string name)
    {
        try { parse(); }
        catch (FormatException error) when (
            error.Message.Contains("ORES-SINGLETON-UNSUPPORTED", StringComparison.Ordinal) &&
            error.Message.Contains("five-backend guest execution parity", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception(name + ": singleton declaration did not hit the compiler-owned admission gate");
    }
}
