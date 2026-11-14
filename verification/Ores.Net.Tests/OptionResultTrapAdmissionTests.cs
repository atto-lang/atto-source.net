using Ores.Net.Compiler;

internal static class OptionResultTrapAdmissionTests
{
    private const string Diagnostic =
        "Option/Result/trap requires verified tagged-sum CIL and NativeAOT guest-control lowering";

    public static void Run()
    {
        // 12 genuine source spellings, none of which may be lowered as an
        // untyped scalar, or falsely counted as a semantic negative pass.
        string[] unimplemented =
        [
            "fnc main():Option<int>{return Some(7);}",
            "fnc main():Option<int>{return None;}",
            "fnc main():Result<int,String>{return Ok(7);}",
            "fnc main():Result<int,String>{return Err(\"bad\");}",
            "trap fnc main():int{return 7;}",
            "fnc main():int{return Some(7);}",
            "fnc main():int{return Ok(7);}",
            "fnc main():int{return Err(7);}",
            "fnc main():int{return None;}",
            "fnc main():int{return Option(7);}",
            "fnc main():int{return Result(7);}",
            "fnc main():int{return 7;} trap"
        ];
        foreach (var source in unimplemented)
        {
            try { _ = OresParser.Parse(source); }
            catch (FormatException error) when
                (error.Message.Contains(Diagnostic, StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("Scalar CIL admitted or misclassified tagged source: " + source);
        }

        // The lexer must not treat words in source trivia as code.
        foreach (var source in new[]
        {
            "fnc main():int{return 7;} // Option Result Some None Ok Err trap",
            "fnc main():int{return 7;} /* Option Result Some None Ok Err trap */",
            "fnc main():int{return 7;} /* Option /* Result trap */ Some */",
            "fnc main():int{return 7;} // Result\r",
            "fnc main():int{return 7;} // trap\u2028"
        })
        {
            if (OresParser.Parse(source) is not Literal { Value: 7 })
                throw new Exception("Comment-only tagged keyword altered scalar source");
        }
        // Keywords in longer identifiers are distinct tokens, not matched
        // by substring. The ordinary scalar name validator still rejects them.
        try { _ = OresParser.Parse("fnc main():int{return OptionValue;}"); }
        catch (FormatException error) when
            (!error.Message.Contains(Diagnostic, StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Substring of Option was incorrectly treated as reserved ADT keyword");
    }
}
