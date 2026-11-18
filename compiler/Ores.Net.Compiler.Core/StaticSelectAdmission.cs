namespace Ores.Net.Compiler;

internal static class StaticSelectAdmission
{
    internal const string Diagnostic =
        "Static select requires checked Channel/Future arbitration, actor-lane continuation, ownership and cancellation lowering in the .NET backend";

    internal static bool IsStaticSelectFollower(string token) =>
        token is "{" or "over" or "first" or "fair" or "random";

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
        // Typed select<T> is deliberately unsupported too; do not misclassify
        // "nb select<T>" as an unrelated execution region.
        if (source[i] is '{' or '<') return true;
        if (!(char.IsLetter(source[i]) || source[i] == '_')) return false;

        int start = i++;
        while (i < source.Length &&
               (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
            i++;
        return IsStaticSelectFollower(source[start..i]);
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
