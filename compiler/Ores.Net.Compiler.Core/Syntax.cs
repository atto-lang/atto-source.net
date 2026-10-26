using System.Globalization;

namespace Ores.Net.Compiler;

public abstract record Expression;
public sealed record Literal(long Value) : Expression;
public sealed record Unary(char Operator, Expression Value) : Expression;
public sealed record Binary(char Operator, Expression Left, Expression Right) : Expression;

public enum SourceExportScope { File, Package, Public }
public enum SourceExportOrigin { ImplicitPub, ExplicitExport }
public sealed record SourceExportBinding(
    string OwnerNamespaceId,
    string LocalSymbolId,
    string LocalKind,
    string LocalName,
    string ExternalName,
    SourceExportScope Scope,
    SourceExportOrigin Origin);
public sealed record ParsedSource(
    Expression Expression,
    IReadOnlyList<SourceExportBinding> SourceExportBindings);

public static class OresParser
{
    private const int MaxDepth = 128;
    private const int MaxTokens = 100_000;

    public static Expression Parse(string source) =>
        ParseWithExports(source).Expression;

    public static ParsedSource ParseWithExports(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576)
            throw new FormatException("Source is larger than the 1 MiB bootstrap limit");
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\0') throw new FormatException("Source contains NUL byte");
            if (char.IsHighSurrogate(c))
            {
                if (++i >= source.Length || !char.IsLowSurrogate(source[i]))
                    throw new FormatException("Source contains invalid UTF-16");
            }
            else if (char.IsLowSurrogate(c))
                throw new FormatException("Source contains invalid UTF-16");
        }
        // Public parser callers supply UTF-16 strings; the source budget is
        // UTF-8 bytes, matching the disk/CLI boundary, not code-unit count.
        if (System.Text.Encoding.UTF8.GetByteCount(source) > 1_048_576)
            throw new FormatException("Source exceeds 1 MiB UTF-8 parser limit");
        // No structural match lowering or readonly shape metadata in scalar CIL yet.
        StructuralShapeAdmission.RejectUnsupportedPatterns(source);
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
                    while (_i < text.Length && text[_i] is not ('\n' or '\r' or '\u0085' or '\u2028' or '\u2029')) _i++;
                    continue;
                }
                // Keep comments lexically invisible even when they occur
                // between a tagged namespace and '.' in a rejected pipeline.
                if (text[_i] == '/' && _i + 1 < text.Length && text[_i + 1] == '*')
                {
                    var offset = _i;
                    _i += 2;
                    var depth = 1;
                    while (depth > 0)
                    {
                        if (_i == text.Length)
                            throw new FormatException($"Unterminated block comment at offset {offset}");
                        if (_i + 1 < text.Length && text[_i] == '/' && text[_i + 1] == '*')
                        {
                            if (++depth > 128)
                                throw new FormatException($"Block comment nesting exceeds 128 at offset {offset}");
                            _i += 2;
                        }
                        else if (_i + 1 < text.Length && text[_i] == '*' && text[_i + 1] == '/')
                        {
                            depth--;
                            _i += 2;
                        }
                        else _i++;
                    }
                    continue;
                }
                break;
            }
            var start = _i;
            if (_i == text.Length) return new Token("", start);
            var ch = text[_i];
            if (ch is >= '0' and <= '9')
            {
                // Match the reference's decimal digit separators without
                // admitting trailing, adjacent or non-digit underscores.
                // Only ASCII decimal digits are accepted, never host-culture
                // separators or floating-point literals.
                _i++;
                while (_i < text.Length)
                {
                    if (text[_i] is >= '0' and <= '9') { _i++; continue; }
                    if (text[_i] != '_') break;
                    if (_i + 1 >= text.Length ||
                        text[_i + 1] is not (>= '0' and <= '9'))
                        throw new FormatException($"Invalid i64 digit separator at offset {_i}");
                    _i++;
                }
                // The bootstrap compiler is i64-only. Reject Java-compatible
                // real-literal starts before splitting into integer tokens.
                if (_i < text.Length &&
                    ((text[_i] == '.' && _i + 1 < text.Length && text[_i + 1] is >= '0' and <= '9') ||
                     ((text[_i] == 'e' || text[_i] == 'E') && _i + 1 < text.Length &&
                       (text[_i + 1] is >= '0' and <= '9' ||
                        ((text[_i + 1] == '+' || text[_i + 1] == '-') &&
                          _i + 2 < text.Length && text[_i + 2] is >= '0' and <= '9'))) ||
                     (text[_i] == 'd' && (_i + 1 == text.Length ||
                         !(char.IsLetterOrDigit(text[_i + 1]) || text[_i + 1] == '_')))))
                    throw new FormatException($"Floating-point/decimal literal requires typed numeric lowering at offset {start}");
                return new Token(text[start.._i], start);
            }
            if (ch is '"' or '\'')
            {
                char quote = ch;
                _i++;
                var valueStart = _i;
                bool escaped = false;
                while (_i < text.Length)
                {
                    char current = text[_i];
                    if (!escaped && current == quote) break;
                    if (current is '\n' or '\r')
                        throw new FormatException($"Raw newline in string literal at offset {start}");
                    if (!escaped && current == '\\') escaped = true;
                    else escaped = false;
                    _i++;
                }
                if (_i == text.Length)
                    throw new FormatException($"Unterminated string literal at offset {start}");
                string value = text[valueStart.._i];
                _i++;
                return new Token($"{quote}{value}{quote}", start);
            }
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')
            {
                _i++;
                while (_i < text.Length &&
                    (text[_i] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                    _i++;
                var word = text[start.._i];
                if (word is "any" or "unknown" or "Any" or "Unknown")
                    throw new FormatException(
                        "any/unknown requires tagged opaque-value ABI and Native AOT runtime descriptors");
                // This bootstrap compiler is single-file. Do not accept Java
                // project-root, zed package or bare import declarations until
                // a verified source graph / native AOT linker exists.
                if (word == "import")
                    throw new FormatException("source imports require verified module graph lowering");
                if (word is "cpu" or "gpu" or "nb")
                    throw new FormatException(
                        $"execution region '{word}' requires verified CPU/GPU Task lowering; no implicit CPU fallback at offset {start}");
                if (word == "task")
                {
                    if (ContextualTaskBoundary.OpensBlock(text, _i))
                        throw new FormatException(
                            $"execution task requires scheduler-aware Future/continuation lowering; no implicit inline execution at offset {start}");
                }
                if (word == "enum")
                    throw new FormatException(
                        $"nominal enum overloads require compiler-bound dispatch (unsupported .NET bootstrap) at offset {start}");
                return new Token(word, start);
            }
            // Recognize the contiguous operator as one token, but never
            // attempt a scalar-only approximation of named-call lowering.
            if (ch == '|' && _i + 1 < text.Length && text[_i + 1] == '>')
            {
                _i += 2;
                return new Token("|>", start);
            }
            if (ch == '-' && _i + 1 < text.Length && text[_i + 1] == '>')
            {
                _i += 2;
                return new Token("->", start);
            }
            // Lex assignment punctuation so unsupported declarations are rejected
            // at their declaration, rather than at a later initializer. The
            // arithmetic parser still has no assignment production.
            if ("()[]+-*/{}:;,.=?#<>".IndexOf(ch) >= 0)
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

        private FormatException Error(string message) =>
            new($"{message} at offset {Current.Offset}");

        private sealed record PendingExport(
            SourceExportScope Scope,
            string? Kind,
            IReadOnlyList<(string Local, string? External, bool Symbol, bool SelfIdentity)> Items,
            int Offset);

        public ParsedSource ParseProgram()
        {
            var exports = new List<PendingExport>();
            while (Current.Text == "export") exports.Add(ParseExport());

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
            if (!Take(":")) Expect("->");
            Expect("int");
            Expect("{");
            Expect("return");
            var expression = ParseAdd(0);
            Expect(";");
            Expect("}");

            while (Current.Text == "export") exports.Add(ParseExport());
            if (Current.Text.Length != 0)
                throw new FormatException($"Unexpected token at offset {Current.Offset}");

            return new ParsedSource(expression, ResolveExports(exports, exposed));
        }

        private PendingExport ParseExport()
        {
            int at = Current.Offset;
            Expect("export");
            SourceExportScope scope = SourceExportScope.Public;
            if (Current.Text is "[" or "(")
            {
                string close = Take("[") ? "]" : (Take("(") ? ")" : throw Error("Invalid export scope"));
                if (Current.Text == "file") scope = SourceExportScope.File;
                else if (Current.Text is "pkg" or "package") scope = SourceExportScope.Package;
                else throw Error("[EXPORT_UNKNOWN_SCOPE] expected file, pkg, or package export scope");
                _index++;
                Expect(close);
                if (Current.Text is "[" or "(")
                    throw Error("[EXPORT_MULTIPLE_SCOPES] export accepts exactly one scope");
            }

            if (Take("*"))
            {
                if (Take("from"))
                    throw Error("[EXPORT_REEXPORT_UNSUPPORTED] export * from is reserved for future re-export syntax");
                throw Error("[EXPORT_WILDCARD_UNSUPPORTED] wildcard exports are not enabled in v1");
            }
            if (Take("from"))
                throw Error("[EXPORT_REEXPORT_UNSUPPORTED] export from is reserved for future re-export syntax");

            string? kind = null;
            bool contextualSymbolKind =
                Current.Text == "symbol" &&
                _index + 1 < tokens.Count &&
                tokens[_index + 1].Text is not ("as" or ";" or ",");
            if (Current.Text is "module" or "actor" or "class" or "fnc" or "routine" or
                "interface" or "contract" or "trait" or "struct" or "type" or
                "const" or "val" or "let" or "model" || contextualSymbolKind)
            {
                kind = Current.Text;
                _index++;
            }
            else if (Current.Text is "string" or "String" or "int" or "bool" or "void")
                throw Error("[EXPORT_UNKNOWN_DECLARATION_KIND] typed export prefixes are declaration families, not runtime value types");

            var items = new List<(string Local, string? External, bool Symbol, bool SelfIdentity)>();
            if (Take("{"))
            {
                if (Take("}")) throw Error("[EXPORT_EMPTY_GROUP] export groups cannot be empty");
                while (true)
                {
                    if (Current.Text == ",")
                        throw Error("[EXPORT_MALFORMED_LIST] leading or repeated comma in export group");
                    if (Current.Text == "{")
                        throw Error("[EXPORT_NESTED_GROUP] nested export groups are not supported");
                    if (Current.Text is "module" or "actor" or "class" or "fnc" or "routine" or
                        "interface" or "contract" or "trait" or "struct" or "type" or
                        "const" or "val" or "let" or "symbol" or "model")
                        throw Error("[EXPORT_KIND_PREFIX_IN_GROUP] typed export kind belongs before the group");
                    items.Add(ParseExportItem(kind));
                    if (Take(","))
                    {
                        if (Take("}")) break;
                        continue;
                    }
                    if (Take("}")) break;
                    RejectExportSuffix();
                    throw Error("[EXPORT_MALFORMED_LIST] expected ',' or '}' after export item");
                }
            }
            else items.Add(ParseExportItem(kind));

            RejectExportSuffix();
            if (!Take(";"))
                throw Error("[EXPORT_SEMICOLON_REQUIRED] export declarations must end with ';'");
            return new PendingExport(scope, kind, items.AsReadOnly(), at);
        }

        private (string Local, string? External, bool Symbol, bool SelfIdentity)
            ParseExportItem(string? kind)
        {
            if (Take("#"))
            {
                string local = "#" + ExportIdentifier();
                if (kind is not null && kind != "symbol")
                    throw Error("[EXPORT_KIND_MISMATCH] Symbol literal does not match typed export kind");
                bool selfIdentity = false;
                if (Take("as"))
                {
                    if (Take("#"))
                    {
                        string target = "#" + ExportIdentifier();
                        if (!string.Equals(target, local, StringComparison.Ordinal))
                            throw Error("[EXPORT_SYMBOL_IDENTITY_MISMATCH] Symbol export identity must be unchanged");
                        selfIdentity = true;
                    }
                    else if (IsQuoted(Current.Text))
                        throw Error("[EXPORT_LITERAL_ALIAS_UNSUPPORTED] runtime literals are not export binding names");
                    else
                        throw Error("[EXPORT_SYMBOL_BINDING_ALIAS_UNSUPPORTED] Symbol identities are not ordinary binding aliases");
                }
                return (local, null, true, selfIdentity);
            }

            if (kind == "symbol")
            {
                if (IsIdentifier(Current.Text) &&
                    _index + 2 < tokens.Count &&
                    tokens[_index + 1].Text == "as" &&
                    tokens[_index + 2].Text == "#")
                    throw Error("[EXPORT_SYMBOL_IDENTITY_TARGET_REQUIRES_SYMBOL_SOURCE] a Symbol identity target requires a #symbol source");
                throw Error("[EXPORT_SYMBOL_LITERAL_REQUIRED] Symbol exports name nominal #symbol identities");
            }

            string name = ExportIdentifier();
            string? alias = null;
            if (Take("as"))
            {
                if (Current.Text == "#")
                    throw Error("[EXPORT_SYMBOL_IDENTITY_TARGET_REQUIRES_SYMBOL_SOURCE] only a Symbol source may use Symbol identity after 'as'");
                if (IsQuoted(Current.Text))
                    throw Error("[EXPORT_LITERAL_ALIAS_UNSUPPORTED] runtime literals are not export binding names");
                alias = ExportIdentifier();
            }
            return (name, alias, false, false);
        }

        private string ExportIdentifier()
        {
            if (!IsIdentifier(Current.Text))
                throw Error("[EXPORT_MALFORMED_TARGET] expected export identifier");
            string result = Current.Text;
            _index++;
            return result;
        }

        private static bool IsIdentifier(string value) =>
            value.Length > 0 &&
            (value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_') &&
            value.Skip(1).All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

        private static bool IsQuoted(string value) =>
            value.Length >= 2 &&
            ((value[0] == '\'' && value[^1] == '\'') ||
             (value[0] == '"' && value[^1] == '"'));

        private void RejectExportSuffix()
        {
            if (Current.Text is "." or ":" or "/")
                throw Error("[EXPORT_QUALIFIED_TARGET_UNSUPPORTED] v1 local exports use simple local names only");
            if (Current.Text is "<" or ">")
                throw Error("[EXPORT_GENERIC_TARGET_UNSUPPORTED] export generic instantiations are not supported");
            if (Current.Text == "(")
                throw Error("[EXPORT_CALLABLE_SIGNATURE_UNSUPPORTED] export overload selectors use the logical family name");
            if (Current.Text == "{")
                throw Error("[EXPORT_NESTED_GROUP] nested export groups are not supported");
        }

        private static IReadOnlyList<SourceExportBinding> ResolveExports(
            IReadOnlyList<PendingExport> exports, bool exposed)
        {
            var bindings = new List<SourceExportBinding>();
            var external = new Dictionary<string, SourceExportBinding>(StringComparer.Ordinal);

            void Add(SourceExportBinding candidate)
            {
                if (external.TryGetValue(candidate.ExternalName, out var prior))
                {
                    if (prior.LocalSymbolId == candidate.LocalSymbolId &&
                        prior.Scope == candidate.Scope) return;
                    if (prior.LocalSymbolId == candidate.LocalSymbolId)
                        throw new FormatException("[EXPORT_SCOPE_CONFLICT] source binding has conflicting scopes");
                    throw new FormatException("[EXPORT_ALIAS_COLLISION] external export name is already bound");
                }
                external.Add(candidate.ExternalName, candidate);
                bindings.Add(candidate);
            }

            if (exposed)
                Add(new SourceExportBinding("$root", "$root::fnc::main", "fnc",
                    "main", "main", SourceExportScope.Public, SourceExportOrigin.ImplicitPub));

            foreach (var declaration in exports)
            {
                foreach (var item in declaration.Items)
                {
                    if (item.Symbol)
                        throw new FormatException("[EXPORT_BACKEND_UNSUPPORTED_KIND] .NET scalar bootstrap has no Symbol declarations");
                    if (declaration.Kind is not null && declaration.Kind != "fnc")
                    {
                        if (item.Local == "main")
                            throw new FormatException("[EXPORT_KIND_MISMATCH] typed export kind does not match local fnc main");
                        throw new FormatException("[EXPORT_BACKEND_UNSUPPORTED_KIND] .NET scalar bootstrap only lowers fnc main");
                    }
                    if (item.Local != "main")
                        throw new FormatException("[EXPORT_UNKNOWN_TARGET] .NET scalar bootstrap has only local fnc main");
                    if (exposed && declaration.Scope != SourceExportScope.Public)
                        throw new FormatException("[EXPORT_SCOPE_CONFLICT] public main cannot be exported at narrower scope");
                    Add(new SourceExportBinding("$root", "$root::fnc::main", "fnc",
                        "main", item.External ?? "main", declaration.Scope,
                        SourceExportOrigin.ExplicitExport));
                }
            }
            return bindings.AsReadOnly();
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
            if (Current.Text == "|>")
                throw new FormatException(
                    $"Value pipeline requires named callable lowering; unsupported in .NET i64 bootstrap at offset {Current.Offset}");
            if (Current.Text == "?")
                throw new FormatException(
                    $"Ternary expression requires bool typing and CFG branch-merge lowering in .NET Native AOT backend at offset {Current.Offset}");
            return left;
        }

        private Expression ParseMul(int depth)
        {
            CheckDepth(depth);
            Expression left = ParseUnary(depth + 1);
            while (Current.Text is "*" or "/")
            {
                char op = Current.Text[0];
                // Reference Oreslang int/int has a float result. This i64-only
                // frontend must not silently truncate it using CIL div.
                if (op == '/')
                    throw new FormatException("Division requires float result lowering; unsupported in the i64 source subset");
                _index++;
                left = new Binary(op, left, ParseUnary(depth + 1));
            }
            return left;
        }

        private Expression ParseUnary(int depth)
        {
            CheckDepth(depth);
            if (Current.Text == "rt")
            {
                int at = Current.Offset;
                _index++;
                string operation = Current.Text;
                if (operation is "ref" or "unref" or "deref" or "raw")
                    throw new FormatException(
                        $"Pointer-style operation 'rt {operation}' is not valid Oreslang source; use rt copy/share/take/borrow or rt mut borrow; unsupported in the .NET scalar backend at offset {at}");

                string ownership = operation;
                if (operation == "mut" && _index + 1 < tokens.Count &&
                    tokens[_index + 1].Text == "borrow")
                    ownership = "mut borrow";
                if (operation == "borrow" && _index + 1 < tokens.Count &&
                    tokens[_index + 1].Text == "mut")
                    throw new FormatException(
                        $"Legacy ownership spelling 'rt borrow mut' is rejected; use 'rt mut borrow' at offset {at}");
                if (ownership is "copy" or "share" or "take" or "borrow" or "mut borrow")
                    throw new FormatException(
                        $"Ownership operation 'rt {ownership}' requires typed actor-arena lowering; unsupported in the .NET scalar backend at offset {at}");

                throw new FormatException(
                    $"Runtime ownership operation requires typed lowering; unsupported in the .NET scalar backend at offset {at}");
            }
            if (Take("+")) return new Unary('+', ParseUnary(depth + 1));
            if (Take("-"))
            {
                // Signed minimum needs special treatment: its magnitude is > long.MaxValue.
                if (Current.Text.Replace("_", "", StringComparison.Ordinal).TrimStart('0') == "9223372036854775808")
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
            if (!long.TryParse(raw.Replace("_", "", StringComparison.Ordinal),
                NumberStyles.None, CultureInfo.InvariantCulture, out long result))
                throw new FormatException($"Invalid i64 at offset {tokens[_index - 1].Offset}");
            return new Literal(result);
        }

        private static void CheckDepth(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("Expression nesting too deep");
        }
    }
}
