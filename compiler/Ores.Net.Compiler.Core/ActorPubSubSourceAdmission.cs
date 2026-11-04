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
    public const string PipeDiagnostic =
        "native actor pipe source lowering unsupported";

    public static void RejectUnsupported(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Token? previous = null;
        Token? beforePrevious = null;
        Token? thirdPrevious = null;
        int pendingPipeOffset = -1;

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

            // Infix "pipe" is contextual: an identifier named pipe or
            // functional |> must not be rejected. Require expression tokens
            // on both sides before reporting unsupported actor lowering.
            if (pendingPipeOffset >= 0)
            {
                if (IsActorPipeRight(token))
                    throw PipeUnsupported(pendingPipeOffset);
                pendingPipeOffset = -1;
            }

            if (token.IsIdentifier && token.Text == "pipe")
            {
                bool staticCall = previous is { Text: "." } &&
                    beforePrevious is { Text: "ActorUtils", IsIdentifier: true };
                bool staticColonCall = previous is { Text: ":" } &&
                    beforePrevious is { Text: ":" } &&
                    thirdPrevious is { Text: "ActorUtils", IsIdentifier: true };
                if (staticCall || staticColonCall)
                    throw PipeUnsupported(token.Offset);
                if (IsActorPipeLeft(previous))
                    pendingPipeOffset = token.Offset;
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

            thirdPrevious = beforePrevious;
            beforePrevious = previous;
            previous = token;
        }
    }

    private static bool IsActorPipeLeft(Token? previous) =>
        previous is { Text: ")" or "]" or "}" } ||
        previous is { IsIdentifier: true } word &&
            word.Text is not ("fnc" or "routine" or "method" or "define" or
                "pub" or "static" or "val" or "let" or "const" or "final" or
                "return" or "spawn" or "pipe" or "as" or "case");

    private static bool IsActorPipeRight(Token next) =>
        next.Text is "(" or "[" ||
        next.IsIdentifier && next.Text is not (
            "then" or "fi" or "end" or "else" or "as" or "case" or "over");

    private static FormatException PipeUnsupported(int offset) =>
        new($"{PipeDiagnostic} at offset {offset}");

    private static FormatException Unsupported(int offset) =>
        new($"{Diagnostic} at offset {offset}");

    private readonly record struct Token(string Text, int Offset, bool IsIdentifier);
}
