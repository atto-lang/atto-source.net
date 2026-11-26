namespace Ores.Net.Compiler;

internal static class StaticSelectAdmission
{
    internal const string Diagnostic =
        "Static select requires checked Channel/Future arbitration, actor-lane continuation, ownership and cancellation lowering in the .NET backend";

    internal static bool IsStaticSelectFollower(string token) =>
        token is "{" or "<" or "over" or "first" or "fair" or "random";

    internal static FormatException Unsupported(int offset) =>
        new($"{Diagnostic} at offset {offset}");

    internal static bool FollowedByStaticSelect(string source, int offset)
    {
        int i = offset;
        if (!SkipTrivia(source, ref i)) return false;

        const string keyword = "select";
        if (i + keyword.Length > source.Length ||
            !source.AsSpan(i, keyword.Length).SequenceEqual(keyword.AsSpan()))
            return false;
        int end = i + keyword.Length;
        if (end < source.Length &&
            (char.IsLetterOrDigit(source[end]) || source[end] == '_'))
            return false;

        i = end;
        if (!SkipTrivia(source, ref i) || i >= source.Length) return false;
        // Preserve ordinary contextual select(...) calls. A count header
        // becomes a static select only when followed by a static body/policy.
        if (source[i] is '{' or '<') return true;
        if (source[i] == '(')
        {
            if (!SkipCountHeader(source, ref i)) return false;
            if (!SkipTrivia(source, ref i) || i >= source.Length) return false;
            if (source[i] == '{') return true;
        }
        if (!(char.IsLetter(source[i]) || source[i] == '_')) return false;

        int start = i++;
        while (i < source.Length &&
               (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
            i++;
        return IsStaticSelectFollower(source[start..i]);
    }

    // The source budget and nesting cap bound contextual lookahead. Quotes
    // and nested comments cannot counterfeit the closing count delimiter.
    private static bool SkipCountHeader(string source, ref int i)
    {
        int start = i;
        int depth = 0;
        while (i < source.Length)
        {
            if (i - start > 16_384)
                throw new FormatException("Select count lookahead exceeds source budget");
            if (!SkipTrivia(source, ref i) || i >= source.Length) return false;
            if (i - start > 16_384)
                throw new FormatException("Select count lookahead exceeds source budget");
            char ch = source[i++];
            if (ch == '(')
            {
                if (++depth > 128)
                    throw new FormatException("Select count nesting exceeds source budget");
            }
            else if (ch == ')')
            {
                if (--depth == 0) return true;
            }
            else if (ch is '\'' or '"' or '`')
            {
                bool closed = false;
                while (i < source.Length)
                {
                    if (i - start > 16_384)
                        throw new FormatException("Select count lookahead exceeds source budget");
                    char quoted = source[i++];
                    if (quoted == '\\')
                    {
                        if (i < source.Length) i++;
                    }
                    else if (quoted == ch) { closed = true; break; }
                }
                if (!closed) return false;
            }
        }
        return false;
    }

    private static bool SkipTrivia(string source, ref int i)
    {
        while (i < source.Length)
        {
            if (char.IsWhiteSpace(source[i])) { i++; continue; }
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length &&
                       source[i] is not ('\n' or '\r' or '\u0085' or '\u2028' or '\u2029'))
                    i++;
                continue;
            }
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                i += 2;
                int depth = 1;
                while (i < source.Length && depth > 0)
                {
                    if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
                    { depth++; i += 2; }
                    else if (i + 1 < source.Length && source[i] == '*' && source[i + 1] == '/')
                    { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0) return false;
                continue;
            }
            break;
        }
        return true;
    }
}
