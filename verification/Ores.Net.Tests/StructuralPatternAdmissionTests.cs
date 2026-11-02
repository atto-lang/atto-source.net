using Ores.Net.Compiler;

internal static class StructuralPatternAdmissionTests
{
    public static void Run()
    {
        foreach (string source in new[] {
            "fnc main():int { match 1 over on structural {} view -> { return 1; } end }",
            "fnc main():int { match 1 over on structural HasId view -> { return 1; } end }",
            "fnc main():int { match 1 over on structural (int,int) view -> { return 1; } end }",
            "fnc main():int { match 1 over on /* trivia */ structural { id:int } view -> { return 1; } end }",
            "fnc main():int { match 1 over on /* outer /* inner */ done */ structural HasId view -> { return 1; } end }"
        })
        {
            RequireReject(() => OresParser.Parse(source));
            RequireReject(() => OresStructuredParser.Parse(source));
        }

        foreach (string source in new[] {
            "// on structural HasId view\nfnc main():int{return 42;}",
            "/* outer /* on structural HasId */ done */ fnc main():int{return 42;}",
            "fnc structural():int{return 42;} fnc main():int{return structural();}",
            "fnc main():int{const msg=\"on structural Shape view\";return 42;}"
        })
        {
            StructuralShapeAdmission.RejectUnsupportedPatterns(source);
        }
        // The AttoLang infer/record/struct syntax is recognized as a distinct
        // unsupported capability, rather than interpreted as scalar CIL.
        foreach (string source in new[] {
            "fnc main():int{const x = infer struct{n:42};return x.n;}",
            "fnc main():int{const x = infer record{n:42};return x.n;}",
            "fnc main():int{const x = record{n:42};return x.n;}",
            "fnc main():int{const x = struct{n:int}{n:42};return x.n;}",
            "fnc main():int{const x = record{...base,n:42};return x.n;}",
            "fnc main():int{const x = infer /* comment */ struct /* comment */ {n:42};return x.n;}"
        })
        {
            RequireAggregateReject(() => OresParser.Parse(source));
            RequireAggregateReject(() => OresStructuredParser.Parse(source));
        }
        // Contextual identifiers and comments/strings are not aggregate constructors.
        foreach (string source in new[] {
            "// infer struct{n:42}\\nfnc main():int{return 42;}",
            "/* infer /* record{n:42} */ done */ fnc main():int{return 42;}",
            "fnc main():int{const msg=\\\"infer struct{n:42}\\\";return 42;}",
            "fnc record():int{return 42;} fnc main():int{return record();}"
        })
            StructuralShapeAdmission.RejectUnsupportedPatterns(source);
        // Test directly that the narrow scalar compiler still executes its
        // normal parse path; the guard must not reject scalar source.
        _ = OresParser.Parse("fnc main():int{return 42;}");
        _ = OresStructuredParser.Parse("fnc main():int{return 42;}");
    }

    private static void RequireAggregateReject(Action parse)
    {
        try { parse(); }
        catch (FormatException ex) when (
            ex.Message.Contains("aggregate requires closed-row type", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Unsupported inferred/inline aggregate was not diagnosed before CIL lowering");
    }

    private static void RequireReject(Action parse)
    {
        try { parse(); }
        catch (FormatException ex) when (
            ex.Message.Contains("Structural match pattern unsupported", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Unsupported structural match pattern was not diagnosed before scalar lowering");
    }
}
