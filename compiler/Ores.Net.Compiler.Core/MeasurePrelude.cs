namespace Ores.Net.Compiler;

public static partial class OresParser
{
    // Exact compiler-owned source from common-source #164.
    // SHA-256 a411966eb3188104cd13cc2201b4d1d0de1e12404378aba313b396ae726a3376.
    private static readonly Lazy<string> MeasureSource = new(() =>
    {
        using var stream = typeof(OresParser).Assembly.GetManifestResourceStream("Atto.Lib.Core.Measure")
            ?? throw new InvalidOperationException("Missing compiler-owned measurement catalog");
        using var reader = new System.IO.StreamReader(stream, new System.Text.UTF8Encoding(false, true));
        return reader.ReadToEnd();
    });

    private static bool HasMeasurementMainPrefix(string source)
    {
        try
        {
            var lexer = new Lexer(source, false);
            return lexer.Read().Text == "pub" && lexer.Read().Text == "fnc" &&
                   lexer.Read().Text == "main" && lexer.Read().Text == "(";
        }
        catch (FormatException) { return false; }
    }

    private static bool RequestsMeasurePrelude(List<Token> tokens)
    {
        var units = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in MeasureSource.Value.Split('\n'))
        {
            if (line.StartsWith("define unit ", StringComparison.Ordinal))
                units.Add(line["define unit ".Length..line.IndexOf(':')]);
            else if (line.StartsWith("type ", StringComparison.Ordinal))
                aliases.Add(line["type ".Length..line.IndexOf('=', "type ".Length)].Trim());
        }
        for (int i = 4; i + 3 < tokens.Count; i++)
        {
            var a = tokens[i]; var b = tokens[i+1]; var c = tokens[i+2]; var d = tokens[i+3];
            if (a.Text.Length != 0 && a.Text.All(ch => (ch >= '0' && ch <= '9') || ch == '_') &&
                b.Text == "<" && units.Contains(c.Text) && d.Text == ">" &&
                a.Offset + a.Text.Length == b.Offset) return true;
            if (a.Text == "val" && ((aliases.Contains(b.Text) && Reader.IsVerifiedIdentifier(c.Text) && d.Text == "=") ||
                (Reader.IsVerifiedIdentifier(b.Text) && c.Text == ":" && aliases.Contains(d.Text)))) return true;
        }
        return false;
    }
}
