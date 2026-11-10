using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Deliberately narrow, opt-in AttoLang source -> ahead-of-time C# lowering
/// for the first actor-private native object-graph fixture. The output is
/// compiler-produced source compiled by the normal .NET NativeAOT toolchain;
/// there is NO Reflection.Emit, runtime parsing, interpretation or JIT.
/// 
/// The general scalar/structured compiler still rejects nominal classes and
/// actors. This lane admits one homogeneous class with an i64 field and a
/// nullable self-reference, and one actor receive body containing exactly two
/// constructions and one checked dereference assertion. Nothing else.
/// Widen only after real guest/type/effect/ownership conformance execution.
/// </summary>
public static class ActorHeapV1Compiler
{
    public const int MaxSourceBytes = 16 * 1024;
    private const int MaxTokens = 256;
    private const int MaxCommentDepth = 16;
    private static readonly HashSet<string> ReservedIdentifiers = new(
        new[]
        {
            "define", "pub", "class", "actor", "as", "end", "int", "bool",
            "receive", "let", "val", "const", "new", "none", "assert",
            "return", "fnc", "method", "self", "true", "false", "null",
            "if", "then", "else", "fi", "while", "do", "await", "suspends",
            "critical", "rt", "borrow", "take", "share", "raise", "throw",
            "panic", "recover"
        }, StringComparer.Ordinal);

    public sealed record Plan(
        string ClassName, string ActorName, string ValueField,
        string NextField, string MessageName, string FirstVariable,
        string SecondVariable, long FirstValue, long SecondValue, long ExpectedValue);

    private readonly record struct Token(string Text, int Offset);

    public static Plan Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int bytes;
        try
        {
            bytes = new UTF8Encoding(false, true).GetByteCount(source);
        }
        catch (EncoderFallbackException ex)
        {
            throw new FormatException("E_ACTOR_HEAP_V1_UTF8: invalid source encoding", ex);
        }
        if (bytes > MaxSourceBytes)
            throw new FormatException("E_ACTOR_HEAP_V1_BUDGET: source exceeds 16 KiB");

