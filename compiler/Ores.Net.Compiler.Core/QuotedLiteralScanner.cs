using System;

namespace Ores.Net.Compiler;

// Compiler-only quote/template scanner shared by scalar and structured frontends.
// String expressions remain unsupported until typed Native AOT CIL lowering exists.
internal static class QuotedLiteralScanner
{
    public static int Scan(string source, int start)
    {
        var scanner = new Cursor(source, start);
        return scanner.Scan();
    }

    private sealed class Cursor(string source, int start)
    {
        private int pos = start;
        private int substitutions;

        private static bool IsLineEnd(char c) =>
            c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';

        private static bool IsHex(char c) =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        private static FormatException Error(string description, int offset) =>
            new(description + " at offset " + offset);

        public int Scan()
        {
            if (pos >= source.Length) throw Error("Expected string literal", pos);
            char delimiter = source[pos];
            if (delimiter is '\'' or '"') Quoted();
            else if (delimiter == (char)96) Template(1);
            else throw Error("Expected string literal", pos);
            return pos;
        }

        private void Escape(int opening, bool template)
        {
            pos++; // backslash
            if (pos >= source.Length)
                throw Error("Unterminated source literal escape", opening);
            char escaped = source[pos++];
            if (escaped == 'u')
            {
                if (pos + 4 > source.Length)
                    throw Error("Invalid Unicode escape", opening);
                for (int i = 0; i < 4; i++)
                    if (!IsHex(source[pos + i]))
                        throw Error("Invalid Unicode escape", opening);
                pos += 4;
            }
            else if (IsLineEnd(escaped))
            {
                if (template)
                    throw Error("Invalid template escape", opening);
                if (escaped == '\r' && pos < source.Length && source[pos] == '\n')
                    pos++;
            }
            else if (escaped is not ('n' or 'r' or 't' or '\\' or '\'' or '"' or '$')
                     && escaped != (char)96)
                throw Error("Invalid source literal escape", opening);
        }

        private void Quoted()
        {
            int opening = pos;
            char quote = source[pos++];
            while (pos < source.Length)
            {
                char c = source[pos];
                if (c == '\\') { Escape(opening, false); continue; }
                if (c == quote) { pos++; return; }
                if (IsLineEnd(c)) throw Error("Raw newline in quoted string", opening);
                pos++;
            }
            throw Error("Unterminated quoted string", opening);
        }

        private void BlockComment()
        {
            int opening = pos;
            pos += 2;
            int depth = 1;
            while (pos < source.Length && depth != 0)
            {
                if (pos + 1 < source.Length && source[pos] == '/' && source[pos + 1] == '*')
                {
                    if (++depth > 128) throw Error("Block comment nesting budget exceeded", opening);
                    pos += 2;
                }
                else if (pos + 1 < source.Length && source[pos] == '*' && source[pos + 1] == '/')
                {
                    depth--;
                    pos += 2;
                }
                else pos++;
            }
            if (depth != 0) throw Error("Unterminated block comment", opening);
        }

        private void Interpolation(int templateDepth)
        {
            int opening = pos;
            int braces = 0;
            bool hasExpression = false;
            while (pos < source.Length)
            {
                char c = source[pos];
                if (char.IsWhiteSpace(c)) { pos++; continue; }
                if (pos + 1 < source.Length && c == '/' && source[pos + 1] == '/')
                {
                    pos += 2;
                    while (pos < source.Length && !IsLineEnd(source[pos])) pos++;
                    continue;
                }
                if (pos + 1 < source.Length && c == '/' && source[pos + 1] == '*')
                {
                    BlockComment();
                    continue;
                }
                if (c is '\'' or '"') { Quoted(); hasExpression = true; continue; }
                if (c == (char)96) { Template(templateDepth + 1); hasExpression = true; continue; }
                if (c == '{')
                {
                    if (++braces > 256) throw Error("Template interpolation braces budget exceeded", opening);
                    hasExpression = true;
                    pos++;
                    continue;
                }
                if (c == '}')
                {
                    pos++;
                    if (braces == 0)
                    {
                        if (!hasExpression) throw Error("Empty template interpolation", opening);
                        return;
                    }
                    braces--;
                    continue;
                }
                hasExpression = true;
                pos++;
            }
            throw Error("Unterminated template interpolation", opening);
        }

        private void Template(int depth)
        {
            int opening = pos;
            if (depth > 32) throw Error("Template nesting budget exceeded", opening);
            pos++;
            while (pos < source.Length)
            {
                char c = source[pos];
                if (c == '\\') { Escape(opening, true); continue; }
                if (c == (char)96) { pos++; return; }
                if (c == '$' && pos + 1 < source.Length && source[pos + 1] == '{')
                {
                    if (++substitutions > 1024)
                        throw Error("Template substitution budget exceeded", pos);
                    pos += 2;
                    Interpolation(depth);
                    continue;
                }
                pos++;
            }
            throw Error("Unterminated template literal", opening);
        }
    }
}
