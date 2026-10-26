namespace Ores.Net.Compiler;

internal static class StaticSelectAdmission
{
    internal const string Diagnostic =
        "Static select requires checked Channel/Future arbitration, actor-lane continuation, ownership and cancellation lowering in the .NET backend";

    internal static bool IsStaticSelectFollower(string token) =>
        token is "{" or "over" or "first" or "fair" or "random";

    internal static FormatException Unsupported(int offset) =>
        new($"{Diagnostic} at offset {offset}");

    internal static bool FollowedBySelect(string source, int offset)
    {
        int i = offset;
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

        const string keyword = "select";
        if (i + keyword.Length > source.Length ||
            !source.AsSpan(i, keyword.Length).SequenceEqual(keyword.AsSpan()))
            return false;
        int end = i + keyword.Length;
        return end == source.Length ||
               !(char.IsLetterOrDigit(source[end]) || source[end] == '_');
    }
}
