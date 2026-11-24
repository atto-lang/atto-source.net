using System.Globalization;
using System.Text;

namespace Atto.Cil.Compiler;

/// <summary>
/// Parse-only actor-attached state admission. This does NOT build CIL, mint
/// read/write capabilities, or authorize TenantVM/actor state access.
/// </summary>
internal static class ActorStateAdmission
{
    internal sealed record Field(string Name, string Kind, bool Writable, bool Initialized);
    internal sealed record Schema(string Owner, IReadOnlyList<Field> Fields);

    private static bool Ident(string? value) =>
        value is { Length: > 0 } &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    // A bounded lexical *probe*, not the CIL source parser. Comments and
    // quoted strings must not create fabricated 'define state' declarations.
    private static List<string> Lex(string source)
    {
        if (Encoding.UTF8.GetByteCount(source) > 65_536)
            throw new CompilerError("E_LIMIT", "64 KiB source limit");
        var tokens = new List<string>();
        for (var i = 0; i < source.Length;)
        {
            if (char.IsWhiteSpace(source[i])) { i++; continue; }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                var depth = 1;
                while (i < source.Length && depth > 0)
                {
                    if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                    {
                        if (++depth > 16) throw new CompilerError("E_STATE_SYNTAX", "comment nesting limit");
                        i += 2;
                    }
                    else if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/')
                    { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0) throw new CompilerError("E_STATE_SYNTAX", "unclosed source comment");
                continue;
            }
            if (source[i] is '"' or '\'')
            {
                var quote = source[i++];
                var complete = false;
                while (i < source.Length)
                {
                    if (source[i] == '\\') { i += Math.Min(2, source.Length - i); continue; }
                    if (source[i++] == quote) { complete = true; break; }
                }
                if (!complete) throw new CompilerError("E_STATE_SYNTAX", "unclosed source literal");
                tokens.Add("<literal>");
                continue;
            }
            var start = i;
            if (char.IsAsciiLetter(source[i]) || source[i] == '_')
            {
                i++;
                while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_')) i++;
            }
            else if (char.IsAsciiDigit(source[i]))
            {
                i++;
                while (i < source.Length && char.IsAsciiDigit(source[i])) i++;
            }
            else i++;
            tokens.Add(source[start..i]);
            if (tokens.Count > 8192) throw new CompilerError("E_LIMIT", "actor source token limit");
        }
        return tokens;
    }

    internal static Schema? Inspect(string source)
    {
        var tokens = Lex(source);
        bool Is(int i, string value) => i >= 0 && i < tokens.Count && tokens[i] == value;
        var actors = new HashSet<string>(StringComparer.Ordinal);
        var declarations = new List<int>();
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (Is(i, "{")) { depth++; continue; }
            if (Is(i, "}")) { depth = Math.Max(0, depth - 1); continue; }
            if (depth != 0) continue;
            if ((Is(i, "define") || Is(i, "declare")) && Is(i + 1, "actor") &&
                i + 2 < tokens.Count && Ident(tokens[i + 2]))
                actors.Add(tokens[i + 2]);
            if (Is(i, "define") && Is(i + 1, "state") && Is(i + 2, "on") &&
                Is(i + 3, "actor")) declarations.Add(i);
        }
        if (declarations.Count == 0) return null;
        if (declarations.Count > 1)
            throw new CompilerError("E_STATE_DUPLICATE", "only one attached actor state schema is supported");
        var p = declarations[0] + 4;
        string Next() => p < tokens.Count ? tokens[p++] : "<eof>";
        void Require(string value)
        {
            if (Next() != value) throw new CompilerError("E_STATE_SYNTAX", $"expected '{value}'");
        }
        var owner = Next();
        if (!Ident(owner) || !actors.Contains(owner))
            throw new CompilerError("E_STATE_OWNER", "attachment must name a declared actor");
        Require("as"); Require("{");
        var fields = new List<Field>();
        while (p < tokens.Count && !Is(p, "}"))
        {
            if (fields.Count >= 128) throw new CompilerError("E_STATE_LIMIT", "128 state fields maximum");
            var binding = Next();
            if (binding is not ("let" or "val"))
                throw new CompilerError("E_STATE_SYNTAX", "state field must use let or val");
            var kind = Next();
            if (kind is not ("int" or "bool"))
                throw new CompilerError("E_STATE_TYPE", "only int/bool state fields are admitted");
            var name = Next();
            if (!Ident(name))
                throw new CompilerError("E_STATE_SYNTAX", "invalid state field identifier");
            var initialized = false;
            if (Is(p, ":") || Is(p, "="))
            {
                p++;
                if (kind == "bool")
                {
                    if (Next() is not ("true" or "false"))
                        throw new CompilerError("E_STATE_SYNTAX", "expected bool literal");
                }
                else
                {
                    var negative = Is(p, "-");
                    if (negative) p++;
                    var digits = Next();
                    var raw = (negative ? "-" : "") + digits;
                    if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                        throw new CompilerError("E_STATE_LITERAL", "state i64 initializer out of range");
                }
                initialized = true;
            }
            Require(";");
            if (binding == "val" && !initialized)
                throw new CompilerError("E_STATE_UNINITIALIZED", "immutable state requires initializer");
            if (fields.Any(f => f.Name == name))
                throw new CompilerError("E_STATE_DUPLICATE", "duplicate protected field");
            fields.Add(new Field(name, kind, binding == "let", initialized));
        }
        Require("}");
        if (fields.Count == 0) throw new CompilerError("E_STATE_SYNTAX", "state attachment cannot be empty");
        // This is *only* parse/nominal admission. There is deliberately no
        // stateful CIL emitter, actor startup or guarded runtime to invoke.
        return new Schema(owner, fields);
    }
}
