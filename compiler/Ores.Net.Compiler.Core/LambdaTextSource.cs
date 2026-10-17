using System.Text;

namespace Ores.Net.Compiler;

public sealed record LambdaTextArm(string Match, string Response);

public sealed record LambdaTextProgram(
    string? ModuleName,
    string FunctionName,
    string ParameterName,
    IReadOnlyList<LambdaTextArm> Arms,
    string DefaultResponse)
{
    public string Invoke(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        foreach (var arm in Arms)
            if (string.Equals(payload, arm.Match, StringComparison.Ordinal))
                return arm.Response;
        return DefaultResponse;
    }
}

/// <summary>
/// Narrow fail-closed parser for the provider-neutral Oreslang text Lambda ABI.
///
/// Admitted source is deliberately small while the shared .NET frontend grows:
///
/// define module handler as
///   pub fnc invoke(String event): String {
///     if event == "ping"; then return "pong"; fi
///     return "unsupported";
///   }
/// end
///
/// A top-level `define pub fnc` form is also accepted. No implicit coercions,
/// arbitrary calls, interpolation, mutable state or host access are admitted.
/// </summary>
public static class OresLambdaTextParser
{
    private const int MaxSourceBytes = 1_048_576;
    private const int MaxTokens = 65_536;
    private const int MaxArms = 1024;
    private const int MaxIdentifierBytes = 128;
    private const int MaxLiteralBytes = 1_048_576;

    private enum TokenKind { Word, String, Symbol, End }
    private readonly record struct Token(TokenKind Kind, string Text, int Offset);

