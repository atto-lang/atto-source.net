namespace Ores.Net.Compiler;

public enum ExplicitExportScope { File, Package, Public }

public enum ExplicitExportKind
{
    Module, Actor, Class, Fnc, Routine, Interface, Contract, Trait, Struct, Type,
    Const, Val, Let, Symbol
}

public sealed record ExplicitExportItem(
    string LocalName,
    string ExternalName,
    bool Symbol,
    bool SelfIdentity);

public sealed record ExplicitExportDeclaration(
    ExplicitExportScope Scope,
    ExplicitExportKind? Kind,
    IReadOnlyList<ExplicitExportItem> Items);

/// <summary>
/// Source-only parser for the shared explicit-export v1 grammar.
///
/// This does not make the scalar Native-AOT compiler a multi-declaration
/// compiler and is not consulted by CIL emission yet. It exists so unsupported
/// .NET compilation cannot drift into a different export grammar while the
/// namespace/type frontend is still being built.
/// </summary>
public static class ExplicitExportSyntax
{
    private const int MaxSourceBytes = 64 * 1024;
    private const int MaxTokens = 4_096;

    public static ExplicitExportDeclaration Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (System.Text.Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            throw Error("EXPORT_SOURCE_BUDGET", "export declaration exceeds 64 KiB syntax budget");

        var tokens = Lex(source);
        var reader = new Reader(tokens);
        return reader.Parse();
    }

    private readonly record struct Token(string Text, int Offset, bool Literal = false);

    private static List<Token> Lex(string source)
    {
        var result = new List<Token>();
        int i = 0;
        void Add(string text, int offset, bool literal = false)
        {
            result.Add(new Token(text, offset, literal));
            if (result.Count > MaxTokens)
                throw Error("EXPORT_TOKEN_BUDGET", "export token budget exceeded");
        }

        while (i < source.Length)
        {
            if (char.IsWhiteSpace(source[i])) { i++; continue; }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] is not ('\n' or '\r' or '\u0085' or '\u2028' or '\u2029')) i++;
                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int at = i;
                i += 2;
                int depth = 1;
                while (depth > 0)
                {
                    if (i >= source.Length)
                        throw Error("EXPORT_UNTERMINATED_COMMENT", $"unterminated block comment at offset {at}");
                    if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
                    {
                        if (++depth > 128)
                            throw Error("EXPORT_COMMENT_DEPTH", "block comment nesting exceeds 128");
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
            char c = source[i];
            if (IsAlpha(c) || c == '_')
            {
                i++;
                while (i < source.Length && (IsAlpha(source[i]) || IsDigit(source[i]) || source[i] == '_')) i++;
                Add(source[start..i], start);
                continue;
            }

            if (c is '\'' or '"')
            {
                char quote = c;
                i++;
                bool escaped = false;
                while (i < source.Length)
                {
                    char next = source[i++];
                    if (escaped) { escaped = false; continue; }
                    if (next == '\\') { escaped = true; continue; }
                    if (next == quote) break;
                }
                if (i > source.Length || source[i - 1] != quote)
                    throw Error("EXPORT_UNTERMINATED_LITERAL", $"unterminated literal at offset {start}");
                Add(source[start..i], start, literal: true);
                continue;
            }

            if (c == ':' && i + 1 < source.Length && source[i + 1] == ':')
            {
                i += 2;
                Add("::", start);
                continue;
            }

            if ("[](){},;.*#<>/".IndexOf(c) >= 0)
            {
                i++;
                Add(source[start..i], start);
                continue;
            }

            throw Error("EXPORT_INVALID_CHARACTER", $"invalid export character at offset {start}");
        }

        result.Add(new Token("", source.Length));
        return result;
    }

    private sealed class Reader(List<Token> tokens)
    {
        private int index;
        private Token Current => tokens[index];

