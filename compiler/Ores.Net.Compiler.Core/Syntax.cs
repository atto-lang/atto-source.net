using System.Globalization;

namespace Ores.Net.Compiler;

public abstract record Expression;
public sealed record Literal(long Value) : Expression;
public sealed record Unary(char Operator, Expression Value) : Expression;
public sealed record Binary(char Operator, Expression Left, Expression Right) : Expression;
// Named intrinsic only. Single '^' is not AttoLang syntax ('^^' is boolean XOR).
public sealed record NamedBitXor(Expression Left, Expression Right) : Expression;
// Named signed-64 Integer.rem/mod/pow (Java StrictIntegerArithmetic parity):
// truncating remainder, Euclidean modulus, checked nonnegative power.
public sealed record NamedIntegerRem(Expression Left, Expression Right) : Expression;
public sealed record NamedIntegerMod(Expression Left, Expression Right) : Expression;
public sealed record NamedIntegerPow(Expression Left, Expression Right) : Expression;
// Closed-world CLR intrinsic: exact System.Math.Abs(Int64), no dynamic binding.
public sealed record DotnetMathAbsI64(Expression Value) : Expression;
public sealed record DotnetMathMaxI64(Expression Left, Expression Right) : Expression;

// Shared by the scalar and structured frontends so unsupported mutable sharing
// cannot be diagnosed differently before native CIL lowering exists.
internal static class MutableShareAdmission
{
    internal const string DiagnosticPrefix =
        "native rt mut share requires verified SharedMutex<T> capability, guarded lowering and allocation-domain proof in .NET Native AOT at offset ";

    internal static FormatException Unsupported(int offset) =>
        new(DiagnosticPrefix + offset);
}

// One fail-closed diagnostic shared by both native source frontends. Static
// F64 host layout metadata does NOT authorize source inverted-class lowering.
internal static class InvertedClassNativeAotAdmission
{
    internal static FormatException Unsupported(int offset) =>
        new($"Inverted class requires checked bracket-row and ownership lowering in the .NET backend at offset {offset}");
}

public static partial class OresParser
{
    private const int MaxDepth = 128;
    private const int MaxTokens = 100_000;
    private const int MaxMacroDeclarations = 8;
    private const int MaxMacroExpansions = 128;
    private const int MaxMacroConstSteps = 512;
    private const int MaxMacroConstDepth = 64;

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

