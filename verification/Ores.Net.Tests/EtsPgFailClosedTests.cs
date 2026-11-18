using Ores.Net.Compiler;

internal static class EtsPgFailClosedTests
{
    internal static void Run()
    {
        Action<string>[] parsers = [
            source => { _ = OresParser.Parse(source); },
            source => { _ = OresStructuredParser.Parse(source); }
        ];
        foreach (var parse in parsers)
        {
            foreach (var expression in new[] {
                "ets.new(2)", "ets.lookup(1,2)", "ets.compare_exchange_versioned(1,2,3)",
                "pg.join(1,2)", "pg.broadcast(1)", "pg.members(1)",
                "pg.leave(1,2)", "pg /* gap */ . broadcast(1)"
            })
            {
                try { parse("fnc main():int{return " + expression + ";}"); }
                catch (FormatException ex) when (ex.Message.Contains(
                    "ETS/pg guest APIs require authenticated isolate",
                    StringComparison.Ordinal)) { continue; }
                throw new Exception("Unsupported ETS/pg was not rejected before CIL: " + expression);
            }
            // Lexer removes comment bodies and must not invent API calls.
            _ = parseScalarSourceProbe(parse,
                "fnc main():int{return 42;} // ets.lookup pg.broadcast\n");
            _ = parseScalarSourceProbe(parse,
                "fnc main():int{return 42;} /* pg /* ets.lookup */ done */");
        }
    }

    private static bool parseScalarSourceProbe(Action<string> parse, string source)
    {
        parse(source);
        return true;
    }
}
