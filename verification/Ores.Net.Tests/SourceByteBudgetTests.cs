using System.Text;
using Ores.Net.Compiler;

internal static class SourceByteBudgetTests
{
    private static void ExpectSourceRejection(Action operation, string diagnostic)
    {
        try { operation(); }
        catch (FormatException exception) when (exception.Message == diagnostic) { return; }
        throw new Exception("Expected exact source rejection: " + diagnostic);
    }

    private static void CheckSourceAdmissionOffsets()
    {
        const string valid = "fnc main(): int { return 42; }";
        string emoji = char.ConvertFromUtf32(0x1f600);
        foreach (var (source, offset, scalarMessage, structuredMessage) in new[]
        {
            (valid + " /* hidden\0 */", valid.Length + 10,
                "Source contains NUL byte", "Source contains NUL"),
            (valid + "\r\n" + (char)0xd800, valid.Length + 2,
                "Source contains invalid UTF-16", "Invalid UTF-16"),
            (valid + " // " + emoji + (char)0xdc00, valid.Length + 6,
                "Source contains invalid UTF-16", "Invalid UTF-16"),
            (valid + (char)0xd800 + "x", valid.Length,
                "Source contains invalid UTF-16", "Invalid UTF-16")
        })
        {
            ExpectSourceRejection(() => OresParser.Parse(source),
                $"{scalarMessage} at offset {offset}");
            ExpectSourceRejection(() => OresStructuredParser.Parse(source),
                $"{structuredMessage} at offset {offset}");
        }


        // Both public parser entry points must reject controls before trivia or
        // quoted source content can conceal them. Report UTF-16 code-unit index.
        foreach (char control in new[] { (char)0x001b, (char)0x007f })
        {
            foreach (string source in new[] {
                "/* hidden" + control + " */" + valid,
                valid + " // hidden" + control,
                "\"inside" + control + "\"",
                "/* café 😀 */" + control + valid
            })
            {
                int at = source.IndexOf(control);
                ExpectSourceRejection(() => OresParser.Parse(source),
                    $"Source contains terminal control at offset {at}");
                ExpectSourceRejection(() => OresStructuredParser.Parse(source),
                    $"Source contains terminal control at offset {at}");
            }
        }
        foreach (string source in new[] { "\0" + (char)0x001b, "\0" + (char)0x007f })
        {
            ExpectSourceRejection(() => OresParser.Parse(source),
                "Source contains NUL byte at offset 0");
            ExpectSourceRejection(() => OresStructuredParser.Parse(source),
                "Source contains NUL at offset 0");
        }

        // Valid UTF-8 comments remain admissible; no guest execution claim.
        _ = OresParser.Parse(valid + " // café " + emoji);
        _ = OresStructuredParser.Parse(valid + " // café " + emoji);
    }

    public static void Run()
    {
        CheckSourceAdmissionOffsets();
        const int limit = 1_048_576;
        const string prefix = "fnc main(): int { return 1; } //";
        foreach (string scalar in new[] { "é", "€", "😀" })
        {
            int remaining = limit - Encoding.UTF8.GetByteCount(prefix);
            int width = Encoding.UTF8.GetByteCount(scalar);
            string exact = prefix + string.Concat(Enumerable.Repeat(scalar, remaining / width))
                + new string(' ', remaining % width);
            if (Encoding.UTF8.GetByteCount(exact) != limit)
                throw new Exception("Invalid source-byte boundary fixture");
            _ = OresParser.Parse(exact);
            _ = OresStructuredParser.Parse(exact);
            foreach (Action parse in new Action[] {
                () => OresParser.Parse(exact + scalar),
                () => OresStructuredParser.Parse(exact + scalar) })
            {
                try { parse(); }
                catch (FormatException ex) when (ex.Message.Contains("1 MiB")) { continue; }
                throw new Exception("Multibyte source exceeded byte limit without admission failure");
            }
        }
    }
}
