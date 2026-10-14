using System.Globalization;

namespace Ores.Net.Compiler;

public abstract record Expression;
public sealed record Literal(long Value) : Expression;
public sealed record Unary(char Operator, Expression Value) : Expression;
public sealed record Binary(char Operator, Expression Left, Expression Right) : Expression;

public static class OresParser
{
    private const int MaxDepth = 128;
    private const int MaxTokens = 100_000;

    public static Expression Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576)
            throw new FormatException("Source is larger than the 1 MiB bootstrap limit");
        var lexer = new Lexer(source);
        var tokens = new List<Token>();
        for (;;)
        {
            var token = lexer.Read();
            tokens.Add(token);
            if (tokens.Count > MaxTokens)
                throw new FormatException("Token count exceeds bootstrap limit");
            if (token.Text.Length == 0) break;
        }
        return new Reader(tokens).ParseProgram();
    }

    private readonly record struct Token(string Text, int Offset);

    private sealed class Lexer(string text)
    {
        private int _i;

        public Token Read()
        {
            while (_i < text.Length)
            {
                if (char.IsWhiteSpace(text[_i])) { _i++; continue; }
                if (text[_i] == '/' && _i + 1 < text.Length && text[_i + 1] == '/')
                {
                    _i += 2;
                    while (_i < text.Length && text[_i] != '\n') _i++;
                    continue;
                }
                break;
            }
            var start = _i;
            if (_i == text.Length) return new Token("", start);
            var ch = text[_i];
            if (ch is >= '0' and <= '9')
            {
                do { _i++; } while (_i < text.Length && text[_i] is >= '0' and <= '9');
                return new Token(text[start.._i], start);
            }
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')
            {
                _i++;
                while (_i < text.Length &&
                    (text[_i] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                    _i++;
                return new Token(text[start.._i], start);
            }
            if ("()+-*/{}:;".Contains(ch, StringComparison.Ordinal))
            {
                _i++;
                return new Token(text[start.._i], start);
            }
            throw new FormatException($"Invalid character at offset {start}");
        }
    }

    private sealed class Reader(List<Token> tokens)
    {
        private int _index;
        private Token Current => tokens[_index];

        private bool Take(string value)
        {
            if (Current.Text != value) return false;
            _index++;
            return true;
        }

        private void Expect(string value)
        {
            if (!Take(value))
                throw new FormatException($"Expected '{value}' at offset {Current.Offset}");
        }

        public Expression ParseProgram()
        {
            bool defined = false, exposed = false;
            while (Current.Text is "define" or "pub")
            {
                if (Take("define"))
                {
                    if (defined) throw new FormatException("Duplicate define modifier");
                    defined = true;
                }
                else
                {
                    Expect("pub");
                    if (exposed) throw new FormatException("Duplicate pub modifier");
                    exposed = true;
                }
            }
            Expect("fnc");
            Expect("main");
            Expect("(");
            Expect(")");
            Expect(":");
            Expect("int");
            Expect("{");
            Expect("return");
            var expression = ParseAdd(0);
            Expect(";");
            Expect("}");
            if (Current.Text.Length != 0)
                throw new FormatException($"Unexpected token at offset {Current.Offset}");
            return expression;
        }

        private Expression ParseAdd(int depth)
        {
            CheckDepth(depth);
            Expression left = ParseMul(depth + 1);
            while (Current.Text is "+" or "-")
            {
                char op = Current.Text[0];
                _index++;
                left = new Binary(op, left, ParseMul(depth + 1));
            }
            return left;
        }

        private Expression ParseMul(int depth)
        {
            CheckDepth(depth);
            Expression left = ParseUnary(depth + 1);
            while (Current.Text is "*" or "/")
            {
                char op = Current.Text[0];
                _index++;
                left = new Binary(op, left, ParseUnary(depth + 1));
            }
            return left;
        }

        private Expression ParseUnary(int depth)
        {
            CheckDepth(depth);
            if (Take("+")) return new Unary('+', ParseUnary(depth + 1));
            if (Take("-"))
            {
                // Signed minimum needs special treatment: its magnitude is > long.MaxValue.
                if (Current.Text == "9223372036854775808")
                {
                    _index++;
                    return new Literal(long.MinValue);
                }
                return new Unary('-', ParseUnary(depth + 1));
            }
            if (Take("("))
            {
                var inner = ParseAdd(depth + 1);
                Expect(")");
                return inner;
            }
            if (Current.Text.Length == 0 || Current.Text[0] is < '0' or > '9')
                throw new FormatException($"Expected integer at offset {Current.Offset}");
            var raw = Current.Text;
            _index++;
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long result))
                throw new FormatException($"Invalid i64 at offset {tokens[_index - 1].Offset}");
            return new Literal(result);
        }

        private static void CheckDepth(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("Expression nesting too deep");
        }
    }
}