    public static Expression Parse(string source, DotnetHostImportPolicy? policy = null,
                                   int? languageVersion = null)
    {
        if (languageVersion.HasValue && languageVersion.Value is not (1 or 2))
            throw new FormatException("E_LANGUAGE_VERSION: expected language version 1 or 2");
        var tokens = StripFileExports(Tokenize(source, allowCaretReturn: languageVersion.HasValue), source);
        RejectUnsupportedSourceActors(tokens);
        RejectUnsupportedInvertedClasses(tokens);
        RejectUnsupportedScalarTokens(tokens);
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Text == "select" &&
                StaticSelectAdmission.IsStaticSelectFollower(tokens[i + 1].Text))
                throw StaticSelectAdmission.Unsupported(tokens[i].Offset);
        }
        return new Reader(tokens, policy ?? DotnetHostImportPolicy.DenyAll, source, languageVersion).ParseProgram();
    }

    /// Parse file-root exports using the shared explicit-export grammar.
    /// The NativeAOT scalar entrypoint currently implements only fnc main;
    /// other target kinds and package linking must remain explicit errors.
    private static List<Token> StripFileExports(List<Token> tokens, string source)
    {
        var stripped = new List<Token>(tokens.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        int braces = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Text == "export")
            {
                if (braces != 0)
                    throw new FormatException($"EXPORT_SCOPE: exports must be file-level at offset {token.Offset}");
                // Legacy source-only export syntax remains in the standalone
                // grammar adapter; compiler admission requires canonical pub.
                if (i + 1 >= tokens.Count || tokens[i + 1].Text != "pub")
                    throw new FormatException($"EXPORT_PUB_REQUIRED: use 'export pub' at offset {token.Offset}");
                int j = i + 1;
                while (j < tokens.Count && tokens[j].Text is not ("" or ";")) j++;
                if (j >= tokens.Count || tokens[j].Text != ";")
                    throw new FormatException($"EXPORT_SEMICOLON_REQUIRED: export at offset {token.Offset}");
                var declaration = ExplicitExportSyntax.Parse(source[token.Offset..(tokens[j].Offset + 1)]);
                if (declaration.Items.Count != 1 || declaration.Items[0].LocalName != declaration.Items[0].ExternalName)
                    throw new FormatException($"EXPORT_SCALAR_UNSUPPORTED: grouped/aliased exports at offset {token.Offset}");
                string name = declaration.Items[0].LocalName;
                if (!names.Add(name))
                    throw new FormatException($"EXPORT_DUPLICATE: duplicate export {name} at offset {token.Offset}");
                if (declaration.Kind is not (null or ExplicitExportKind.Fnc) || name != "main")
                    throw new FormatException($"EXPORT_BACKEND_UNSUPPORTED: only fnc main is lowered in .NET NativeAOT scalar compiler at offset {token.Offset}");
                if (declaration.Scope != ExplicitExportScope.Public)
                    throw new FormatException($"EXPORT_PACKAGE_LINKER_UNSUPPORTED: package-only main needs module-scoped linkage at offset {token.Offset}");
                i = j;
                continue;
            }
            if (token.Text == "{") braces++;
            else if (token.Text == "}") braces--;
            stripped.Add(token);
        }
        return stripped;
    }

    private static List<Token> Tokenize(string source, bool allowCaretReturn = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576)
            throw new FormatException("Source is larger than the 1 MiB bootstrap limit");
        // Validate original UTF-16 before lexing or comment removal.
        // Report the first offending code-unit offset, matching the structured
        // frontend while retaining the scalar admission diagnostic categories.
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\0')
                throw new FormatException($"Source contains NUL byte at offset {i}");
            // Original UTF-16 source must reject raw terminal controls in comments/literals.
            if (c == (char)0x001b || c == (char)0x007f)
                throw new FormatException($"Source contains terminal control at offset {i}");
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= source.Length || !char.IsLowSurrogate(source[i + 1]))
                    throw new FormatException($"Source contains invalid UTF-16 at offset {i}");
                i++; // Valid supplementary scalar consumes two UTF-16 units.
            }
            else if (char.IsLowSurrogate(c))
                throw new FormatException($"Source contains invalid UTF-16 at offset {i}");
        }
        // Public parser callers supply UTF-16 strings; the source budget is
        // UTF-8 bytes, matching the disk/CLI boundary, not code-unit count.
        if (System.Text.Encoding.UTF8.GetByteCount(source) > 1_048_576)
            throw new FormatException("Source exceeds 1 MiB UTF-8 parser limit");
        ActorPubSubSourceAdmission.RejectUnsupported(source);
        // No structural match lowering or readonly shape metadata in scalar CIL yet.
        StructuralShapeAdmission.RejectUnsupportedPatterns(source);
        var lexer = new Lexer(source, allowCaretReturn);
        var tokens = new List<Token>();
        for (;;)
        {
            var token = lexer.Read();
            tokens.Add(token);
            // Multiple actor forward signatures and file namespace headers
            // require an actual nominal actor linker and NativeAOT CIL actor
            // lowering. Detect at the token boundary, before later syntax is
            // mistaken for an executable scalar helper.
            int i = tokens.Count - 1;
            if ((token.Text is "actor" or "isoactor") &&
                i > 0 &&
                (tokens[i - 1].Text == "declare" ||
                 (i > 1 && tokens[i - 1].Text == "pub" &&
                  tokens[i - 2].Text == "declare")))
                throw new FormatException(
                    $"ACTOR_FORWARD_NATIVEAOT_UNSUPPORTED: actor forward requires nominal actor identity, ownership and executable CIL lowering at offset {token.Offset}");
            if (token.Text == "namespace" && i > 0 && tokens[i - 1].Text == "use")
                throw new FormatException(
                    $"NAMESPACE_RUNTIME_NATIVEAOT_UNSUPPORTED: file namespace requires verified nominal actor/linker identity at offset {token.Offset}");
            // Fail at the completed typed receiver header, before tokenizing
            // body operations or field syntax not in the scalar grammar.
            int receiverAt = tokens.Count - 1;
            if (token.Text == ":" &&
                ((receiverAt >= 3 &&
                  tokens[receiverAt - 1].Text == "self" &&
                  tokens[receiverAt - 2].Text == "(" &&
                  tokens[receiverAt - 3].Text == "mtd") ||
                 (receiverAt >= 4 &&
                  tokens[receiverAt - 1].Text == "self" &&
                  tokens[receiverAt - 2].Text == "mut" &&
                  tokens[receiverAt - 3].Text == "(" &&
                  tokens[receiverAt - 4].Text == "mtd")))
            {
                int methodIndex = tokens[receiverAt - 2].Text == "("
                    ? receiverAt - 3 : receiverAt - 4;
                if (methodIndex == 0 ||
                    (tokens[methodIndex - 1].Text != "fnc" &&
                     tokens[methodIndex - 1].Text != "routine"))
                    throw new FormatException(
                        $"MTD_RECEIVER_NATIVEAOT_UNSUPPORTED: typed receiver methods need nominal class slots, borrow checking and CIL lowering at offset {tokens[methodIndex].Offset}");
            }
            // Fail closed on canonical type-first mtd (Foo self) and
            // (Foo mut self) as soon as the header is complete. These are
            // valid AttoLang declarations, but this NativeAOT frontend has
            // no actor/class receiver slots or method borrowing ABI.
            if (token.Text == ")" &&
                ((receiverAt >= 4 &&
                  tokens[receiverAt - 1].Text == "self" &&
                  tokens[receiverAt - 3].Text == "(" &&
                  tokens[receiverAt - 4].Text == "mtd") ||
                 (receiverAt >= 5 &&
                  tokens[receiverAt - 1].Text == "self" &&
                  tokens[receiverAt - 2].Text == "mut" &&
                  tokens[receiverAt - 4].Text == "(" &&
                  tokens[receiverAt - 5].Text == "mtd")))
            {
                int methodIndex = tokens[receiverAt - 2].Text == "mut"
                    ? receiverAt - 5 : receiverAt - 4;
                if (methodIndex == 0 ||
                    (tokens[methodIndex - 1].Text != "fnc" &&
                     tokens[methodIndex - 1].Text != "routine"))
                    throw new FormatException(
                        $"MTD_RECEIVER_NATIVEAOT_UNSUPPORTED: typed receiver methods need nominal actor/class slots, ownership and CIL lowering at offset {tokens[methodIndex].Offset}");
            }
            if (tokens.Count > MaxTokens)
                throw new FormatException("Token count exceeds bootstrap limit");
            if (token.Text.Length == 0) break;
        }
        return tokens;
    }

    private static void RejectUnsupportedScalarTokens(List<Token> tokens,
        IReadOnlySet<int>? protocolActorOffsets = null)
    {
        // The scalar i64 parser is an independent public compiler entrypoint.
        // A Kestrel host HTTP provider does not authorize guest HTTP actors.
        // Deny exact contextual declarations before expression/IR parsing.
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            int j = i;
            if (tokens[j].Text == "pub" && tokens[j + 1].Text == "define") j++;
            if (tokens[j].Text != "define") continue;
            j++;
            bool hasHttpTransport = false;
            while (j < tokens.Count &&
                   tokens[j].Text is "pub" or "private" or "shared" or "untrusted"
                       or "serial" or "http" or "tcp" or "websocket" or "ws"
                       or "stream" or "unary" or "abstract")
            {
                if (tokens[j].Text == "http") hasHttpTransport = true;
                j++;
            }
            if (hasHttpTransport && j < tokens.Count &&
                (tokens[j].Text is "actor" or "isoactor"))
                throw new FormatException(
                    $"HTTP_ACTOR_BACKEND_UNSUPPORTED: .NET scalar compiler requires typed HTTP actor admission, ownership and physical shutdown lowering at offset {tokens[j].Offset}");
        }

        // Source actors are a semantic boundary, not an alternate spelling of scalar
        // functions. Canonical isolated actors already got the schema-aware rejection
        // (RejectUnsupportedSourceActors); every other actor declaration fails here,
        // before scalar parsing can misdiagnose it as fnc. Forms with their own gates
        // keep them: 'define virtual actor' and 'define supervisor ...'.
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "define" || protocolActorOffsets?.Contains(tokens[i].Offset) == true)
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
            if (i + 2 < tokens.Count && tokens[i].Text == "rt" &&
                tokens[i + 1].Text == "mut" && tokens[i + 2].Text == "share")
                throw MutableShareAdmission.Unsupported(tokens[i].Offset);
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
                throw ActorPipeAdmission.Unsupported(tokens[i].Offset, tokens[i].Text);
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

    private static void RejectUnsupportedInvertedClasses(List<Token> tokens)
    {
        // Match source declaration tokens, not raw comments or string text.
        // Both define inverted class and define pub inverted class are denied
        // until typed source HIR, per-field loans and native CIL exist.
        for (int i = 1; i + 1 < tokens.Count; ++i)
        {
            if (tokens[i].Text != "inverted" ||
                tokens[i + 1].Text != "class")
                continue;
            bool declared = tokens[i - 1].Text == "define" ||
                (tokens[i - 1].Text == "pub" && i >= 2 &&
                 tokens[i - 2].Text == "define");
            if (declared)
                throw InvertedClassNativeAotAdmission.Unsupported(tokens[i].Offset);
        }
    }

    private static void RejectUnsupportedSourceActors(List<Token> tokens,
        IReadOnlySet<int>? protocolActorOffsets = null)
    {
        var schema = FindActorStateSchema(tokens, protocolActorOffsets);
        if (schema is not null)
            throw new FormatException(
                $"Source actor state schema parsed; sealed handle_state and " +
                $"generation-fenced logical-turn lowering is unavailable at offset {schema.StateOffset}");
    }

    private static ActorStateSchemaMetadata? FindActorStateSchema(List<Token> tokens,
        IReadOnlySet<int>? protocolActorOffsets = null)
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
            if (tokens[i].Text != "define" || protocolActorOffsets?.Contains(tokens[i].Offset) == true)
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

    private sealed class Lexer(string text, bool allowCaretReturn)
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
            // Exception: the package-scope brackets of a file-level `export pub[pkg]`
            // are export syntax, not collection access (validated by StripFileExports).
            bool exportScopeBracket =
                (ch == '[' && System.Text.RegularExpressions.Regex.IsMatch(text[..start], @"(^|[^A-Za-z0-9_])export\s+pub\s*$")) ||
                (ch == ']' && System.Text.RegularExpressions.Regex.IsMatch(text[..start], @"(^|[^A-Za-z0-9_])export\s+pub\s*\[\s*[A-Za-z_][A-Za-z0-9_]*\s*$"));
            if (!exportScopeBracket)
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
                if (word == "select" &&
                    StaticSelectAdmission.FollowedByStaticSelect(text, start))
                    throw StaticSelectAdmission.Unsupported(start);
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
                // Canonical bang exceptional control must not degrade to an
                // ordinary scalar callable or host-side .NET exception.
                if (word is "trap" or "throw" or "raise" or "panic" &&
                    _i < text.Length && text[_i] == '!' &&
                    (_i + 1 >= text.Length || text[_i + 1] != '=') &&
                    !(text.AsSpan(_i).StartsWith("!eq", StringComparison.Ordinal) &&
                      (_i + 3 == text.Length ||
                       !(char.IsLetterOrDigit(text[_i + 3]) || text[_i + 3] == '_'))))
                    throw new FormatException(
                        $"EXCEPTION_BANG_BACKEND_UNSUPPORTED: {word}! requires typed Error/Result, checked unwind, actor supervision and verified NativeAOT lowering at offset {start}");
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
            if (_i + 1 < text.Length &&
                ((ch == '|' && text[_i + 1] == '>') ||
                 (ch == '<' && text[_i + 1] == '|')))
            {
                _i += 2;
                return new Token(text[start.._i], start);
            }
            if (ch == '-' && _i + 1 < text.Length && text[_i + 1] == '>')
            {
                _i += 2;
                return new Token("->", start);
            }
            // Preserve the explicit free-function header/body separator.
            if (ch == ':' && _i + 1 < text.Length && text[_i + 1] == ':')
            {
                _i += 2;
                return new Token("::", start);
            }
            // Lex assignment punctuation so unsupported declarations are rejected
            // at their declaration, rather than at a later initializer. The
            // arithmetic parser still has no assignment production.
            if ("()+-*/{}:;,.=?[]<>~|!".IndexOf(ch) >= 0)
            {
                _i++;
                return new Token(text[start.._i], start);
            }
            if (ch == '%')
                throw new FormatException($"noncanonical '%' source operator; use named Integer.rem/mod at offset {start}");
            if (ch == '^')
            {
                // Explicitly versioned Atto source has caret-return token.
                // The production parser must still require its use at a
                // statement-return boundary; never reinterpret it as XOR.
                if (allowCaretReturn && !(_i + 1 < text.Length && text[_i + 1] == '^'))
                {
                    _i++;
                    return new Token("^", start);
                }
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

    private sealed partial class Reader(List<Token> tokens, DotnetHostImportPolicy policy, string source, int? languageVersion)
    {
        // Macro definitions are compiler-only. Neither these declarations nor
        // their arguments appear in OresIR/CIL, and no runtime expansion exists.
        private enum MacroKind { ConstIntQuote, SyntaxExprIdentity, SyntaxExprLiteralTwice }
        private sealed record ScalarMacro(string Name, string Parameter, MacroKind Kind);
        private readonly Dictionary<string, ScalarMacro> _macros = new(StringComparer.Ordinal);
        private readonly Dictionary<string, QuantityUnit> _dimensions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, QuantityUnit> _units = new(StringComparer.Ordinal);
        private readonly HashSet<string> _canonicalUnitDimensions = new(StringComparer.Ordinal);
        private int _index;
        private int _macroExpansions;

        private static FormatException UnsupportedMacro(int at, string reason) =>
            new($"NET_MACRO_EXPANDER_REQUIRED: {reason} at offset {at}; " +
                "macro expansion must finish before OresIR/CIL; runtime fallback is forbidden");

        private static bool IsSourceIdentifier(string word)
        {
            if (word.Length is 0 or > 128 || !(word[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
                return false;
            foreach (char ch in word)
                if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                    return false;
            return true;
        }

        private static bool IsMacroName(string word) =>
            IsSourceIdentifier(word) && word is not ("define" or "pub" or "macro" or "return" or "fnc" or "main" or
                "quote" or "unquote" or "eval" or "const" or "syntax" or "expr" or
                "Const" or "Syntax" or "Expr" or "int" or "rt");

        // Closed scalar stage-0 subset also accepted by LLVM #1026:
        //   define macro literal(Const<int> x) -> Syntax<Expr> { return quote(x); }
        //   define macro identity(Syntax<Expr> x) -> Syntax<Expr> { return x; }
        //   define macro twice(Syntax<Expr> x) -> Syntax<Expr> {
        //       return quote(unquote(x) + unquote(x)); }
        // Only closed integer literal arguments can be repeated. This avoids
        // pre-typecheck ownership/effect amplification and never emits runtime
        // source parsing, JIT, dynamic code or reflection.
        // Validate even unused macros: unsupported bodies must never be erased
        // silently by an AOT compiler.
        // Malformed/unsupported macro signatures and bodies have one stable
        // compile-time-only rejection category. Do not let ordinary expression
        // grammar diagnostics disguise an unimplemented macro expander.
        private void ExpectMacro(string expected)
        {
            if (!Take(expected))
                throw UnsupportedMacro(Current.Offset,
                    $"unsupported macro signature/body; expected '{expected}'");
        }

        private void ParseMacroPrelude()
        {
            while (Current.Text is "define" or "pub")
            {
                int checkpoint = _index;
                bool defined = false, exposed = false;
                while (Current.Text is "define" or "pub")
                {
                    if (Take("define"))
                    {
                        if (defined) throw UnsupportedMacro(Current.Offset, "duplicate define modifier");
                        defined = true;
                    }
                    else
                    {
                        ExpectMacro("pub");
                        if (exposed) throw UnsupportedMacro(Current.Offset, "duplicate pub modifier");
                        exposed = true;
                    }
                }
                if (Current.Text != "macro")
                {
                    _index = checkpoint; // Normal main declaration: existing grammar owns it.
                    return;
                }
                int at = Current.Offset;
                _index++;
                if (!defined || exposed)
                    throw UnsupportedMacro(at, "public or unqualified macro declarations require checked export/visibility metadata");
                string name = Current.Text;
                if (!IsMacroName(name))
                    throw UnsupportedMacro(at, "invalid or reserved macro name");
                _index++;
                ExpectMacro("(");
                bool constInput;
                if (Take("Const"))
                {
                    ExpectMacro("<"); ExpectMacro("int"); ExpectMacro(">");
                    constInput = true;
                }
                else if (Take("Syntax"))
                {
                    ExpectMacro("<"); ExpectMacro("Expr"); ExpectMacro(">");
                    constInput = false;
                }
                else
                    throw UnsupportedMacro(at, "only Const<int> and Syntax<Expr> inputs are supported");
                string parameter = Current.Text;
                if (!IsMacroName(parameter))
                    throw UnsupportedMacro(at, "invalid macro parameter name");
                _index++;
                ExpectMacro(")");
                ExpectMacro("->"); ExpectMacro("Syntax"); ExpectMacro("<"); ExpectMacro("Expr"); ExpectMacro(">");
                ExpectMacro("{"); ExpectMacro("return");
                MacroKind kind;
                if (constInput)
                {
                    ExpectMacro("quote"); ExpectMacro("("); ExpectMacro(parameter); ExpectMacro(")");
                    kind = MacroKind.ConstIntQuote;
                }
                else if (Take("quote"))
                {
                    // Deliberately narrow identifier-free typed template:
                    // exactly two direct, same-parameter unquotes with '+'.
                    ExpectMacro("(");
                    ExpectMacro("unquote"); ExpectMacro("("); ExpectMacro(parameter); ExpectMacro(")");
                    ExpectMacro("+");
                    ExpectMacro("unquote"); ExpectMacro("("); ExpectMacro(parameter); ExpectMacro(")");
                    ExpectMacro(")");
                    kind = MacroKind.SyntaxExprLiteralTwice;
                }
                else
                {
                    ExpectMacro(parameter);
                    kind = MacroKind.SyntaxExprIdentity;
                }
                ExpectMacro(";"); ExpectMacro("}");
                if (_macros.Count >= MaxMacroDeclarations)
                    throw UnsupportedMacro(at, "macro declaration budget exceeded");
                if (!_macros.TryAdd(name, new ScalarMacro(name, parameter, kind)))
                    throw UnsupportedMacro(at, "duplicate macro declaration");
            }
        }

        private static long EvaluateMacroConst(Expression expression, int at)
        {
            int steps = 0;
            long Visit(Expression node, int depth)
            {
                if (++steps > MaxMacroConstSteps || depth > MaxMacroConstDepth)
                    throw UnsupportedMacro(at, "NET_MACRO_CONST_BUDGET: closed Const<int> AST budget exceeded");
                try
                {
                    switch (node)
                    {
                        case Literal number: return number.Value;
                        case Unary unary when unary.Operator is '+' or '-':
                        {
                            long value = Visit(unary.Value, depth + 1);
                            return unary.Operator == '-' ? checked(-value) : value;
                        }
                        case Binary binary when binary.Operator is '+' or '-' or '*':
                        {
                            long left = Visit(binary.Left, depth + 1);
                            long right = Visit(binary.Right, depth + 1);
                            return binary.Operator switch
                            {
                                '+' => checked(left + right),
                                '-' => checked(left - right),
                                '*' => checked(left * right),
                                _ => throw UnsupportedMacro(at, "unsupported const binary operator")
                            };
                        }
                        default:
                            throw UnsupportedMacro(at, "Const<int> must be a closed scalar AST");
                    }
                }
                catch (OverflowException ex)
                {
                    throw new FormatException(
                        $"NET_MACRO_CONST_OVERFLOW at offset {at}; runtime fallback is forbidden", ex);
                }
            }
            return Visit(expression, 0);
        }
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

        private string ReadQuantityName()
        {
            var token = Current;
            if (!IsSourceIdentifier(token.Text))
                throw new FormatException($"E_UNIT_NAME: expected a quantity name at offset {token.Offset}");
            _index++;
            return token.Text;
        }

        private QuantityUnit ReadUnit()
        {
            int offset = Current.Offset;
            string name = ReadQuantityName();
            if (!_units.TryGetValue(name, out var unit))
                throw new FormatException($"E_UNIT_NAME: unknown unit '{name}' at offset {offset}");
            return unit;
        }

        private long ReadQuantityFactor()
        {
            var token = Current;
            if (!long.TryParse(token.Text.Replace("_", "", StringComparison.Ordinal),
                    NumberStyles.None, CultureInfo.InvariantCulture, out long factor) || factor <= 0)
                throw new FormatException($"E_UNIT_SCALE: expected a positive signed i64 factor at offset {token.Offset}");
            _index++;
            return factor;
        }

        private void ParseQuantityDeclarations()
        {
            while (Current.Text == "define" && tokens[_index + 1].Text is ("dimension" or "unit"))
            {
                _index++;
                if (_dimensions.Count + _units.Count >= 128)
                    throw new FormatException("E_UNIT_LIMIT: more than 128 quantity declarations");
                bool dimension = Take("dimension");
                if (!dimension) Expect("unit");
                string name = ReadQuantityName();
                if (_dimensions.ContainsKey(name) || _units.ContainsKey(name))
                    throw new FormatException($"E_UNIT_DUPLICATE: quantity name '{name}' is already declared");
                if (dimension)
                {
                    Expect(";");
                    _dimensions.Add(name, QuantityUnit.Base(name));
                    continue;
                }

                Expect(":");
                string dimensionName = ReadQuantityName();
                if (!_dimensions.TryGetValue(dimensionName, out var declared))
                    throw new FormatException($"E_UNIT_NAME: unknown dimension '{dimensionName}'");
                var unit = declared;
                if (Take("="))
                {
                    if (Current.Text.Length != 0 && char.IsAsciiDigit(Current.Text[0]))
                    {
                        long factor = ReadQuantityFactor();
                        Expect("*");
                        unit = ReadUnit().Scale(factor, divide: false);
                    }
                    else
                    {
                        unit = ReadUnit();
                        if (Take("/")) unit = unit.Scale(ReadQuantityFactor(), divide: true);
                        else if (Take("*")) unit = unit.Scale(ReadQuantityFactor(), divide: false);
                    }
                    if (!unit.HasDimension(declared))
                        throw new FormatException($"E_UNIT_DIMENSION: unit '{name}' has the wrong declared dimension");
                }
                else if (!_canonicalUnitDimensions.Add(dimensionName))
                    throw new FormatException($"E_UNIT_DUPLICATE: dimension '{dimensionName}' already has a canonical unit");
                Expect(";");
                _units.Add(name, unit);
            }
        }

        private Expression ParseNumericSuffix(Token token, long value)
        {
            if (_dimensions.Count == 0 || Current.Text != "<"
                    || Current.Offset != token.Offset + token.Text.Length)
                return new Literal(value);
            Expect("<");
            var unit = ReadUnit();
            Expect(">");
            if (Current.Text == "." && tokens[_index + 1].Text != "<")
            {
                _index++;
                Expect("to_unit");
                Expect("<");
                var target = ReadUnit();
                Expect(">");
                Expect("(");
                Expect(")");
                value = unit.ConvertExact(value, target);
                unit = target;
            }
            return new QuantityLiteral(value, unit);
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
            ParseQuantityDeclarations();
            ParseMacroPrelude();
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
            // Scalar NativeAOT has no originating-slot virtual dispatch or
            // compiler-owned mailbox ingress. Deny before parsing fnc main.
            if (Current.Text == "abstract")
                throw new FormatException($"NET_PRIVATE_ABSTRACT_UNSUPPORTED: Private abstract class hooks require typed virtual dispatch; inherited override authority and trusted actor-hook dispatch require typed lowering at offset {Current.Offset}");
            if (Current.Text is "class" or "actor")
                throw new FormatException($"NET_NOMINAL_METHODS_UNSUPPORTED: Nominal class/actor entrypoints require runtime-sealed receiver and CIL lowering at offset {Current.Offset}");
            // Scalar CIL and NativeAOT do not lower nominal ModelGraphs.
            // 'model' is contextual, not globally reserved.
            if (Current.Text == "model")
                throw new FormatException(
                    $"MODEL_LOWERING_UNSUPPORTED: .NET requires typed nominal ModelGraph lowering at offset {Current.Offset}");
            Expect("fnc");
            Expect("main");
            Expect("(");
            Expect(")");
            bool arrowReturn = !Take(":");
            if (arrowReturn) Expect("->");
            Expect("int");
            bool braced = Take("{");
            bool explicitSeparator = false;
            if (!braced)
            {
                if (!arrowReturn)
                    throw new FormatException($"End-delimited main requires '-> int' at offset {Current.Offset}");
                explicitSeparator = Take("::");
                if (!explicitSeparator)
                {
                    var previous = tokens[_index - 1];
                    int boundaryStart = previous.Offset + previous.Text.Length;
                    bool hasLineBreak = false;
                    for (int i = boundaryStart; i < Current.Offset; i++)
                    {
                        if (source[i] is '\n' or '\r' or '\u0085' or '\u2028' or '\u2029')
                        {
                            hasLineBreak = true;
                            break;
                        }
                    }
                    if (!hasLineBreak && !languageVersion.HasValue)
                        throw new FormatException($"Compact end-delimited main requires '::' at offset {Current.Offset}");
                }
            }
            // Explicit v1/v2 mode ONLY; legacy callers keep their current
            // unversioned admission and diagnostics. The restricted i64
            // executable slice accepts pure comma expressions, not arbitrary
            // effectful steps, locals, actor boundaries or implicit casts.
            if (!braced && languageVersion is 1 or 2 &&
                (explicitSeparator || languageVersion == 1))
            {
                if (Current.Text == "return" || Current.Text == "^")
                    throw new FormatException("E_SYNTAX: comma body uses expression-tail syntax");
                Expression last = ParseAdd(0);
                int steps = 0;
                while (Take(","))
                {
                    if (++steps > 1024)
                        throw new FormatException("E_SOURCE_LIMIT: comma body exceeds 1024 items");
                    if (!VersionedPureScalar(last))
                        throw new FormatException("E_UNSUPPORTED: discarded comma-body value requires effect-safe lowering");
                    if (Current.Text is "," or "end" or ";" or "")
                        throw new FormatException("E_SYNTAX: empty comma-body item");
                    last = ParseAdd(0);
                }
                if (Current.Text == ";")
                    throw new FormatException("E_SYNTAX: comma body requires ',' not ';'");
                Expect("end");
                if (Current.Text.Length != 0)
                    throw new FormatException($"Unexpected token at offset {Current.Offset}");
                return last;
            }
            if (!braced && languageVersion == 2 && Current.Text is not ("return" or "^"))
                throw new FormatException("E_LANGUAGE_VERSION: v2 unmarked body requires explicit return; comma tails require '::'");
            if (Current.Text == "guard")
                throw new FormatException(
                    $"GUARD_BACKEND_UNSUPPORTED: early-exit CFG and ownership lowering required at offset {Current.Offset}");
            if (!(languageVersion.HasValue && Take("^")))
                Expect("return");
            var expression = ParseAdd(0);
            if (!braced && languageVersion == 2 && Current.Text == ",")
                throw new FormatException("E_LANGUAGE_VERSION: v2 comma body requires '::'");
            Expect(";");
            Expect(braced ? "}" : "end");
            if (Current.Text.Length != 0)
                throw new FormatException($"Unexpected token at offset {Current.Offset}");
            return expression;
        }

        private static bool VersionedPureScalar(Expression expression) => expression switch
        {
            Literal => true,
            Unary unary => VersionedPureScalar(unary.Value),
            Binary binary => VersionedPureScalar(binary.Left) && VersionedPureScalar(binary.Right),
            _ => false
        };

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
            int casts = 0;
            while (Take("as"))
            {
                CheckDepth(depth + ++casts);
                left = ApplyScalarCastOrUnit(left, ReadQuantityName(), unitOnly: false, legacyUnit: true);
            }
            if (Current.Text is "|>" or "<|")
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
                // A quantity slash has an exact signed-i64 profile. Plain
                // int/int keeps the existing source rule.
                if (op == '/' && _units.Count == 0)
                    throw new FormatException(
                        "Division requires float result lowering; unsupported in the i64 source subset");
                _index++;
                var rhs = ParseUnary(depth + 1);
                left = op == '/' ? new QuantityDivision(left, rhs) : new Binary(op, left, rhs);
            }
            return left;
        }

        private Expression ApplyScalarCastOrUnit(Expression value, string target, bool unitOnly, bool legacyUnit)
        {
            if (unitOnly || (legacyUnit && _units.ContainsKey(target)))
            {
                if (!_units.TryGetValue(target, out var unit) || target is "int" or "i64" or "i32")
                    throw new FormatException("E_UNIT_NAME: unknown or ambiguous unit");
                // Literal attachment can use the existing exact literal conversion path.
                if (value is Literal number) return new QuantityLiteral(number.Value, unit);
                return new QuantityAttachment(value, unit);
            }
            if (target is not ("int" or "i64"))
                throw new FormatException("E_UNSUPPORTED_CAST: only signed-i64 identity casts are admitted in the scalar profile");
            return new QuantityScalarCast(value);
        }

        private Expression ParseUnary(int depth)
        {
            var value = ParseUnaryBase(depth);
            int links = 0;
            while ((Current.Text == "::" || Current.Text == ".") &&
                   _index + 1 < tokens.Count && tokens[_index + 1].Text == "<")
            {
                CheckDepth(depth + ++links);
                bool unitOnly = Take(".");
                if (!unitOnly) Expect("::");
                Expect("<");
                string target = ReadQuantityName();
                Expect(">");
                value = ApplyScalarCastOrUnit(value, target, unitOnly, legacyUnit: true);
            }
            if (value is QuantityLiteral quantity && Current.Text == "." &&
                    _index + 1 < tokens.Count && tokens[_index + 1].Text == "to_unit")
            {
                Expect("."); Expect("to_unit"); Expect("<");
                var target = ReadUnit();
                Expect(">"); Expect("("); Expect(")");
                value = new QuantityLiteral(quantity.Unit.ConvertExact(quantity.Value, target), target);
            }
            return value;
        }

        private Expression ParseUnaryBase(int depth)
        {
            CheckDepth(depth);
            if (Take("<"))
            {
                string target = ReadQuantityName();
                Expect(">");
                return ApplyScalarCastOrUnit(ParseUnary(depth + 1), target, unitOnly: false, legacyUnit: false);
            }
            if (Current.Text.Length > 0 && Current.Text[0] is '\'' or '"' or '\u0060')
                throw new FormatException(
                    $"STRING_BACKEND_UNSUPPORTED: quoted strings and template interpolation require typed String CIL/NativeAOT lowering at offset {Current.Offset}");
            if (Current.Text == "rt" &&
                !(_index + 1 < tokens.Count && tokens[_index + 1].Text == "!"))
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
                    ownership = "mut borrow"; // compatible legacy order, same unsupported typed gate
                if (ownership is "copy" or "share" or "take" or "borrow" or "mut borrow")
                    throw new FormatException(
                        $"Ownership operation 'rt {ownership}' requires typed actor-arena lowering; unsupported in the .NET scalar backend at offset {at}");

                throw new FormatException(
                    $"Runtime ownership operation requires typed lowering; unsupported in the .NET scalar backend at offset {at}");
            }
            // Recognize the new contextual ownership prefix before macro
            // expansion, but fail closed: this scalar subset has no typed
            // ownership/actor-arena Native AOT lowering. Only a verb touching
            // '!' is an ownership operator; 'copy !foo' is not.
            int verbIndex = Current.Text == "mut" ? _index + 1 : _index;
            if (verbIndex + 1 < tokens.Count &&
                (Current.Text != "mut" ||
                    (tokens[verbIndex].Text == "borrow")) &&
                (tokens[verbIndex].Text is "copy" or "take" or "borrow" or "share") &&
                tokens[verbIndex + 1].Text == "!" &&
                (verbIndex + 2 >= tokens.Count ||
                    tokens[verbIndex + 2].Text is not ("=" or "eq")) &&
                tokens[verbIndex + 1].Offset ==
                    tokens[verbIndex].Offset + tokens[verbIndex].Text.Length)
                throw new FormatException(
                    $"OWNERSHIP_BANG_BACKEND_UNSUPPORTED: '{(Current.Text == "mut" ? "mut " : "")}{tokens[verbIndex].Text}!' requires typed ownership and actor-arena Native AOT lowering at offset {Current.Offset}");

            // Every token followed by ! is a macro invocation candidate.
            // IsMacroName intentionally rejects reserved identifiers, but using
            // it to *detect* calls would route quote!/eval!/rt!/main! to the
            // ordinary scalar-expression error instead of the fail-closed
            // macro diagnostic. The declared-name check still happens below.
            if (Current.Text.Length != 0 && _index + 1 < tokens.Count &&
                tokens[_index + 1].Text == "!")
            {
                int at = Current.Offset;
                string name = Current.Text;
                if (!_macros.TryGetValue(name, out ScalarMacro? macro))
                    throw UnsupportedMacro(at, $"unresolved macro '{name}'");
                if (++_macroExpansions > MaxMacroExpansions)
                    throw UnsupportedMacro(at, "macro expansion count exceeds 128");
                _index += 2;
                Expect("(");
                Expression argument;
                if (macro.Kind == MacroKind.ConstIntQuote)
                {
                    if (!Take("const"))
                        throw UnsupportedMacro(at, "Const<int> macro requires a const argument");
                    argument = ParseAdd(depth + 1);
                    Expect(")");
                    // Materialize a new checked literal in the compiler AST.
                    // No token-string rewriting, reflection or JIT execution.
                    return new Literal(EvaluateMacroConst(argument, at));
                }
                if (!Take("syntax") || !Take("expr"))
                    throw UnsupportedMacro(at, "Syntax<Expr> macro requires syntax expr");
                if (macro.Kind == MacroKind.SyntaxExprLiteralTwice &&
                    IsMacroName(Current.Text) &&
                    (_index + 1 >= tokens.Count || tokens[_index + 1].Text != "!"))
                    throw UnsupportedMacro(at, "repeated unquote requires a closed integer literal; cannot duplicate a name or call before ownership analysis");
                argument = ParseAdd(depth + 1);
                Expect(")");
                if (macro.Kind == MacroKind.SyntaxExprLiteralTwice)
                {
                    // This pre-typecheck expander cannot prove name copyability
                    // or the absence of effects/ownership transfers. Only a
                    // closed integer literal is safe to repeat. Both AST
                    // occurrences MUST have fresh identity (no DAG alias).
                    if (argument is not Literal number)
                        throw UnsupportedMacro(at, "repeated unquote requires a closed integer literal; cannot duplicate effects or ownership");
                    try
                    {
                        _ = checked(number.Value + number.Value);
                    }
                    catch (OverflowException ex)
                    {
                        throw new FormatException(
                            $"NET_MACRO_CONST_OVERFLOW at offset {at}; runtime fallback is forbidden", ex);
                    }
                    return new Binary('+', new Literal(number.Value), new Literal(number.Value));
                }
                // Single call-site AST splice: no generated identifier, and
                // no copied/replayed ownership or effectful expression.
                return argument;
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
            // A first native, escaping-free product construction slice:
            // Tuple.of(<i64 literals...>)[constant_slot] only; ~ is structural shorthand.
            // All operands are pure checked constants, so projecting the
            // selected SSA scalar preserves the exact source evaluation
            // behavior without allocating a host tuple or losing an effect.
            if (Current.Text is "Tuple" or "Record" or "Entry" or "Pair" or "~")
            {
                int at = Current.Offset;
                if (Take("~"))
                    throw new FormatException(
                        $"E_COLLECTION_FACTORY_REMOVED: '~' denotes structural typing, not a collection constructor; use Type.of(...) at offset {at}");
                if (Take("Tuple"))
                {
                    Expect(".");
                    Expect("of");
                    return ParseScalarizedTupleFactory(depth + 1, at);
                }
                if (Take("Record"))
                {
                    Expect(".");
                    Expect("of");
                    return ParseScalarizedRecordFactory(depth + 1, at);
                }
                bool isEntry = Take("Entry");
                if (!isEntry) Expect("Pair");
                Expect(".");
                Expect("of");
                return ParseScalarizedBinaryProductFactory(depth + 1, at, isEntry);
            }
            if (Current.Text is "Map" or "Array" or "List" or "Vector" or "Set")
                throw new FormatException(
                    $"E_COLLECTION_FACTORY_UNSUPPORTED: {Current.Text}.of requires owned typed aggregate and NativeAOT CIL lowering at offset {Current.Offset}");
            // Closed named Integer intrinsics with two eager signed-i64 operands.
            // No generic method invocation or host interoperability is admitted.
            if (Take("Integer"))
            {
                Expect(".");
                string operation = Current.Text;
                if (operation is not ("bitXor" or "rem" or "mod" or "pow"))
                    throw new FormatException(
                        $"Unsupported Integer intrinsic at offset {Current.Offset}");
                _index++;
                Expect("(");
                var left = ParseAdd(depth + 1);
                Expect(",");
                var right = ParseAdd(depth + 1);
                Expect(")");
                return operation switch
                {
                    "bitXor" => new NamedBitXor(left, right),
                    "rem" => new NamedIntegerRem(left, right),
                    "mod" => new NamedIntegerMod(left, right),
                    _ => new NamedIntegerPow(left, right)
                };
            }
            if (Take("+")) return new Unary('+', ParseUnary(depth + 1));
            if (Take("-"))
            {
                // Signed minimum needs special treatment: its magnitude is > long.MaxValue.
                if (Current.Text.Replace("_", "", StringComparison.Ordinal).TrimStart('0') == "9223372036854775808")
                {
                    Token magnitude = Current;
                    _index++;
                    return ParseNumericSuffix(magnitude, long.MinValue);
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
            Token numericToken = Current;
            _index++;
            if (!long.TryParse(numericToken.Text.Replace("_", "", StringComparison.Ordinal),
                NumberStyles.None, CultureInfo.InvariantCulture, out long result))
                throw new FormatException($"Invalid i64 at offset {numericToken.Offset}");
            return ParseNumericSuffix(numericToken, result);
        }

        // A nominal Entry<K,V> and a Pair<A,B> remain different source
        // families: an Entry exposes only .key/.value; a Pair exposes only
        // .first/.second. The scalar bootstrap can represent neither as an
        // escaping object. A checked pure literal immediate projection is
        // valid without silently allocating a host tuple/dictionary or
        // introducing unproven ownership/actor-heap transport.
        private Expression ParseScalarizedBinaryProductFactory(int depth, int at, bool isEntry)
        {
            CheckDepth(depth);
            if (Take("<"))
            {
                Expect("int");
                Expect(",");
                Expect("int");
                Expect(">");
            }
            Expect("(");
            long first = ReadPureI64Literal();
            Expect(",");
            long second = ReadPureI64Literal();
            Expect(")");
            if (!Take("."))
                throw new FormatException(
                    $"E_BINARY_PRODUCT_ESCAPE_UNSUPPORTED: Entry/Pair requires an owned nominal aggregate at offset {at}");
            string field = Current.Text;
            int fieldAt = Current.Offset;
            _index++;
            if (isEntry)
            {
                if (field == "key") return new Literal(first);
                if (field == "value") return new Literal(second);
                throw new FormatException($"E_ENTRY_FIELD: only key/value projections are supported at offset {fieldAt}");
            }
            if (field == "first") return new Literal(first);
            if (field == "second") return new Literal(second);
            throw new FormatException($"E_PAIR_FIELD: only first/second projections are supported at offset {fieldAt}");
        }

        // This is not an escaped Tuple<T...> value: the scalar bootstrap
        // emits an i64 result, not an owning container. Require an immediate
        // constant-index projection, bounded arity, and pure literal operands.
        // If any operand may execute guest code or overflow, refuse the
        // scalarization instead of silently dropping its evaluation.
        private Expression ParseScalarizedTupleFactory(int depth, int at)
        {
            CheckDepth(depth);
            int? explicitArity = null;
            if (Take("<"))
            {
                int count = 0;
                do
                {
                    Expect("int");
                    if (++count > 32)
                        throw new FormatException($"E_TUPLE_BUDGET: over 32 tuple type slots at offset {at}");
                }
                while (Take(","));
                Expect(">");
                explicitArity = count;
            }

            Expect("(");
            var values = new List<long>();
            if (Current.Text != ")")
            {
                do
                {
                    if (values.Count >= 32)
                        throw new FormatException($"E_TUPLE_BUDGET: over 32 elements at offset {at}");
                    int operandAt = Current.Offset;
                    string sign = "";
                    if (Current.Text is "+" or "-")
                    {
                        sign = Current.Text;
                        _index++;
                    }
                    string raw = Current.Text;
                    if (raw.Length == 0 || raw[0] is < '0' or > '9')
                        throw new FormatException(
                            $"E_TUPLE_LITERAL_ONLY: scalarized tuple requires side-effect-free i64 literal operands at offset {operandAt}");
                    _index++;
                    if (!long.TryParse(sign + raw.Replace("_", "", StringComparison.Ordinal),
                            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n))
                        throw new FormatException($"E_TUPLE_RANGE: invalid signed i64 at offset {operandAt}");
                    values.Add(n);
                }
                while (Take(",") && Current.Text != ")");
            }
            Expect(")");
            if (explicitArity.HasValue && explicitArity.Value != values.Count)
                throw new FormatException($"E_TUPLE_ARITY: tuple witness/operand count mismatch at offset {at}");

            if (!Take("["))
                throw new FormatException(
                    $"E_TUPLE_ESCAPE_UNSUPPORTED: tuple value requires native product storage; only immediate constant-index projection is admitted at offset {at}");
            string indexText = Current.Text;
            int indexAt = Current.Offset;
            if (indexText.Length == 0 || indexText[0] is < '0' or > '9' ||
                !int.TryParse(indexText.Replace("_", "", StringComparison.Ordinal),
                    NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                throw new FormatException($"E_TUPLE_INDEX: tuple projection needs nonnegative constant index at offset {indexAt}");
            _index++;
            Expect("]");
            if (index >= values.Count)
                throw new FormatException($"E_TUPLE_BOUNDS: tuple index out of range at offset {indexAt}");
            return new Literal(values[index]);
        }

        // Readonly record scalarization is legal only for an immediate static
        // field projection of a closed, all-literal shape. No record reference
        // enters the scalar SSA, so the projection cannot escape or alias a
        // host object. All field values are pure checked i64 literals.
        private Expression ParseScalarizedRecordFactory(int depth, int at)
        {
            CheckDepth(depth);
            Expect("(");
            var fields = new Dictionary<string, long>(StringComparer.Ordinal);
            if (Current.Text != ")")
            {
                do
                {
                    if (fields.Count >= 32)
                        throw new FormatException($"E_RECORD_BUDGET: over 32 fields at offset {at}");
                    string field = Current.Text;
                    int fieldAt = Current.Offset;
                    if (field.Length == 0 ||
                        !(field[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') ||
                        field.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                            or >= '0' and <= '9' or '_')))
                        throw new FormatException(
                            $"E_RECORD_STATIC_FIELD: record field must be static ASCII identifier at offset {fieldAt}");
                    _index++;
                    Expect(":");
                    long value = ReadPureI64Literal();
                    if (!fields.TryAdd(field, value))
                        throw new FormatException($"E_RECORD_DUPLICATE: duplicate field at offset {fieldAt}");
                }
                while (Take(",") && Current.Text != ")");
            }
            Expect(")");
            if (!Take("."))
                throw new FormatException(
                    $"E_RECORD_ESCAPE_UNSUPPORTED: record requires typed storage; only immediate field projection is admitted at offset {at}");
            int nameAt = Current.Offset;
            string member = Current.Text;
            _index++;
            if (!fields.TryGetValue(member, out long projected))
                throw new FormatException($"E_RECORD_FIELD: unknown or inaccessible record field at offset {nameAt}");
            return new Literal(projected);
        }

        private long ReadPureI64Literal()
        {
            int at = Current.Offset;
            string sign = "";
            if (Current.Text is "+" or "-")
            {
                sign = Current.Text;
                _index++;
            }
            string raw = Current.Text;
            if (raw.Length == 0 || raw[0] is < '0' or > '9')
                throw new FormatException(
                    $"E_AGGREGATE_LITERAL_ONLY: scalarized fields require pure i64 literals at offset {at}");
            _index++;
            if (!long.TryParse(sign + raw.Replace("_", "", StringComparison.Ordinal),
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                throw new FormatException($"E_AGGREGATE_I64_RANGE: invalid signed i64 at offset {at}");
            return value;
        }

        private static void CheckDepth(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("Expression nesting too deep");
        }
    }
}