        var tokens = Lex(source);
        var reader = new Reader(tokens);
        reader.Expect("define");
        reader.Expect("class");
        string cls = reader.Identifier();
        reader.Expect("as");
        reader.Expect("int");
        string valueField = reader.Identifier();
        reader.Expect(cls);
        reader.Expect("?");
        string nextField = reader.Identifier();
        if (nextField == valueField)
            throw new FormatException("E_ACTOR_HEAP_V1_FIELD: duplicate field name");
        reader.Expect("end");
        reader.Expect("define");
        reader.Expect("actor");
        string actor = reader.Identifier();
        if (actor == cls)
            throw new FormatException("E_ACTOR_HEAP_V1_TYPE: class and actor names must differ");
        reader.Expect("as");
        reader.Expect("receive");
        reader.Expect("(");
        reader.Expect("int");
        string message = reader.Identifier();
        reader.Expect(")");
        reader.Expect("{");
        reader.Expect("let");
        string first = reader.Identifier();
        reader.Expect("=");
        reader.Expect("new");
        reader.Expect(cls);
        reader.Expect("(");
        long firstValue = reader.I64();
        reader.Expect(",");
        reader.Expect("none");
        reader.Expect(")");
        reader.Expect("let");
        string second = reader.Identifier();
        if (first == second)
            throw new FormatException("E_ACTOR_HEAP_V1_LOCAL: duplicate owner binding");
        reader.Expect("=");
        reader.Expect("new");
        reader.Expect(cls);
        reader.Expect("(");
        long secondValue = reader.I64();
        reader.Expect(",");
        reader.Expect(first);
        reader.Expect(")");
        reader.Expect("assert");
        reader.Expect("(");
        reader.Expect(second);
        reader.Expect(".");
        reader.Expect(nextField);
        reader.Expect("?");
        reader.Expect(".");
        reader.Expect(valueField);
        reader.Expect("==");
        long expected = reader.I64();
        reader.Expect(")");
        reader.Expect("}");
        reader.Expect("end");
        reader.ExpectEnd();
        return new Plan(cls, actor, valueField, nextField, message,
            first, second, firstValue, secondValue, expected);
    }

    public static string EmitCSharp(string source)
    {
        var plan = Parse(source);
        string firstLiteral = Literal(plan.FirstValue);
        string secondLiteral = Literal(plan.SecondValue);
        string expectedLiteral = Literal(plan.ExpectedValue);

        // Source-derived names are used only in this comment. Every
        // executable identifier and API call is compiler-owned; literals
        // are parsed 64-bit integers, not pasted raw source expressions.
        var output = new StringBuilder();
        output.AppendLine("// GENERATED by ActorHeapV1Compiler from checked .atto source.");
        output.AppendLine("// Narrow V1 actor-heap guest subset, NOT general class/actor lowering.");
        output.Append("// Declared class: ").Append(plan.ClassName)
            .Append("; actor: ").Append(plan.ActorName).AppendLine();
        output.AppendLine("using System;");
        output.AppendLine("using System.Buffers.Binary;");
        output.AppendLine("using Ores.Net.Runtime;");
        output.AppendLine("namespace Ores.Net.Generated;");
        output.AppendLine("public static class ActorHeapV1Guest");
        output.AppendLine("{");
        output.AppendLine("    private static readonly ActorGuestLayout32 layout = new(1u, 16,");
        output.AppendLine("        new ActorGuestField32[] {");
        output.AppendLine("            new(0, ActorGuestFieldKind32.I64),");
        output.AppendLine("            new(8, ActorGuestFieldKind32.Ref32, 1u)");
        output.AppendLine("        });");
        output.AppendLine("    public static void receive(ActorContext context, int message)");
        output.AppendLine("    {");
        output.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        output.AppendLine("        uint first = 0;");
        output.AppendLine("        uint second = 0;");
        output.AppendLine("        try");
        output.AppendLine("        {");
        output.AppendLine("            Span<byte> value = stackalloc byte[8];");
        output.AppendLine("            first = context.AllocateTypedGuestObject32(layout);");
        output.Append("            BinaryPrimitives.WriteInt64LittleEndian(value, ")
            .Append(firstLiteral).AppendLine(");");
        output.AppendLine("            context.WriteGuestObject32(first, 0, value);");
        output.AppendLine("            context.WriteGuestReference32(first, 8, 0);");
        output.AppendLine("            second = context.AllocateTypedGuestObject32(layout);");
        output.Append("            BinaryPrimitives.WriteInt64LittleEndian(value, ")
            .Append(secondLiteral).AppendLine(");");
        output.AppendLine("            context.WriteGuestObject32(second, 0, value);");
        output.AppendLine("            context.WriteGuestReference32(second, 8, first);");
        output.AppendLine("            uint linked = context.ReadGuestReference32(second, 8);");
        output.AppendLine("            if (linked == 0)");
        output.AppendLine("                throw new InvalidOperationException(\"E_ACTOR_HEAP_V1_NULL\");");
        output.AppendLine("            context.ReadGuestObject32(linked, 0, value);");
        output.Append("            if (BinaryPrimitives.ReadInt64LittleEndian(value) != ")
            .Append(expectedLiteral).AppendLine(")");
        output.AppendLine("                throw new InvalidOperationException(\"E_ACTOR_HEAP_V1_ASSERT\");");
        output.AppendLine("        }");
        output.AppendLine("        finally");
        output.AppendLine("        {");
        output.AppendLine("            // Every owned native root gets its cleanup attempt even");
        output.AppendLine("            // if an earlier reference clear or destructor fails.");
        output.AppendLine("            try");
        output.AppendLine("            {");
        output.AppendLine("                if (second != 0)");
        output.AppendLine("                {");
        output.AppendLine("                    try");
        output.AppendLine("                    {");
        output.AppendLine("                        context.WriteGuestReference32(second, 8, 0);");
        output.AppendLine("                    }");
        output.AppendLine("                    finally");
        output.AppendLine("                    {");
        output.AppendLine("                        context.DropGuestObject32(second);");
        output.AppendLine("                    }");
        output.AppendLine("                }");
        output.AppendLine("            }");
        output.AppendLine("            finally");
        output.AppendLine("            {");
        output.AppendLine("                if (first != 0)");
        output.AppendLine("                    context.DropGuestObject32(first);");
        output.AppendLine("            }");
        output.AppendLine("        }");
        output.AppendLine("    }");
        output.AppendLine("}");
        return output.ToString();
    }

    private static string Literal(long number) =>
        number == long.MinValue ? "long.MinValue" :
            number.ToString(CultureInfo.InvariantCulture) + "L";

    private sealed class Reader(List<Token> tokens)
    {
        private int _index;
        private Token Current => tokens[_index];

        internal void Expect(string text)
        {
            if (!string.Equals(Current.Text, text, StringComparison.Ordinal))
                throw new FormatException(
                    $"E_ACTOR_HEAP_V1_SYNTAX: expected '{text}' at offset {Current.Offset}");
            _index++;
        }

        internal string Identifier()
        {
            Token token = Current;
            if (token.Text.Length is < 1 or > 128 ||
                !IsIdentifierStart(token.Text[0]) ||
                token.Text.Skip(1).Any(c => !IsIdentifierContinue(c)) ||
                ReservedIdentifiers.Contains(token.Text))
                throw new FormatException(
                    $"E_ACTOR_HEAP_V1_NAME: invalid identifier at offset {token.Offset}");
            _index++;
            return token.Text;
        }

        internal long I64()
        {
            Token token = Current;
            if (token.Text.Length == 0 ||
                !long.TryParse(token.Text, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var value))
                throw new FormatException(
                    $"E_ACTOR_HEAP_V1_INTEGER: signed i64 literal required at offset {token.Offset}");
            _index++;
            return value;
        }

        internal void ExpectEnd()
        {
            if (Current.Text.Length != 0)
                throw new FormatException(
                    $"E_ACTOR_HEAP_V1_TRAILING: unsupported statement at offset {Current.Offset}");
        }
    }

    private static bool IsIdentifierStart(char ch) =>
        ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    private static bool IsIdentifierContinue(char ch) =>
        IsIdentifierStart(ch) || ch is >= '0' and <= '9';

    private static List<Token> Lex(string source)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < source.Length)
        {
            char ch = source[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] != '\n' && source[i] != '\r') i++;
                continue;
            }
            if (ch == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int start = i;
                i += 2;
                int depth = 1;
                while (depth != 0)
                {
                    if (i >= source.Length)
                        throw new FormatException(
                            $"E_ACTOR_HEAP_V1_COMMENT: unterminated comment at offset {start}");
                    if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
                    {
                        if (++depth > MaxCommentDepth)
                            throw new FormatException(
                                $"E_ACTOR_HEAP_V1_COMMENT: nesting limit at offset {i}");
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
            int at = i;
            if (IsIdentifierStart(ch))
            {
                i++;
                while (i < source.Length && IsIdentifierContinue(source[i])) i++;
                tokens.Add(new Token(source[at..i], at));
            }
            else if (ch is >= '0' and <= '9' ||
                     ch == '-' && i + 1 < source.Length &&
                     source[i + 1] is >= '0' and <= '9')
            {
                i++;
                while (i < source.Length && source[i] is >= '0' and <= '9') i++;
                tokens.Add(new Token(source[at..i], at));
            }
            else if (ch == '=' && i + 1 < source.Length && source[i + 1] == '=')
            {
                tokens.Add(new Token("==", at));
                i += 2;
            }
            else if (ch is '(' or ')' or '{' or '}' or ',' or '.' or '?' or '=')
            {
                tokens.Add(new Token(ch.ToString(), at));
                i++;
            }
            else
            {
                throw new FormatException(
                    $"E_ACTOR_HEAP_V1_TOKEN: unsupported character at offset {at}");
            }
            if (tokens.Count >= MaxTokens)
                throw new FormatException("E_ACTOR_HEAP_V1_BUDGET: token count exceeded");
        }
        tokens.Add(new Token("", source.Length));
        return tokens;
    }
}
