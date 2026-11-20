using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Source-language admission for exactly one independently scheduled
/// Future.from_task(task { return i64-literal; }) entry. This is an interpreter
/// entry for the NativeAOT compiler CLI, NOT emitted guest CIL or full Task
/// lowering. Keep the normal compile command fail-closed.
/// </summary>
public static class OresAsyncSourceSlice
{
    public sealed record Program(long LiteralValue);

    public static Program Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576 ||
            Encoding.UTF8.GetByteCount(source) > 1_048_576)
            throw new FormatException("Async source exceeds 1 MiB budget");
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\0') throw new FormatException($"NUL at offset {i}");
            if (char.IsHighSurrogate(source[i]))
            {
                if (++i == source.Length || !char.IsLowSurrogate(source[i]))
                    throw new FormatException($"Invalid UTF-16 at offset {i - 1}");
            }
            else if (char.IsLowSurrogate(source[i]))
                throw new FormatException($"Invalid UTF-16 at offset {i}");
        }

        var tokens = Scan(source);
        var reader = new Reader(tokens);
        reader.Expect("fnc");
        reader.Expect("main");
        reader.Expect("(");
        reader.Expect(")");
        reader.Expect(":");
        reader.Expect("Future");
        reader.Expect("<");
        reader.Expect("int");
        reader.Expect(">");
        reader.Expect("{");
        reader.Expect("return");
        reader.Expect("Future");
        reader.Expect(".");
        reader.Expect("from_task");
        reader.Expect("(");
        reader.Expect("task");
        reader.Expect("{");
        reader.Expect("return");
        var number = reader.TakeNumber();
        reader.Expect(";");
        reader.Expect("}");
        reader.Expect(")");
        reader.Expect(";");
        reader.Expect("}");
        reader.Expect(""); // Reject trailing tokens and executable decorations.
        return new Program(number);
    }

    private readonly record struct Token(string Value, int Offset);

    private sealed class Reader(IReadOnlyList<Token> tokens)
    {
        private int index;
        public void Expect(string value)
        {
            if (index >= tokens.Count || tokens[index].Value != value)
            {
                var at = index < tokens.Count ? tokens[index].Offset : -1;
                throw new FormatException($"Expected '{value}' at offset {at}");
            }
            index++;
        }

        public long TakeNumber()
        {
            var token = tokens[index];
            if (token.Value.Length == 0 || token.Value[0] is not (>= '0' and <= '9'))
                throw new FormatException($"Expected i64 literal at offset {token.Offset}");
            index++;
            if (!long.TryParse(token.Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long result))
                throw new FormatException($"i64 literal out of range at offset {token.Offset}");
            return result;
        }
    }

    private static List<Token> Scan(string source)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < source.Length)
        {
            if (tokens.Count >= 256) throw new FormatException("Async source token limit");
            if (char.IsWhiteSpace(source[i])) { i++; continue; }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length &&
                    source[i] is not ('\n' or '\r' or '\u0085' or '\u2028' or '\u2029'))
                    i++;
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int begin = i, depth = 1;
                i += 2;
                while (depth != 0)
                {
                    if (i >= source.Length)
                        throw new FormatException($"Unterminated block comment at offset {begin}");
                    if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
                    {
                        if (++depth > 64)
                            throw new FormatException($"Block comment depth exceeded at offset {begin}");
                        i += 2;
                    }
                    else if (i + 1 < source.Length && source[i] == '*' && source[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else i++;
                }
                continue;
            }
            int start = i;
            char ch = source[i++];
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')
            {
                while (i < source.Length &&
                    source[i] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '_') i++;
            }
            else if (ch is >= '0' and <= '9')
            {
                while (i < source.Length && source[i] is >= '0' and <= '9') i++;
            }
            else if ("(){}:;.<>" .IndexOf(ch) < 0)
                throw new FormatException($"Unsupported async source token at offset {start}");
            tokens.Add(new Token(source[start..i], start));
        }
        tokens.Add(new Token("", source.Length));
        return tokens;
    }
}
