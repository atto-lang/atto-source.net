using Ores.Net.Compiler;

internal static class AsyncInteropFailClosedTests
{
    internal static void Run()
    {
        Action<string>[] parsers = [
            source => { _ = OresParser.Parse(source); },
            source => { _ = OresStructuredParser.Parse(source); }
        ];
        _ = OresStructuredParser.Parse("fnc main():int{let int task = 42;return task;}");
        foreach (var parse in parsers)
        {
            parse("fnc main():int{return 42;} // Future.from_task(task { ignored })");
            parse("fnc main():int{return 42;} /* task { /* nested */ } */");
            // These inputs are otherwise within bootstrap lexical admission;
            // demand the Task-boundary reason, not an unrelated syntax error.
            foreach (var trivia in new[] { " ", "/* gap */", "/* a /* b */ c */",
                "// gap\n", "// gap\r", "// gap\r\n", "// gap\u0085",
                "// gap\u2028", "// gap\u2029" })
            {
                string source = "fnc main():int{return task" + trivia + "{return 42;};}";
                try { parse(source); }
                catch (FormatException error) when (error.Message.Contains(
                    "execution task requires scheduler-aware Future/continuation lowering",
                    StringComparison.Ordinal)) { continue; }
                throw new Exception("Missing targeted unsupported Task-boundary diagnostic");
            }
            // These broad rejections establish containment ONLY. In particular
            // a parser error here does NOT certify I-04 type/cardinality checks.
            foreach (var expression in new[] {
                "Future.from_task(task {return 42;})", "Task.from_future(f)",
                "Observable.from_future(f)", "Observable.from_task(t)",
                "f.to_obs()", "t.to_obs()", "await obs",
                "Future.from_observable(obs, mode=#single)",
                "Future.from_observable(obs, mode=#first)",
                "Task.from_observable(obs, mode=#single)",
                "Task.from_observable(obs, mode=#first)",
                "Future.new(task {return 42;})"
            })
            {
                try { parse("fnc main():int{return " + expression + ";}"); }
                catch (FormatException) { continue; }
                throw new Exception("Unsupported async interop reached compiler admission");
            }
        }
    }
}