        internal ExplicitExportDeclaration Parse()
        {
            Expect("export");
            var scope = ParseScope();
            ExplicitExportKind? kind = ParseKind();

            if (Take("*"))
            {
                if (Current.Text == "from")
                    throw Error("EXPORT_REEXPORT_UNSUPPORTED", "export * from is reserved for future re-export syntax");
                throw Error("EXPORT_WILDCARD_UNSUPPORTED", "wildcard exports are unsupported in v1");
            }
            if (Current.Text == "from")
                throw Error("EXPORT_REEXPORT_UNSUPPORTED", "export from is reserved for future re-export syntax");

            if (kind is null && IsRuntimeTypeWord(Current.Text))
                throw Error("EXPORT_UNKNOWN_DECLARATION_KIND",
                    "typed exports use declaration families, not runtime value types");

            var items = new List<ExplicitExportItem>();
            if (Take("{"))
            {
                if (Take("}")) throw Error("EXPORT_EMPTY_GROUP", "export group cannot be empty");
                for (;;)
                {
                    if (Current.Text == ",")
                        throw Error("EXPORT_MALFORMED_LIST", "leading or repeated comma");
                    if (Current.Text == "{")
                        throw Error("EXPORT_NESTED_GROUP", "nested export groups are unsupported");
                    if (TryKind(Current.Text, out _))
                        throw Error("EXPORT_KIND_PREFIX_IN_GROUP", "kind belongs before export group");

                    items.Add(ParseItem(kind));
                    if (Take(","))
                    {
                        if (Take("}")) break;
                        if (Current.Text == ",")
                            throw Error("EXPORT_MALFORMED_LIST", "repeated comma");
                        continue;
                    }
                    if (!Take("}"))
                        throw Error("EXPORT_MALFORMED_LIST", "expected ',' or '}' after export item");
                    break;
                }
            }
            else
            {
                items.Add(ParseItem(kind));
            }

            if (!Take(";"))
                throw Error("EXPORT_SEMICOLON_REQUIRED", "export declaration must end with ';'");
            if (Current.Text.Length != 0)
                throw Error("EXPORT_TRAILING_TOKENS", $"unexpected token at offset {Current.Offset}");

            return new ExplicitExportDeclaration(scope, kind, items.AsReadOnly());
        }

        private ExplicitExportScope ParseScope()
        {
            string? close = null;
            if (Take("[")) close = "]";
            else if (Take("(")) close = ")";
            if (close is null) return ExplicitExportScope.Public;

            var scope = Current.Text switch
            {
                "file" => ExplicitExportScope.File,
                "pkg" or "package" => ExplicitExportScope.Package,
                _ => throw Error("EXPORT_UNKNOWN_SCOPE", "expected file, pkg, or package")
            };
            index++;
            Expect(close);
            if (Current.Text is "[" or "(")
                throw Error("EXPORT_MULTIPLE_SCOPES", "export declaration accepts one scope");
            return scope;
        }

        private ExplicitExportKind? ParseKind()
        {
            if (!TryKind(Current.Text, out var kind)) return null;
            // 'symbol' is contextual. With no following target/group it is an
            // ordinary local identifier, e.g. export symbol as PublicSymbol;
            if (kind == ExplicitExportKind.Symbol)
            {
                string next = index + 1 < tokens.Count ? tokens[index + 1].Text : "";
                bool typedSymbol = next == "#" || next == "{" ||
                    (IsIdentifier(next) && next != "as");
                if (!typedSymbol) return null;
            }
            index++;
            return kind;
        }

