using System.Globalization;

namespace Ores.Net.Compiler;

public abstract record Expression;
public sealed record Literal(long Value) : Expression;
public sealed record Unary(char Operator, Expression Value) : Expression;
public sealed record Binary(char Operator, Expression Left, Expression Right) : Expression;
// Named intrinsic only. Single '^' is not AttoLang syntax ('^^' is boolean XOR).
public sealed record NamedBitXor(Expression Left, Expression Right) : Expression;
// Closed-world CLR intrinsic: exact System.Math.Abs(Int64), no dynamic binding.
public sealed record DotnetMathAbsI64(Expression Value) : Expression;
public sealed record DotnetMathMaxI64(Expression Left, Expression Right) : Expression;

public static partial class OresParser
{
    private const int MaxDepth = 128;
    private const int MaxTokens = 100_000;

    /// <summary>
    /// Inspect the bounded actor schema without lowering executable guest code.
    /// The returned metadata is not scheduler admission authority.
    /// </summary>
    public static ActorStateSchemaMetadata InspectActorStateSchema(string source)
    {
        var schema = FindActorStateSchema(Tokenize(source));
        return schema ?? throw new FormatException(
            "Source must contain a canonical isolated actor state declaration");
    }

    public static Expression Parse(string source, DotnetHostImportPolicy? policy = null)
    {
        var tokens = Tokenize(source);
        RejectUnsupportedSourceActors(tokens);
        RejectUnsupportedScalarTokens(tokens);
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Text == "select" &&
                StaticSelectAdmission.IsStaticSelectFollower(tokens[i + 1].Text))
                throw StaticSelectAdmission.Unsupported(tokens[i].Offset);
        }
        return new Reader(tokens, policy ?? DotnetHostImportPolicy.DenyAll).ParseProgram();
    }

    private static List<Token> Tokenize(string source)
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
        ActorPubSubSourceAdmission.RejectUnsupported(source);
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
        return tokens;
    }

    private static void RejectUnsupportedScalarTokens(List<Token> tokens)
    {
        // Source actors are a semantic boundary, not an alternate spelling of scalar
        // functions. Canonical isolated actors already got the schema-aware rejection
        // (RejectUnsupportedSourceActors); every other actor declaration fails here,
        // before scalar parsing can misdiagnose it as fnc. Forms with their own gates
        // keep them: 'define virtual actor' and 'define supervisor ...'.
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "define")
                continue;
            int limit = Math.Min(tokens.Count, i + 5);
            for (int j = i + 1; j < limit; j++)
            {
                if (tokens[j].Text is "virtual" or "supervisor" or "class" or "enum" or "fnc" or "routine")
                    break;
                if (tokens[j].Text == "actor")
                    throw new FormatException(
                        $"Source actor lowering requires sealed state/handle_state and " +
                        $"scheduler-owned logical continuation frames at offset {tokens[j].Offset}");
            }
        }
        // Nested Future/Option wrappers are rejected inside the lexer (see
        // Lexer.NestedSameWrapper): this scalar lexer never emits '<' tokens,
        // so a post-lex token scan could not observe them.
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            // Native AOT has no isolate-scoped ETS, local pg actor membership
            // or mailbox delivery lowering. Reject before OresIR/CIL emission.
            // The lexer has already stripped comments, so trivia is harmless.
            if ((tokens[i].Text is "ets" or "pg") && tokens[i + 1].Text == ".")
                throw new FormatException(
                    $"ETS/pg guest APIs require authenticated isolate, table and actor-mailbox lowering (unsupported .NET bootstrap) at offset {tokens[i].Offset}");
            // Actor 'pipe' is a contextual graph operator, not scalar '|>'.
            // Shared token-aware admission keeps comments and identifier 'pipe'
            // inert while forbidding untyped graph lowering to Native AOT.
            if (ActorPipeAdmission.IsReservedForm(
                i > 2 ? tokens[i - 3].Text : "",
                i > 1 ? tokens[i - 2].Text : "",
                i > 0 ? tokens[i - 1].Text : "",
                tokens[i].Text, tokens[i + 1].Text))
                throw ActorPipeAdmission.Unsupported(tokens[i].Offset);
            // Both shipped scalar and structured frontends must reject a
            // nominal supervisor BEFORE source/IR lowering. Check tokens,
            // not substrings: nested comments and lookalike identifiers do
            // not mint supervisor CONTROL authority.
            if (tokens[i].Text == "supervisor" &&
                (i > 0 && tokens[i - 1].Text == "define" ||
                 i > 1 && tokens[i - 1].Text == "pub" &&
                 tokens[i - 2].Text == "define"))
                throw new FormatException(
                    $"Source supervisors require typed Failure, owned CONTROL dispatch and Native AOT lowering; unsupported in .NET scalar frontend at offset {tokens[i].Offset}");
            if (tokens[i].Text == "select" &&
                StaticSelectAdmission.IsStaticSelectFollower(tokens[i + 1].Text))
                throw StaticSelectAdmission.Unsupported(tokens[i].Offset);
        }
        // The shipping i64 frontend has no coroutine frames, bounded FIFO
        // critical admission, or authenticated guard owner. Reject with an
        // explicit compiler-owned capability diagnostic, not a generic parser
        // error that could be misreported as feature parity. Lexer-produced
        // tokens keep these words in strings/comments lexically invisible.
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            string word = tokens[i].Text;
            string next = tokens[i + 1].Text;
            // Compiler-owned lexical recognition: the Java/LLVM canonical
            // telemetry scope must not accidentally become an ordinary scalar
            // local or inert call. The current .NET CIL path has neither an
            // exception-region cleanup protocol nor a typed suspendable frame.
            // Lexed tokens, not raw-source regex: comments cannot authorize it.
            if (word == "using" && next == "telemetry" && i + 4 < tokens.Count &&
                tokens[i + 2].Text == "." && tokens[i + 3].Text == "span" &&
                tokens[i + 4].Text == "(")
                throw new FormatException(
                    $"E_TELEMETRY_SCOPE_UNSUPPORTED: .NET NativeAOT requires compiler-owned begin/end, lexical cleanup and suspension-safe scope lowering at offset {tokens[i].Offset}");
            if (word == "async" && next == "fnc")
                throw new FormatException(
                    $"E_CALLABLE_MODIFIERS: async fnc is removed; use suspends at offset {tokens[i].Offset}");
            if ((word == "critical" && next == "suspends") ||
                (word == "suspends" && next == "critical"))
                throw new FormatException(
                    $"E_CALLABLE_MODIFIERS: critical suspends fnc is forbidden at offset {tokens[i].Offset}");
            if (word == "critical")
                throw new FormatException(
                    $"E_CRITICAL_UNSUPPORTED: guarded admission and non-suspending body effect checks require verified .NET lowering at offset {tokens[i].Offset}");
            if (word is "suspends" or "await")
                throw new FormatException(
                    $"E_SUSPENDS_UNSUPPORTED: suspension requires .NET continuation/effect lowering at offset {tokens[i].Offset}");
        }
    }

    private static void RejectUnsupportedSourceActors(List<Token> tokens)
    {
        var schema = FindActorStateSchema(tokens);
        if (schema is not null)
            throw new FormatException(
                $"Source actor state schema parsed; sealed handle_state and " +
                $"generation-fenced logical-turn lowering is unavailable at offset {schema.StateOffset}");
    }

    private static ActorStateSchemaMetadata? FindActorStateSchema(List<Token> tokens)
    {
        static bool IsIdentifier(Token token)
        {
            if (token.Text.Length == 0 ||
                token.Text[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
                return false;
            for (int k = 1; k < token.Text.Length; k++)
                if (token.Text[k] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '_'))
                    return false;
            return true;
        }

        Token At(int index)
        {
            if (index < 0 || index >= tokens.Count)
                return tokens[^1];
            return tokens[index];
        }

        ActorStateSchemaMetadata? discovered = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "define")
                continue;

            int j = i + 1;
            if (At(j).Text == "pub") j++;
            bool isolated = At(j).Text == "isolated";
            if (isolated) j++;
            if (At(j).Text != "actor")
                continue;

            int actorOffset = At(j).Offset;
            j++;
            if (!isolated)
                throw new FormatException(
                    $"Source actor lowering requires sealed state/handle_state and " +
                    $"scheduler-owned logical continuation frames at offset {actorOffset}");

            if (!IsIdentifier(At(j)))
                throw new FormatException(
                    $"Isolated actor requires a nominal actor name at offset {At(j).Offset}");
            string actorName = At(j).Text;
            j++;
            if (At(j).Text != "as")
                throw new FormatException(
                    $"Isolated actor requires 'as' before sealed state at offset {At(j).Offset}");
            j++;

            // Accept canonical as-state and legacy outer declaration braces
            // only for metadata admission. Neither form reaches lowering.
            if (At(j).Text == "{") j++;
            if (At(j).Text != "state")
                throw new FormatException(
                    $"Isolated actor requires sealed state {{ ... }} before methods at offset {At(j).Offset}");
            int stateOffset = At(j).Offset;
            j++;
            if (At(j).Text != "{")
                throw new FormatException(
                    $"Actor state requires '{{' at offset {At(j).Offset}");
            j++;

            var names = new HashSet<string>(StringComparer.Ordinal);
            var metadataFields = new List<ActorStateFieldMetadata>();
            int fields = 0;
            while (At(j).Text != "}")
            {
                if (At(j).Text.Length == 0)
                    throw new FormatException(
                        $"Unterminated actor state schema at offset {stateOffset}");
                if (fields == 16)
                    throw new FormatException(
                        $"Actor state schema field limit exceeded (16) at offset {At(j).Offset}");
                if (!IsIdentifier(At(j)))
                    throw new FormatException(
                        $"Actor state field requires an identifier at offset {At(j).Offset}");
                string name = At(j).Text;
                int fieldOffset = At(j).Offset;
                if (!names.Add(name))
                    throw new FormatException(
                        $"Duplicate actor state field '{name}' at offset {At(j).Offset}");
                j++;
                if (At(j).Text != ":")
                    throw new FormatException(
                        $"Actor state field '{name}' requires ':' at offset {At(j).Offset}");
                j++;
                string type = At(j).Text;
                if (type is not ("int" or "bool"))
                    throw new FormatException(
                        $"Actor state schema admits only int/bool fields in this metadata slice at offset {At(j).Offset}");
                j++;
                if (At(j).Text != "=")
                    throw new FormatException(
                        $"Actor state field '{name}' requires a literal default at offset {At(j).Offset}");
                j++;

                long intDefault = 0;
                bool booleanDefault = false;
                if (type == "int")
                {
                    bool negative = At(j).Text == "-";
                    if (negative) j++;
                    string raw = At(j).Text.Replace("_", "", StringComparison.Ordinal);
                    if (raw.Length == 0 || raw.Any(c => c is < '0' or > '9'))
                        throw new FormatException(
                            $"Actor int state default must be a signed i64 literal at offset {At(j).Offset}");
                    if (negative) raw = "-" + raw;
                    if (!long.TryParse(raw, NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out intDefault))
                        throw new FormatException(
                            $"Actor int state default overflows signed i64 at offset {At(j).Offset}");
                    j++;
                }
                else
                {
                    if (At(j).Text is not ("true" or "false"))
                        throw new FormatException(
                            $"Actor bool state default must be true or false at offset {At(j).Offset}");
                    booleanDefault = At(j).Text == "true";
                    j++;
                }

                if (At(j).Text != ";")
                    throw new FormatException(
                        $"Actor state field '{name}' requires ';' at offset {At(j).Offset}");
                j++;
                metadataFields.Add(new ActorStateFieldMetadata(
                    fields, name,
                    type == "int" ? ActorStateFieldKind.Int64 : ActorStateFieldKind.Boolean,
                    intDefault, booleanDefault, fieldOffset));
                fields++;
            }
            if (fields == 0)
                throw new FormatException(
                    $"Actor state schema requires at least one field at offset {stateOffset}");

            if (discovered is not null)
                throw new FormatException(
                    $"Multiple actor declarations cannot share one isolated actor schema at offset {actorOffset}");
            discovered = new ActorStateSchemaMetadata(actorName, actorOffset,
                stateOffset, metadataFields);
        }
        return discovered;
    }
    private readonly record struct Token(string Text, int Offset);

    private sealed class Lexer(string text)
    {
        private int _i;
        private bool previousWasWith;

        // Whitespace, line comments and nested block comments. Shared by Read()
        // and lexical lookahead so comments stay invisible in both.
        private static int SkipTrivia(string text, int i)
        {
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    i += 2;
                    while (i < text.Length && text[i] is not ('\n' or '\r' or '\u0085' or '\u2028' or '\u2029')) i++;
                    continue;
                }
                // Keep comments lexically invisible even when they occur
                // between a tagged namespace and '.' in a rejected pipeline.
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var offset = i;
                    i += 2;
                    var depth = 1;
                    while (depth > 0)
                    {
                        if (i == text.Length)
                            throw new FormatException($"Unterminated block comment at offset {offset}");
                        if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                        {
                            if (++depth > 128)
                                throw new FormatException($"Block comment nesting exceeds 128 at offset {offset}");
                            i += 2;
                        }
                        else if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/')
                        {
                            depth--;
                            i += 2;
                        }
                        else i++;
                    }
                    continue;
                }
                break;
            }
            return i;
        }

        // True when `wrapper` (just read, ending at i) is followed by
        // '<' wrapper '<' -- e.g. Future<Future<...  -- ignoring trivia.
        private static bool NestedSameWrapper(string text, int i, string wrapper)
        {
            i = SkipTrivia(text, i);
            if (i >= text.Length || text[i] != '<') return false;
            i = SkipTrivia(text, i + 1);
            int word = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            if (!text.AsSpan(word, i - word).SequenceEqual(wrapper)) return false;
            i = SkipTrivia(text, i);
            return i < text.Length && text[i] == '<';
        }

        public Token Read()
        {
            _i = SkipTrivia(text, _i);
            var start = _i;
            if (_i == text.Length) return new Token("", start);
            var ch = text[_i];
            if (ch is '\'' or '"' or '\u0060')
            {
                _i = QuotedSourceSyntax.Scan(text, start);
                return new Token(text[start.._i], start);
            }
            // Lexical-context guard: comments/whitespace were skipped above.
            // Preserve ordinary identifiers 'with(x)' and inert comments.
            bool precededByWith = previousWasWith;
            previousWasWith = false;
            if (precededByWith && ch == '{')
                throw new FormatException(
                    $"WITH_BACKEND_UNSUPPORTED: immutable with requires checked rt copy/take, "
                    + $"owned field layout/drop and Native AOT reconstruction at offset {_i}");
            // No unchecked array access can enter the shipping scalar CIL path.
            SafeCollectionSourceAdmission.RejectUnverifiedBracket(ch, start);
            // Scalar and structured CIL frontends must agree: state-changing
            // annotations are compiler effects, never inert metadata. Check
            // only lexed tokens, after comments have been skipped.
            if (ch == '@')
            {
                int nameEnd = _i + 1;
                while (nameEnd < text.Length &&
                    (text[nameEnd] is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                        or >= '0' and <= '9' or '_')) nameEnd++;
                string name = text[(_i + 1)..nameEnd];
                if (name is "Inject" or "StateBacked" or "MutableStateContainer" or "StateContainer")
                    throw new FormatException(
                        $"STATE_INJECTION_UNSUPPORTED: @{name} requires typed container ownership, borrow checks and Native AOT lowering at offset {start}");
                throw new FormatException($"Annotations require verified compiler lowering at offset {start}");
            }
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
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')
            {
                _i++;
                while (_i < text.Length &&
                    (text[_i] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                    _i++;
                var word = text[start.._i];
                // G0: do not allow unlowered native authority to be spelled as
                // an ordinary identifier, parameter, member or callable.
                if (word == "unsafe")
                    throw new FormatException(
                        $"unsafe boundary requires checked effects, native grants and Native AOT lowering at offset {start}");
                previousWasWith = word == "with";
                // G0: unsupported unsafe source must not become a normal
                // identifier, callable, member or intrinsic in scalar CIL.
                if (word == "unsafe")
                    throw new FormatException(
                        $"unsafe boundary requires checked effects, native grants and Native AOT lowering at offset {start}");
                if (word is "any" or "unknown" or "Any" or "Unknown")
                    throw new FormatException(
                        "any/unknown requires tagged opaque-value ABI and Native AOT runtime descriptors");
                // This bootstrap compiler is single-file. Do not accept Java
                // project-root, zed package or bare import declarations until
                // a verified source graph / native AOT linker exists.
                if (word == "import")
                {
                    // General imports remain unsupported by this scalar compiler.
                    // Only the pinned 'import dotnet' adapter may enter the reader.
                    int next = _i;
                    while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                    const string hostKeyword = "dotnet";
                    if (!text.AsSpan(next).StartsWith(hostKeyword, StringComparison.Ordinal) ||
                        (next + hostKeyword.Length < text.Length &&
                        (char.IsLetterOrDigit(text[next + hostKeyword.Length]) ||
                         text[next + hostKeyword.Length] == '_')))
                        throw new FormatException("source imports require verified module graph lowering");
                }
                if (word is "cpu" or "gpu" or "quantum" ||
                    (word == "nb" && !StaticSelectAdmission.FollowedByStaticSelect(text, _i)))
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
                // The shipping i64-only frontend has no tagged sum storage,
                // constructor discriminants, payload lifetime handling or
                // ordinary-throw-to-Option trap lowering. Keep an explicit
                // unsupported-feature category rather than a generic parse
                // error that might be mistaken for an ADT semantic rejection.
                // This check occurs after comment/trivia skipping; an inert
                // comment is never proof that executable tagged source exists.
                // Nested wrappers are semantically distinct source types
                // (contracts/v1/strict-wrapper-nesting-v1.json). Reject them
                // for that reason, before the generic tagged-sum gate and
                // before '<' would fail as incidental punctuation.
                if (word is "Future" or "Option" && NestedSameWrapper(text, _i, word))
                    throw new FormatException(word == "Future"
                        ? $"Nested Future remains nested; implicit Future flatten is forbidden; " +
                          $".NET source Future continuation lowering is unavailable at offset {start}"
                        : $"Nested Option remains nested; implicit Option flatten is forbidden; " +
                          $".NET source Option lowering is unavailable at offset {start}");
                if (word is "Option" or "Result" or "Some" or "None" or "Ok" or "Err" or "trap")
                    throw new FormatException(
                        $"Option/Result/trap requires verified tagged-sum CIL and NativeAOT guest-control lowering at offset {start}");
                // Reject here, not only in the reader: a guard condition such
                // as `1>0` contains characters this scalar lexer cannot lex,
                // which would otherwise surface as a generic lexer error.
                if (word == "guard")
                    throw new FormatException(
                        $"GUARD_BACKEND_UNSUPPORTED: early-exit CFG and ownership lowering required at offset {start}");
                if (word is "gen" or "generator")
                    throw new FormatException(
                        $"generator declarations require iterator-frame and continuation lowering in .NET Native AOT backend at offset {start}");
                return new Token(word, start);
            }
            if (ch == '"')
            {
                // Host assembly/type names are literal-only. Reject interpolation,
                // escapes, unclosed strings and line breaks before any resolution.
                _i++;
                while (_i < text.Length && text[_i] != '"')
                {
                    if (text[_i] is '\\' or '\n' or '\r' or '\u0085' or '\u2028' or '\u2029')
                        throw new FormatException($"Invalid host import literal at offset {start}");
                    _i++;
                }
                if (_i == text.Length)
                    throw new FormatException($"Unterminated host import literal at offset {start}");
                _i++;
                return new Token(text[start.._i], start);
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
            if ("()+-*/{}:;.,=?|<>".IndexOf(ch) >= 0)
            {
                _i++;
                return new Token(text[start.._i], start);
            }
            if (ch == '%')
                throw new FormatException($"noncanonical '%' source operator; use named Integer.rem/mod at offset {start}");
            if (ch == '^')
            {
                // Rejected after lexical trivia and before any scalar instruction
                // reaches typed IR or CIL. `^^` is the canonical boolean XOR
                // (spec ch.16), but this frontend only types int scalars, so any
                // `^^` here has non-bool operands; the structured frontend lowers
                // it. Single `^` is not AttoLang syntax (use Integer.bitXor).
                if (_i + 1 < text.Length && text[_i + 1] == '^')
                    throw new FormatException(
                        $"Logical operator requires bool operands: '^^' in scalar int source at offset {start}");
                throw new FormatException(
                    $"Reserved AttoLang operator '^'; use Integer.bitXor(a, b) at offset {start}");
            }
            throw new FormatException($"Invalid character at offset {start}");
        }
    }

    private sealed class Reader(List<Token> tokens, DotnetHostImportPolicy policy)
    {
        private int _index;
        private string? _dotnetMathAlias;
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
            if (Take("import"))
            {
                Expect("dotnet");
                // An import is an authority request, not an authority grant.
                // Deny before interpreting even a permitted System.Math alias.
                policy.RequireMathI64();
                // Explicit, finite v0 target. No dynamic assembly loading,
                // arbitrary reflection, overload inference or host object escape.
                if (Current.Text != "\"System.Math\"")
                    throw new FormatException("DOTNET_INTEROP_UNSUPPORTED: only System.Math is allowlisted");
                _index++;
                Expect("as");
                string alias = Current.Text;
                if (alias.Length == 0 || alias[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_') ||
                    alias.Any(ch => ch is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
                    alias is "Integer" or "main" or "int" or "fnc" or "return")
                    throw new FormatException("Invalid or reserved dotnet import alias");
                _index++;
                _dotnetMathAlias = alias;
                Expect(";");
            }
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
            if (Current.Text == "state" && _index + 1 < tokens.Count && tokens[_index + 1].Text == "container")
                throw new FormatException($"STATE_CONTAINER_BACKEND_UNSUPPORTED: protected heap region and critical guard lowering required at offset {Current.Offset}");
            if (Current.Text == "virtual" && _index + 1 < tokens.Count && tokens[_index + 1].Text == "actor")
                throw new FormatException($"VIRTUAL_ACTOR_BACKEND_UNSUPPORTED: logical key and activation fencing required at offset {Current.Offset}");
            // Scalar CIL and NativeAOT do not lower nominal ModelGraphs.
            // 'model' is contextual, not globally reserved.
            if (Current.Text == "model")
                throw new FormatException(
                    $"MODEL_LOWERING_UNSUPPORTED: .NET requires typed nominal ModelGraph lowering at offset {Current.Offset}");
            Expect("fnc");
            Expect("main");
            Expect("(");
            Expect(")");
            if (!Take(":")) Expect("->");
            Expect("int");
            Expect("{");
            if (Current.Text == "guard")
                throw new FormatException(
                    $"GUARD_BACKEND_UNSUPPORTED: early-exit CFG and ownership lowering required at offset {Current.Offset}");
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
                var rhs = ParseUnary(depth + 1);
                left = new Binary(op, left, rhs);
            }
            return left;
        }

        private Expression ParseUnary(int depth)
        {
            CheckDepth(depth);
            if (Current.Text.Length > 0 && Current.Text[0] is '\'' or '"' or '\u0060')
                throw new FormatException(
                    $"STRING_BACKEND_UNSUPPORTED: quoted strings and template interpolation require typed String CIL/NativeAOT lowering at offset {Current.Offset}");
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
            if (_dotnetMathAlias is not null && Take(_dotnetMathAlias))
            {
                Expect(".");
                if (Take("Abs"))
                {
                    Expect("(");
                    var value = ParseAdd(depth + 1);
                    Expect(")");
                    return new DotnetMathAbsI64(value);
                }
                if (Take("Max"))
                {
                    Expect("(");
                    var left = ParseAdd(depth + 1);
                    Expect(",");
                    var right = ParseAdd(depth + 1);
                    Expect(")");
                    return new DotnetMathMaxI64(left, right);
                }
                throw new FormatException(
                    $"DOTNET_INTEROP_UNSUPPORTED: only Math.Abs(i64) and Math.Max(i64,i64) are allowlisted at offset {Current.Offset}");
            }
            // The scalar subset admits only this one typed Integer intrinsic.
            // Parser-owned name resolution rejects arbitrary Integer.* calls.
            if (Take("Integer"))
            {
                Expect(".");
                if (!Take("bitXor"))
                    throw new FormatException(
                        $"Unsupported Integer intrinsic at offset {Current.Offset}");
                Expect("(");
                var left = ParseAdd(depth + 1);
                Expect(",");
                var right = ParseAdd(depth + 1);
                Expect(")");
                return new NamedBitXor(left, right);
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
