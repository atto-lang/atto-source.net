using Ores.Net.Compiler;

internal static class AdtSourceAdmissionTests
{
    // This is a fail-closed bootstrap ABI reservation. Unsupported-syntax
    // refusal MUST NOT be credited as a semantic-negative ADT parity pass.
    public static void Run()
    {
        string[] nominalCases =
        {
            "define enum Stage as Ready = 1; Done = 2 end",
            "define enum Stage as Count: int = 1 end",
            "define enum Stage as Ready = 0 end",
            "define enum Stage as Ready = 2147483648 end",
            "define enum Stage as Ready = 1; Ready = 2 end",
            "define enum Stage as Ready = 1; Done = 1 end",
            "define enum Stage as Ready = 2; Done = 1 end",
            "define enum Stage as Last = 2147483647; Overflow end",
            "define enum Alpha as Ready = 1 end define enum Beta as Ready = 1 end",
            "define enum Box as Item: Option<int> = 1 end",
            "define enum Tree as Node: Tuple[int,Tree] = 1 end",
            "define enum Envelope as Wrapped: Result<int, string> = 1 end",
            "define enum Stage as Ready = 1 end fnc invalid():Stage{return Stage.Ready(99);}",
            "define enum Stage as Count:int = 1 end fnc invalid():Stage{return Stage.Count();}",
            "define enum Stage as Ready = 1; Done = 2 end fnc invalid(Stage x):int { match x over on Stage.Ready->{return 1;} end }",
            "define enum Stage as Ready = 1; Done = 2 end fnc invalid(Stage x):int { match x over on Stage.Ready when true->{return 1;} on Stage.Done->{return 2;} end }",
        };

        foreach (var source in nominalCases)
        {
            try { _ = OresParser.Parse(source); }
            catch (FormatException ex) when (
                ex.Message.Contains(
                    "nominal enum overloads require compiler-bound dispatch",
                    StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception(
                "A source-level nominal ADT bypassed .NET scalar compiler admission");
        }

        // Type-only patterns, qualified cases and monadic constructors remain
        // unsupported by the .NET bootstrap. Check refusal, not a particular
        // unrelated parser error: these are NOT semantic negative passes.
        foreach (var source in new[]
        {
            "define pattern Messages as Stage.Ready end",
            "define match pattern Messages as Stage.Ready end",
            "fnc main():int { match Some(true) over on Some(true)->{return 1;} end }",
            "fnc main():int { return Stage.Ready(); }",
            "fnc main():int { return Ok(42); }",
            "fnc main():int { return Err(7); }",
            "fnc main():int { return None(); }",
        })
        {
            try { _ = OresParser.Parse(source); }
            catch (FormatException) { continue; }
            throw new Exception("Unsupported ADT shape was admitted to scalar IR");
        }

        // The lexical guard cannot treat comments as code.
        foreach (var source in new[]
        {
            "fnc main():int{return 42;} // define enum Stage as Ready end",
            "fnc main():int{return 42;} /* define enum Foo as Case end */",
            "fnc main():int{return 42;} /* outer /* enum Bar */ end */",
            "fnc main():int{return 42;} // Result<int,int> Option<bool>"
        })
        {
            if (OresParser.Parse(source) is not Literal { Value: 42 })
                throw new Exception("ADT words in trivia affected scalar parsing");
        }
    }
}
