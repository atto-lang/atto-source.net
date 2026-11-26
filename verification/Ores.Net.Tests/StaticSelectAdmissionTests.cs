using Ores.Net.Compiler;

internal static class StaticSelectAdmissionTests
{
    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception(why);
    }
    private static void RejectDiagnostic(Action action, string diagnostic, string why)
    {
        try { action(); }
        catch (FormatException e) when (e.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected select-specific rejection: " + why);
    }
    public static void Run()
    {
        // Typed static select must fail with the select-specific diagnostic in
        // both compiler entry points, even when trivia separates the tokens. It is
        // never silently downgraded to scalar arithmetic or a CPU execution region.
        foreach (string source in new[] {
            "select<int> over default: -> { return 42; } end",
            "select /* trivia */ <int> over default: -> { return 42; } end",
            "nb select<int> over default: -> { return 42; } end",
            "try select<int> over default: -> { return 42; } end",
            "do select<int> over default: -> { return 42; } end",
            "do select over default: -> { return 42; } end",
            "nb select over default: -> { return 42; } end",
            "select { default: -> { return 42; } }",
            "select(3) over when readch incoming: int value -> { return value; } end",
            "select /* outer /* inner */ trivia */ (3) { default: -> { return 42; } }",
            "select<int>(3) { default: -> { return 42; } }",
            "await select(3) over default: -> { return 42; } end",
            "await select<int>(3) over default: -> { return 42; } end",
            "nb select(3) { default: -> { return 42; } }",
            "nb /* comment */ select /* count */ (3) { default: -> { return 42; } }",
            "try select(0) { default: -> { return 42; } }",
            "do select(-1) { default: -> { return 42; } }",
            "select(true) { default: -> { return 42; } }",
            "select(1 + 2) { default: -> { return 42; } }"
        })
        {
            RejectDiagnostic(() => OresParser.Parse(source),
                "Static select requires checked Channel/Future arbitration",
                "scalar frontend must fail closed on typed select syntax");
            RejectDiagnostic(() => OresStructuredParser.Parse(source),
                "Static select requires checked Channel/Future arbitration",
                "structured frontend must fail closed on typed select syntax");
        }
        Require(OresStructuredParser.Parse("fnc select(int n):int{return n;} pub fnc main():int{return select((42));}") is not null,
            "ordinary contextual select call must remain accepted in the structured frontend");
        Require(OresParser.Parse("fnc main():int{return 42;} /* select<int> over ignored */") is not null,
            "comment-only typed select must not trigger scalar admission");
        Require(OresStructuredParser.Parse(
            "fnc main()->int { /* select<int> over ignored */ return 42; }").Body.Count == 1,
            "comment-only typed select must not trigger structured admission");
    }
}
