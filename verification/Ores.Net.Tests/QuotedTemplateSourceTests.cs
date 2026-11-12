using Ores.Net.Compiler;

internal static class QuotedTemplateSourceTests
{
    private static void Fail(string source, string expected, bool structured)
    {
        try
        {
            if (structured) _ = OresStructuredParser.Parse(source);
            else _ = OresParser.Parse(source);
        }
        catch (FormatException error)
        {
            if (error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("offset", StringComparison.Ordinal))
                return;
            throw new Exception("Unexpected quote/template diagnostic: " + error.Message);
        }
        throw new Exception("Incorrectly accepted quote/template in i64-only CIL: " + source);
    }

    public static void Run()
    {
        foreach (string literal in new[]
        {
            "'single'", "\"double\"", "'escaped \\' quote'",
            "\"opposite 'quote'\"", "'unicode \\u0041'",
            new string((char)0x60, 1) + "raw\nmultiline" + (char)0x60,
            new string((char)0x60, 1) + "x $" + "{call({a: '}'}, /* { */ " +
                (char)0x60 + "nested $" + "{y}" + (char)0x60 + ")}" + (char)0x60
        })
        {
            Fail("pub fnc main():int { return " + literal + "; }",
                "STRING_BACKEND_UNSUPPORTED", false);
            Fail("pub fnc main():int { return " + literal + "; }",
                "STRING_BACKEND_UNSUPPORTED", true);
        }
        foreach (string malformed in new[]
        {
            "'raw\nnewline'", "'unterminated", "'\\q'", "'\\u12X4'",
            new string((char)0x60, 1) + "unclosed $" + "{value"
        })
        {
            Fail("pub fnc main():int { return " + malformed + "; }",
                malformed.Contains('\n') ? "newline in quoted string" :
                    malformed.Contains("\\q") ? "Invalid string escape" :
                    malformed.Contains("\\u12") ? "Invalid Unicode escape" :
                    "Unterminated", false);
            Fail("pub fnc main():int { return " + malformed + "; }",
                malformed.Contains('\n') ? "newline in quoted string" :
                    malformed.Contains("\\q") ? "Invalid string escape" :
                    malformed.Contains("\\u12") ? "Invalid Unicode escape" :
                    "Unterminated", true);
        }
        foreach (var (literal, diagnostic) in new (string Literal, string Diagnostic)[]
        {
            ("'\\uD800'", "Invalid Unicode escape"),
            ("'\\uDFFF'", "Invalid Unicode escape"),
            (new string((char)0x60, 1) + "x ${/* only comment */}" + (char)0x60,
                "Empty template interpolation"),
            (new string((char)0x60, 1) + "x ${// only comment\n}" + (char)0x60,
                "Empty template interpolation")
        })
        {
            foreach (bool structured in new[] { false, true })
                Fail("pub fnc main():int { return " + literal + "; }", diagnostic, structured);
        }
        // Legal import specifiers are actual source syntax and resolve exactly
        // through the compiler graph; this is more than a host-only scanner.
        string root = Path.Combine(Path.GetTempPath(),
            "atto-quoted-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string main = Path.Combine(root, "main.atto");
            File.WriteAllText(main, "import fnc twice from './base'; pub fnc main():int { return twice(21); }");
            File.WriteAllText(Path.Combine(root, "base.atto"),
                "pub fnc twice(int n):int { return n * 2; }");
            var result = OresSourceModuleCompiler.Compile(main, new[] { root });
            if (result.Sources.Count != 2 || result.Module.Helpers.Count == 0)
                throw new Exception("Single-quoted .atto import was not linked as a typed module graph");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