    public static LambdaTextProgram Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(source);
        var lexer = new Lexer(source);
        var tokens = new List<Token>();
        while (true)
        {
            var token = lexer.Read();
            tokens.Add(token);
            if (tokens.Count > MaxTokens)
                throw new FormatException("Lambda text token budget exceeded");
            if (token.Kind == TokenKind.End) break;
        }
        return new Reader(tokens).ReadProgram();
    }

    private static void ValidateSource(string source)
    {
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\0')
                throw new FormatException($"Source contains NUL at offset {i}");
            if (char.IsHighSurrogate(source[i]))
            {
                if (++i >= source.Length || !char.IsLowSurrogate(source[i]))
                    throw new FormatException($"Invalid UTF-16 at offset {i - 1}");
            }
            else if (char.IsLowSurrogate(source[i]))
                throw new FormatException($"Invalid UTF-16 at offset {i}");
        }
        if (Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            throw new FormatException("Source exceeds 1 MiB UTF-8 parser limit");
    }

    private sealed class Lexer(string source)
    {
        private int index;

        public Token Read()
        {
            SkipTrivia();
            if (index == source.Length)
                return new Token(TokenKind.End, "", index);

            int at = index;
            char ch = source[index];
            if (IsAlpha(ch) || ch == '_')
            {
                index++;
                while (index < source.Length &&
                       (IsAlpha(source[index]) || IsDigit(source[index]) ||
                        source[index] == '_'))
                    index++;
                return new Token(TokenKind.Word, source[at..index], at);
            }

            if (ch == '"')
                return ReadString();

            if (ch == '-' && index + 1 < source.Length &&
                source[index + 1] == '>')
            {
                index += 2;
                return new Token(TokenKind.Symbol, "->", at);
            }
            if (ch == '=' && index + 1 < source.Length &&
                source[index + 1] == '=')
            {
                index += 2;
                return new Token(TokenKind.Symbol, "==", at);
            }
            if ("(){}:;".IndexOf(ch) >= 0)
            {
                index++;
                return new Token(TokenKind.Symbol, ch.ToString(), at);
            }

            throw new FormatException($"Invalid Lambda text character at offset {at}");
        }

        private void SkipTrivia()
        {
            while (true)
            {
                while (index < source.Length && char.IsWhiteSpace(source[index]))
                    index++;
                if (index + 1 >= source.Length) return;

                if (source[index] == '/' && source[index + 1] == '/')
                {
                    index += 2;
                    while (index < source.Length && source[index] != '\n')
                        index++;
                    continue;
                }

                if (source[index] == '/' && source[index + 1] == '*')
                {
                    int start = index;
                    index += 2;
                    int depth = 1;
                    while (index < source.Length && depth > 0)
                    {
                        if (index + 1 < source.Length &&
                            source[index] == '/' && source[index + 1] == '*')
                        {
                            depth++;
                            if (depth > 64)
                                throw new FormatException(
                                    $"Nested comment depth exceeded at offset {index}");
                            index += 2;
                        }
                        else if (index + 1 < source.Length &&
                                 source[index] == '*' && source[index + 1] == '/')
                        {
                            depth--;
                            index += 2;
                        }
                        else index++;
                    }
                    if (depth != 0)
                        throw new FormatException(
                            $"Unterminated block comment at offset {start}");
                    continue;
                }
                return;
            }
        }

        private Token ReadString()
        {
            int at = index++;
            var value = new StringBuilder();
            while (index < source.Length)
            {
                char ch = source[index++];
                if (ch == '"')
                {
                    if (Encoding.UTF8.GetByteCount(value.ToString()) > MaxLiteralBytes)
                        throw new FormatException(
                            $"String literal exceeds 1 MiB at offset {at}");
                    return new Token(TokenKind.String, value.ToString(), at);
                }
                if (ch is '\r' or '\n')
                    throw new FormatException(
                        $"Raw newline in string literal at offset {index - 1}");
                if (ch == '\\')
                {
                    if (index >= source.Length)
                        throw new FormatException(
                            $"Unterminated string escape at offset {index - 1}");
                    char escaped = source[index++];
                    value.Append(escaped switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => throw new FormatException(
                            $"Unsupported string escape at offset {index - 1}")
                    });
                }
                else
                {
                    if (char.IsControl(ch))
                        throw new FormatException(
                            $"Control character in string literal at offset {index - 1}");
                    value.Append(ch);
                }
            }
            throw new FormatException($"Unterminated string literal at offset {at}");
        }
    }

    private sealed class Reader(List<Token> tokens)
    {
        private int index;
        private Token Current => tokens[index];

        public LambdaTextProgram ReadProgram()
        {
            string? module = null;
            bool wrapper = false;

            if (Is("define") && Peek(1, "module"))
            {
                Take("define");
                Take("module");
                module = Name();
                Take("as");
                wrapper = true;
            }
            else if (Is("define"))
            {
                Take("define");
            }

            Take("pub");
            Take("fnc");
            var function = Name();
            Take("(");
            Take("String");
            var parameter = Name();
            Take(")");
            if (Is(":")) Take(":");
            else Take("->");
            Take("String");
            Take("{");

            var arms = new List<LambdaTextArm>();
            while (Is("if"))
            {
                if (arms.Count >= MaxArms)
                    throw Error("Lambda text arm budget exceeded");
                Take("if");
                var read = Name();
                if (!string.Equals(read, parameter, StringComparison.Ordinal))
                    throw Error("Lambda condition must read the function parameter");
                Take("==");
                var match = StringLiteral();
                Take(";");
                Take("then");
                Take("return");
                var response = StringLiteral();
                Take(";");
                Take("fi");
                arms.Add(new LambdaTextArm(match, response));
            }

            Take("return");
            var fallback = StringLiteral();
            Take(";");
            Take("}");

            if (wrapper) Take("end");
            if (Current.Kind != TokenKind.End)
                throw Error("Unexpected trailing Lambda text token");

            return new LambdaTextProgram(
                module,
                function,
                parameter,
                Array.AsReadOnly(arms.ToArray()),
                fallback);
        }

        private bool Is(string text) =>
            string.Equals(Current.Text, text, StringComparison.Ordinal);

        private bool Peek(int delta, string text) =>
            index + delta < tokens.Count &&
            string.Equals(tokens[index + delta].Text, text, StringComparison.Ordinal);

        private void Take(string text)
        {
            if (!Is(text))
                throw Error($"Expected '{text}'");
            index++;
        }

        private string Name()
        {
            var token = Current;
            if (token.Kind != TokenKind.Word)
                throw Error("Expected identifier");
            if (Encoding.UTF8.GetByteCount(token.Text) > MaxIdentifierBytes ||
                token.Text is "define" or "module" or "as" or "pub" or "fnc" or
                    "String" or "if" or "then" or "return" or "fi" or "end")
                throw Error("Invalid Lambda text identifier");
            index++;
            return token.Text;
        }

        private string StringLiteral()
        {
            var token = Current;
            if (token.Kind != TokenKind.String)
                throw Error("Expected string literal");
            index++;
            return token.Text;
        }

        private FormatException Error(string message) =>
            new($"{message} at offset {Current.Offset}");
    }

    private static bool IsAlpha(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
