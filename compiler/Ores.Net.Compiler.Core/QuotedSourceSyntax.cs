namespace Ores.Net.Compiler;

// Shared lexical boundary for scalar and structured frontends. Recognizing
// literal syntax is NOT typed String/CIL lowering. No guest code is evaluated.
internal static class QuotedSourceSyntax
{
    internal static int Scan(string source, int start)
    {
        if (start < 0 || start >= source.Length || !IsQuote(source[start]))
            throw new FormatException("Expected quote at offset " + start);
        int substitutions = 0;
        return Quoted(source, start, 0, ref substitutions);
    }

    private static bool IsQuote(char c) => c is '\'' or '"' or '\u0060';
    private static bool IsLine(char c) =>
        c is '\n' or '\r' or '\u0085' or '\u2028' or '\u2029';
    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static FormatException Error(string message, int offset) =>
        new(message + " at offset " + offset);

    private static int Quoted(string source, int start, int depth, ref int substitutions)
    {
        if (depth > 64) throw Error("String/template nesting limit", start);
        char quote = source[start];
        int i = start + 1;
        while (i < source.Length)
        {
            char ch = source[i];
            if (ch == '\\')
            {
                int escape = i++;
                if (i == source.Length) throw Error("Unterminated escape", escape);
                char next = source[i];
                if (IsLine(next))
                {
                    if (next == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
                    i++;
                    continue;
                }
                if (next == 'u')
                {
                    i++;
                    if (i < source.Length && source[i] == '{')
                    {
                        int digits = ++i;
                        int scalar = 0;
                        while (i < source.Length && IsHex(source[i]) && i - digits < 7)
                        {
                            scalar = checked(scalar * 16 +
                                (source[i] <= '9' ? source[i] - '0' :
                                 source[i] <= 'F' ? source[i] - 'A' + 10 : source[i] - 'a' + 10));
                            i++;
                        }
                        if (i == digits || i - digits > 6 || i == source.Length ||
                            source[i] != '}' || scalar > 0x10ffff ||
                            scalar is >= 0xd800 and <= 0xdfff)
                            throw Error("Invalid Unicode escape", escape);
                        i++;
                    }
                    else
                    {
                        for (int j = 0; j < 4; j++)
                        {
                            if (i == source.Length || !IsHex(source[i]))
                                throw Error("Invalid Unicode escape", escape);
                            i++;
                        }
                    }
                    continue;
                }
                if (next == 'x')
                {
                    i++;
                    for (int j = 0; j < 2; j++)
                    {
                        if (i == source.Length || !IsHex(source[i]))
                            throw Error("Invalid hex escape", escape);
                        i++;
                    }
                    continue;
                }
                if ("\\'\"\u0060$0bfnrtv".IndexOf(next) < 0)
                    throw Error("Invalid string escape", escape);
                i++;
                continue;
            }
            if (ch == quote) return i + 1;
            if (quote != '\u0060' && IsLine(ch))
                throw Error("Newline in quoted string", i);
            if (quote == '\u0060' && ch == '$' && i + 1 < source.Length && source[i + 1] == '{')
            {
                if (++substitutions > 128)
                    throw Error("Template substitution limit", i);
                int body = i + 2;
                int end = Interpolation(source, body, depth + 1, ref substitutions);
                bool nonempty = false;
                for (int j = body; j < end - 1; j++)
                    if (!char.IsWhiteSpace(source[j])) { nonempty = true; break; }
                if (!nonempty) throw Error("Empty template interpolation", i);
                i = end;
                continue;
            }
            i++;
        }
        throw Error("Unterminated string/template", start);
    }

    private static int Interpolation(string source, int at, int depth, ref int substitutions)
    {
        if (depth > 64) throw Error("Template nesting limit", at);
        int braces = 1;
        int i = at;
        while (i < source.Length)
        {
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && !IsLine(source[i])) i++;
                continue;
            }
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                int comment = i;
                i += 2;
                int nesting = 1;
                while (nesting > 0 && i < source.Length)
                {
                    if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
                    {
                        if (++nesting > 64) throw Error("Comment nesting limit", i);
                        i += 2;
                    }
                    else if (i + 1 < source.Length && source[i] == '*' && source[i + 1] == '/')
                    {
                        nesting--;
                        i += 2;
                    }
                    else i++;
                }
                if (nesting != 0) throw Error("Unterminated interpolation comment", comment);
                continue;
            }
            if (IsQuote(source[i]))
            {
                i = Quoted(source, i, depth + 1, ref substitutions);
                continue;
            }
            if (source[i] == '{')
            {
                if (++braces > 128) throw Error("Interpolation brace nesting limit", i);
            }
            else if (source[i] == '}')
            {
                if (--braces == 0) return i + 1;
            }
            i++;
        }
        throw Error("Unterminated template interpolation", at - 2);
    }
}
