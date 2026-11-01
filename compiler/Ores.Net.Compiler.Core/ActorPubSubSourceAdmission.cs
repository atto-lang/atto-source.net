namespace Ores.Net.Compiler;

/// <summary>
/// Shared fail-closed source gate while .NET has runtime pub/sub substrate but
/// no verified ActorGroup/Topic lowering. The scanner ignores comments and
/// string contents so inert text never becomes capability evidence.
/// </summary>
public static class ActorPubSubSourceAdmission
{
    public const string Diagnostic =
        "native ActorGroup/Topic source lowering unsupported";

    public static void RejectUnsupported(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Token? previous = null;
        Token? beforePrevious = null;

        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                int depth = 1;
                while (i < source.Length && depth != 0)
                {
                    if (i + 1 < source.Length && source[i] == '/' &&
                        source[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (i + 1 < source.Length && source[i] == '*' &&
                             source[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else i++;
                }
                continue;
            }

            if (c is '"' or '\'')
            {
                char quote = c;
                i++;
                bool escaped = false;
                while (i < source.Length)
                {
                    char current = source[i++];
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (current == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (current == quote) break;
                }
                continue;
            }

            Token token;
            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i++;
                while (i < source.Length &&
                       (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_'))
                    i++;
                token = new Token(source[start..i], start, IsIdentifier: true);
            }
            else
            {
                token = new Token(source[i].ToString(), i, IsIdentifier: false);
                i++;
            }

            if (token.Text == "group" && token.IsIdentifier &&
                previous is { Text: "." } &&
                beforePrevious is { Text: "actor", IsIdentifier: true })
                throw Unsupported(beforePrevious.Value.Offset);

            if (token.Text == "<" &&
                previous is { IsIdentifier: true } prior &&
                (prior.Text is "Topic" or "Subscription" ||
                 (prior.Text == "topic" &&
                  beforePrevious is { Text: "." })))
                throw Unsupported(prior.Offset);

            beforePrevious = previous;
            previous = token;
        }
    }

    private static FormatException Unsupported(int offset) =>
        new($"{Diagnostic} at offset {offset}");

    private readonly record struct Token(string Text, int Offset, bool IsIdentifier);
}
