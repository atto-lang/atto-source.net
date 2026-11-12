using System;
using System.Linq;
using Ores.Net.Compiler;

internal static class QuotedTemplateAdmissionTests
{
    private static void AssertReject(string source, string diagnostic)
    {
        foreach (Action parse in new Action[] {
            () => { _ = OresParser.Parse(source); },
            () => { _ = OresStructuredParser.Parse(source); }
        })
        {
            try { parse(); }
            catch (FormatException e)
            {
                if (e.Message.Contains(diagnostic, StringComparison.Ordinal) &&
                    e.Message.Contains("offset", StringComparison.Ordinal))
                    continue;
                throw new Exception("Wrong quote/template diagnostic: " + e.Message);
            }
            throw new Exception("Unlowered quoted/template String admitted to .NET CIL");
        }
    }

    public static void Run()
    {
        foreach (string source in new[] {
            "fnc main():int{return 'single quote';}",
            "fnc main():int{return \"double quote\";}",
            "fnc main():int{return 'say \"hello\"';}",
            "fnc main():int{return \"it's valid\";}",
            "fnc main():int{return `hello world`;}",
            "fnc main():int{return `x ${'nested } quote'} y`;}",
            "fnc main():int{return `outer ${`inner ${\"x\"}`}`;}",
            "fnc main():int{return `line one\nline two`;}",
            "fnc main():int{return `before ${ /* } */ \"ok\" } after`;}",
            "fnc main():int{return 'line\\\ncontinuation';}",
            "fnc main():int{return 'raw\nnewline';}",
            "fnc main():int{return \"raw\r\nnewline\";}",
            "fnc main():int{return 'legacy \\q';}",
            "fnc main():int{return 'legacy \\x41';}"
        })
            AssertReject(source, "STRING_BACKEND_UNSUPPORTED");

        foreach (string source in new[] {
            "fnc main():int{return 'unterminated;}",
            "fnc main():int{return `unterminated;}",
            "fnc main():int{return `bad ${name;}",
            "fnc main():int{return `empty ${}`;}",
            "fnc main():int{return `open ${ /* comment not closed` ;}",
            "fnc main():int{return `invalid \\u12G4`;}"
        })
        {
            foreach (Action parse in new Action[] {
                () => { _ = OresParser.Parse(source); },
                () => { _ = OresStructuredParser.Parse(source); }
            })
            {
                try { parse(); }
                catch (FormatException e)
                {
                    if (e.Message.Contains("offset", StringComparison.Ordinal))
                        continue;
                    throw new Exception("Missing quote/template failure position: " + e.Message);
                }
                throw new Exception("Malformed quote/template admitted to .NET CIL");
            }
        }

        AssertReject("fnc main():int{return `" +
            string.Concat(Enumerable.Repeat("${'x'}", 1025)) + "`;}",
            "Template substitution budget");
        AssertReject("fnc main():int{return `" +
            string.Concat(Enumerable.Repeat("${`", 33)) + "x" +
            string.Concat(Enumerable.Repeat("`}", 33)) + "`;}",
            "Template nesting budget");

        AssertReject("fnc main():int{return '\\uD83D\\uDE00';}", "STRING_BACKEND_UNSUPPORTED");
        AssertReject("fnc main():int{return '\\uD83D';}", "Unpaired Unicode surrogate escape");
        AssertReject("fnc main():int{return '\\uDE00';}", "Unpaired Unicode surrogate escape");
        AssertReject("fnc main():int{return '\\uD83D\\u0061';}", "Unpaired Unicode surrogate escape");
        string comment128 = "`prefix ${" + string.Concat(Enumerable.Repeat("/*", 128)) +
            " ignored " + string.Concat(Enumerable.Repeat("*/", 128)) + "'ok'}`";
        string comment129 = "`prefix ${" + string.Concat(Enumerable.Repeat("/*", 129)) +
            " ignored " + string.Concat(Enumerable.Repeat("*/", 129)) + "'ok'}`";
        AssertReject("fnc main():int{return " + comment128 + ";}", "STRING_BACKEND_UNSUPPORTED");
        AssertReject("fnc main():int{return " + comment129 + ";}", "Block comment nesting budget exceeded");
        // Canonical Java/LLVM depth counts both nested template and interpolation frames.
        string nestedTemplate = "`x`";
        for (int i = 2; i <= 17; i++) nestedTemplate = "`outer ${" + nestedTemplate + "}`";
        AssertReject("fnc main():int{return " + nestedTemplate + ";}", "STRING_BACKEND_UNSUPPORTED");
        nestedTemplate = "`outer ${" + nestedTemplate + "}`";
        AssertReject("fnc main():int{return " + nestedTemplate + ";}", "Template nesting budget");
        string braces255 = "`" + "${" + new string('{', 255) + "x" + new string('}', 255) + "}" + "`";
        string braces256 = "`" + "${" + new string('{', 256) + "x" + new string('}', 256) + "}" + "`";
        AssertReject("fnc main():int{return " + braces255 + ";}", "STRING_BACKEND_UNSUPPORTED");
        AssertReject("fnc main():int{return " + braces256 + ";}", "Template interpolation braces budget");

        if (OresParser.Parse("fnc main():int{return 42;}") is null ||
            OresStructuredParser.Parse("fnc main():int{return 42;}") is null)
            throw new Exception("Scalar baseline broken by quote/template lexer");
    }
}
