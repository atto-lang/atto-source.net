using Ores.Net.Compiler;

internal static class SingletonDeclarationAdmissionTests
{
    public static void Run()
    {
        foreach (var source in new[]
        {
            "define singleton Settings as end",
            "define pub singleton Settings as end",
            "pub define singleton Settings as end",
            "singleton Settings as end",
            "pub singleton Settings as end",
            "define /* nested /* comment */ marker */ singleton Settings as end"
        })
        {
            RejectSingleton(() => { _ = OresParser.Parse(source); }, "scalar/" + source);
            RejectSingleton(() => { _ = OresStructuredParser.Parse(source); }, "structured/" + source);
        }

        // Shared candidate bytes: injected Stateful and inline sealed state.
        // Both shipping parsers must reject at the singleton capability boundary
        // before unsupported @Inject/trait/member syntax is encountered.
        foreach (var source in new[]
        {
            "define singleton StateBank implements Stateful<BankState> as\n"
                + "    @Inject\n"
                + "    state: MutableStateContainer<BankState>\n"
                + "end\n",
            "define singleton StateBank as\n"
                + "    state { let count: int = 0 }\n"
                + "end\n"
        })
        {
            RejectSingleton(() => { _ = OresParser.Parse(source); }, "scalar/managed/" + source);
            RejectSingleton(() => { _ = OresStructuredParser.Parse(source); }, "structured/managed/" + source);
        }

        // Local declarations must fail at the singleton admission boundary,
        // not at an incidental assignment/statement parse error.
        foreach (var source in new[]
        {
            "fnc main():int{define singleton Local as end return 42;}",
            "fnc main():int{define pub singleton Local as end return 42;}",
            "fnc main():int{pub define singleton Local as end return 42;}"
        })
            RejectSingleton(() => { _ = OresStructuredParser.Parse(source); }, "structured/local/" + source);

        // Comments must not be mistaken for executable declarations.
        _ = OresParser.Parse("fnc main():int{return 42;} // define singleton CommentOnly as end");
        _ = OresStructuredParser.Parse(
            "fnc singleton():int{return 42;} fnc main():int{return singleton();} " +
            "// define singleton CommentOnly as end");
        _ = OresParser.Parse("fnc main():int{return 42;} /* define /* pub singleton */ Ignored as end */");
        _ = OresStructuredParser.Parse(
            "fnc main():int{let int singleton = 42; return singleton;} " +
            "/* define /* pub singleton */ Ignored as end */");
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