        private ExplicitExportItem ParseItem(ExplicitExportKind? kind)
        {
            if (Take("#"))
            {
                string source = "#" + Name("EXPORT_MALFORMED_TARGET");
                if (kind is not null && kind != ExplicitExportKind.Symbol)
                    throw Error("EXPORT_KIND_MISMATCH", "Symbol target requires export symbol or untyped export");

                if (!Take("as"))
                    return new ExplicitExportItem(source, source, Symbol: true, SelfIdentity: false);

                if (!Take("#"))
                {
                    if (IsIdentifier(Current.Text))
                        throw Error("EXPORT_SYMBOL_BINDING_ALIAS_UNSUPPORTED",
                            "Symbol binding aliases are unsupported in v1");
                    throw Error("EXPORT_SYMBOL_IDENTITY_MISMATCH", "expected exact Symbol self-identity");
                }
                string external = "#" + Name("EXPORT_SYMBOL_IDENTITY_MISMATCH");
                if (!string.Equals(source, external, StringComparison.Ordinal))
                    throw Error("EXPORT_SYMBOL_IDENTITY_MISMATCH",
                        "Symbol export cannot substitute nominal identity");
                return new ExplicitExportItem(source, external, Symbol: true, SelfIdentity: true);
            }

            string local = Name("EXPORT_MALFORMED_TARGET");
            if (kind == ExplicitExportKind.Symbol)
            {
                if (Take("as") && Take("#"))
                    throw Error("EXPORT_SYMBOL_IDENTITY_TARGET_REQUIRES_SYMBOL_SOURCE",
                        "Symbol identity target requires a #symbol source");
                throw Error("EXPORT_KIND_MISMATCH", "export symbol requires #symbol target");
            }

            if (Current.Text is "." or "::" or "/")
                throw Error("EXPORT_QUALIFIED_TARGET_UNSUPPORTED", "local target must be a simple name");
            if (Current.Text == "<")
                throw Error("EXPORT_GENERIC_TARGET_UNSUPPORTED", "generic instantiation is not an export target");
            if (Current.Text == "(")
                throw Error("EXPORT_CALLABLE_SIGNATURE_UNSUPPORTED", "export callable family by name");

            string external = local;
            if (Take("as"))
            {
                if (Take("#"))
                    throw Error("EXPORT_SYMBOL_IDENTITY_TARGET_REQUIRES_SYMBOL_SOURCE",
                        "ordinary declaration cannot alias to Symbol identity");
                if (Current.Literal)
                    throw Error("EXPORT_LITERAL_ALIAS_UNSUPPORTED",
                        "runtime literal is not a source export name");
                external = Name("EXPORT_MALFORMED_ALIAS");
            }

            return new ExplicitExportItem(local, external, Symbol: false, SelfIdentity: false);
        }

        private string Name(string code)
        {
            if (!IsIdentifier(Current.Text))
                throw Error(code, $"expected identifier at offset {Current.Offset}");
            string value = Current.Text;
            index++;
            return value;
        }

        private bool Take(string text)
        {
            if (!string.Equals(Current.Text, text, StringComparison.Ordinal)) return false;
            index++;
            return true;
        }

        private void Expect(string text)
        {
            if (!Take(text))
                throw Error("EXPORT_MALFORMED_SYNTAX", $"expected '{text}' at offset {Current.Offset}");
        }
    }

    private static bool TryKind(string text, out ExplicitExportKind kind)
    {
        (bool ok, ExplicitExportKind value) = text switch
        {
            "module" => (true, ExplicitExportKind.Module),
            "actor" => (true, ExplicitExportKind.Actor),
            "class" => (true, ExplicitExportKind.Class),
            "fnc" => (true, ExplicitExportKind.Fnc),
            "routine" => (true, ExplicitExportKind.Routine),
            "interface" => (true, ExplicitExportKind.Interface),
            "contract" => (true, ExplicitExportKind.Contract),
            "trait" => (true, ExplicitExportKind.Trait),
            "struct" => (true, ExplicitExportKind.Struct),
            "type" => (true, ExplicitExportKind.Type),
            "const" => (true, ExplicitExportKind.Const),
            "val" => (true, ExplicitExportKind.Val),
            "let" => (true, ExplicitExportKind.Let),
            "symbol" => (true, ExplicitExportKind.Symbol),
            _ => (false, default)
        };
        kind = value;
        return ok;
    }

    private static bool IsRuntimeTypeWord(string value) =>
        value is "string" or "String" or "int" or "float" or "bool" or "void";

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || (!IsAlpha(value[0]) && value[0] != '_')) return false;
        for (int i = 1; i < value.Length; i++)
            if (!IsAlpha(value[i]) && !IsDigit(value[i]) && value[i] != '_') return false;
        return true;
    }

    private static bool IsAlpha(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static FormatException Error(string code, string message) =>
        new($"[{code}] {message}");
}
