using System.Globalization;

namespace Ores.Net.Compiler;

// Non-executable source AST. The production arithmetic parser and emitter stay
// unchanged until structured CFG lowering has an independent verification gate.
public abstract record StructuredExpr(int Offset, IrValueType Type);
public sealed record StructuredInt(int At, long Value) : StructuredExpr(At, IrValueType.I64);
public sealed record StructuredBool(int At, bool Value) : StructuredExpr(At, IrValueType.Bool);
// A closed, allocation-free Option<int> source type. Lowering expands it into
// two independently verified SSA primitives (tag + payload), never a sentinel.
// No Option<T> ABI, nested Option, type erasure or implicit unwrap is authorized.
public sealed record StructuredSome(int At, StructuredExpr Payload)
    : StructuredExpr(At, IrValueType.OptionI64);
public sealed record StructuredNone(int At) : StructuredExpr(At, IrValueType.OptionI64);
// A bounded fixed-size array is lowered as immutable scalar SSA components.
// Neither a CLR array nor actor-heap allocation is exposed to guest source.
public sealed record StructuredFixedArray(int At, IReadOnlyList<StructuredExpr> Elements)
    : StructuredExpr(At, IrValueType.FixedArrayI64);
public sealed record StructuredArrayGet(int At, StructuredExpr Array, StructuredExpr Index)
    : StructuredExpr(At, IrValueType.OptionI64);
public sealed record StructuredOptionPresent(int At, StructuredExpr Option)
    : StructuredExpr(At, IrValueType.Bool);
public sealed record StructuredOptionAbsent(int At, StructuredExpr Option)
    : StructuredExpr(At, IrValueType.Bool);
// Closed tagged equality: compare declared Option<int> tags before active payloads.
// Neither a None payload nor an untyped scalar may influence the result.
public sealed record StructuredOptionEqual(int At, StructuredExpr LeftOption,
    StructuredExpr RightOption, bool Negated)
    : StructuredExpr(At, IrValueType.Bool);
public sealed record StructuredOptionOr(int At, StructuredExpr Option, StructuredExpr Fallback)
    : StructuredExpr(At, IrValueType.I64);
public sealed record StructuredRead(int At, string Name, IrValueType ValueType)
    : StructuredExpr(At, ValueType);
public sealed record StructuredNegate(int At, StructuredExpr Value)
    : StructuredExpr(At, IrValueType.I64);
public sealed record StructuredConditional(int At, StructuredExpr Test,
    StructuredExpr WhenTrue, StructuredExpr WhenFalse, IrValueType ResultType)
    : StructuredExpr(At, ResultType);
// First typed call milestone: statically resolved, expression-only helpers.
// Calls are expanded into verified SSA before emitting any CIL.
public sealed record StructuredCall(int At, string Name,
    IReadOnlyList<StructuredExpr> Arguments, IrValueType ResultType)
    : StructuredExpr(At, ResultType);
public sealed record StructuredParameter(string Name, IrValueType Type);
public sealed record StructuredFunction(string Name,
    IReadOnlyList<StructuredParameter> Parameters, IrValueType ResultType,
    StructuredExpr? ReturnValue, IReadOnlyList<StructuredStmt> Body);
public enum StructuredOperator { Add, Subtract, Multiply, EqualI64, LessThanI64, GreaterThanI64, NotEqualI64,
    LessThanOrEqualI64, GreaterThanOrEqualI64,
    EqualBool, NotEqualBool, LogicalAnd, LogicalOr, LogicalXor,
    // atto-spec bitwise-and-stream-operators-v1: <<<, >>> and infix ashr.
    ShiftLeft, ShiftRightLogical, ShiftRightArithmetic,
    Divide, EqualGuestError, NotEqualGuestError, EqualGuestText, NotEqualGuestText, Remainder, Modulo, BitXor }
public sealed record StructuredBinary(int At, StructuredOperator Operator,
    StructuredExpr Left, StructuredExpr Right, IrValueType ResultType)
    : StructuredExpr(At, ResultType);
public abstract record StructuredStmt(int Offset);
// Compile-time const, single-assignment val, and mutable let are distinct
// source bindings; the experimental structured AST preserves that distinction.
public enum StructuredBindingKind { Let, Val, Const, Readonly }
public enum StructuredLocalTypeKind { Struct, Record }
public sealed record StructuredLocalField(string Name, IrValueType Type);
public sealed record StructuredLocalTypeDecl(int At, string Name,
    StructuredLocalTypeKind Kind, IReadOnlyList<StructuredLocalField> Fields)
    : StructuredStmt(At);
public sealed record StructuredLet(int At, string Name, IrValueType ValueType,
    StructuredExpr Initializer, StructuredBindingKind Kind = StructuredBindingKind.Let)
    : StructuredStmt(At);
// Immutable runtime-valued aggregate field snapshots. All field expressions
// execute in lexical order, even when later spread/explicit fields override.
// Each private SSA place is opaque to source syntax and never escapes a row.
public sealed record StructuredAggregateScalarWrite(string Place,
    IrValueType Type, StructuredExpr Value);
public sealed record StructuredAggregateRuntimeLet(int At,
    IReadOnlyList<StructuredAggregateScalarWrite> Writes)
    : StructuredStmt(At);
public sealed record StructuredSet(int At, string Name, StructuredExpr Value)
    : StructuredStmt(At);
public sealed record StructuredReturn(int At, StructuredExpr Value) : StructuredStmt(At);
// First typed guest-error slice. The catch binding is intentionally opaque;
// only the source-level nominal is-type test is represented. CLR exception
// identity remains an internal CIL implementation detail.
public sealed record StructuredOverflowCatchReturn(
    int At, StructuredExpr Protected, string? TypeTest,
    long MatchValue, long FallbackValue) : StructuredStmt(At);
// Explicit return; and compiler-inserted void fallthrough share a terminator.
public sealed record StructuredReturnVoid(int At) : StructuredStmt(At);
public sealed record StructuredIf(int At, StructuredExpr Condition,
    IReadOnlyList<StructuredStmt> Then, IReadOnlyList<StructuredStmt>? Else)
    : StructuredStmt(At);
public sealed record StructuredWhile(int At, StructuredExpr Condition,
    IReadOnlyList<StructuredStmt> Body) : StructuredStmt(At);
// Lexical loop transfers share the same typed CFG/SSA environment.
public sealed record StructuredBreak(int At) : StructuredStmt(At);
public sealed record StructuredContinue(int At) : StructuredStmt(At);
public sealed record StructuredProgram(string Name, IReadOnlyList<StructuredStmt> Body,
    IReadOnlyDictionary<string, StructuredFunction>? Functions = null,
    IReadOnlyList<string>? FunctionOrder = null);

/// <summary>
/// Narrow checked parser for a single int-returning main, initialized locals,
/// assignments, if/else, while and returns. No CIL or runtime execution.
/// </summary>
public static partial class OresStructuredParser
{
    private const int MaxChars = 1_048_576;
    private const int MaxTokens = 100_000;
    private const int MaxStatements = 16_384;
    private const int MaxDepth = 128;
    private const int MaxIdentifier = 128;
    private const int MaxHelpers = 64;
    private const int MaxParameters = 16;
    private const int MaxLocalTypeFields = 64;

    // Structured CFG/CIL has no typed macro splice semantics yet. Its source
    // and import discovery must fail closed rather than dropping definitions
    // or mistaking name!(...) for a normal function call.
    private static void RejectUnsupportedMacros(IReadOnlyList<Token> tokens)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "macro" && i > 0 &&
                (tokens[i - 1].Text is "define" or "pub"))
                throw new FormatException(
                    $"NET_STRUCTURED_MACRO_EXPANDER_REQUIRED: macro declarations require typed AST expansion before OresIR/CIL at offset {tokens[i].Offset}; runtime fallback is forbidden");
            if (tokens[i].Text == "!" && i + 1 < tokens.Count &&
                tokens[i + 1].Text == "(" && !IsPrefixBangPosition(tokens, i))
                throw new FormatException(
                    $"NET_STRUCTURED_MACRO_EXPANDER_REQUIRED: macro invocation requires typed AST expansion before OresIR/CIL at offset {tokens[i].Offset}; runtime fallback is forbidden");
        }
    }

    // A parenthesized boolean operand is not a macro splice. Permit only
    // expression-prefix positions whose previous token cannot be a callee.
    // Identifier/number/member spellings followed by ! (even with trivia)
    // retain the original fail-closed macro diagnostic.
    private static bool IsPrefixBangPosition(IReadOnlyList<Token> tokens, int at)
    {
        if (at == 0) return true;
        var before = tokens[at - 1];
        if (before.Text is "(" or "=" or "," or ":" or "?" or "{" or ";" or
            "^" or "->" or "&&" or "||" or "^^" or "==" or "!=" or "!eq" or
            "<" or ">" or "<=" or ">=" or "!" or "+" or "-" or "*" or "/")
            return true;
        // Do not treat contiguous reserved-name macro-like forms such as
        // return!(...) as keyword followed by a prefix expression.
        return before.Offset + before.Text.Length < tokens[at].Offset &&
            before.Text is ("return" or "guard" or "when" or "if" or "ifnot" or
                "unless" or "then" or "else" or "and" or "or" or "xor" or "eq" or "neq");
    }

    // `...source` directly inside `infer struct|record { }` or `= record { }` is the
    // closed aggregate spread, lowered as single-evaluation scalar row writes.
    // Every other spread keeps the tuple-v2 fail-closed diagnostic.
    private static bool InClosedAggregateInitializer(IReadOnlyList<Token> tokens, int at)
    {
        int depth = 0;
        for (int j = at - 1; j >= 0; j--)
        {
            string t = tokens[j].Text;
            if (t == "}") depth++;
            else if (t == ";" && depth == 0) return false;
            else if (t == "{")
            {
                if (depth > 0) { depth--; continue; }
                return j >= 2 && tokens[j - 1].Text is "struct" or "record" &&
                       tokens[j - 2].Text is "infer" or "=";
            }
        }
        return false;
    }

    private static void RejectUnsupportedMutableSharing(IReadOnlyList<Token> tokens)
    {
        for (int i = 0; i + 2 < tokens.Count; i++)
        {
            if (tokens[i].Text == "rt" && tokens[i + 1].Text == "mut" &&
                tokens[i + 2].Text == "share")
                throw MutableShareAdmission.Unsupported(tokens[i].Offset);
        }
    }

    public static StructuredProgram Parse(string source)
        => ParseCore(source, null).Program;

    // Explicit conditional-only strict source profile. Ordinary production and
    // migration entry points retain their existing admitted syntax.
    public static StructuredProgram ParseStrictConditionals(string source)
        => ParseCore(source, null, strictConditionals: true).Program;

    internal static StructuredProgram ParseProduction(string source, bool allowDeferredStdout = false)
        => ParseCore(source, null, productionGuard: true, allowDeferredStdout: allowDeferredStdout).Program;

    internal static bool HasGuardToken(string source)
        => HasStructuredFeatureToken(source, guardOnly: true);

    internal static bool HasStructuredFeatureToken(string source, bool guardOnly = false)
    {
        if (guardOnly && !source.Contains("guard", StringComparison.Ordinal)) return false;
        var lexer = new Lexer(source, moduleMode: false);
        try
        {
            string previous = "", beforePrevious = "";
            while (lexer.Read() is { Text.Length: > 0 } token)
            {
                if (token.Text == "guard") return true;
                if (guardOnly) continue;
                if (token.Text is "/" or "Option" or "Some" or "None" or "Result" or "trap" or "trap!" or "throw" or "throw!" or
                    "panic" or "panic!" or "raise" or "raise!" or "match" or "if" or "ifnot" or "unless" ||
                    (previous == "sync" && token.Text == "task") ||
                    (beforePrevious == "fnc" && token.Text == "<") ||
                    (beforePrevious == "Integer" && previous == "." && token.Text is "rem" or "mod"))
                    return true;
                beforePrevious = previous;
                previous = token.Text;
            }
        }
        catch (FormatException)
        {
            // An input outside the CFG lexer before any matching token belongs
            // to the ordinary frontend and its existing diagnostics.
        }
        return false;
    }

    internal static StructuredLinkedUnit ParseLinked(string source, LinkedModuleContext linking)
        => ParseCore(source, linking);

    internal static IReadOnlyList<SourceImport> ReadLinkedImports(string source,
        bool compilerOwnedCoreNamespace = false)
    {
        var reader = new Reader(LexLinked(source),
            allowCoreNamespace: compilerOwnedCoreNamespace);
        return reader.ReadImports();
    }

    // Import discovery must enforce the same byte, NUL and UTF-16 admission
    // as full parsing BEFORE it can resolve or probe another module.
    private static void ValidateSourceInput(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaxChars)
            throw new FormatException("Source exceeds 1 MiB parser limit");
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\0')
                throw new FormatException($"Source contains NUL at offset {i}");
            // Import discovery and structured parser must share the same raw-source rule.
            if (source[i] == (char)0x001b || source[i] == (char)0x007f)
                throw new FormatException($"Source contains terminal control at offset {i}");
            if (source[i] is '\u061c' or '\u200e' or '\u200f' or
                >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')
                throw new FormatException($"Source contains bidirectional Unicode control at offset {i}");
            if (char.IsHighSurrogate(source[i]))
            {
                if (++i >= source.Length || !char.IsLowSurrogate(source[i]))
                    throw new FormatException($"Invalid UTF-16 at offset {i - 1}");
            }
            else if (char.IsLowSurrogate(source[i]))
                throw new FormatException($"Invalid UTF-16 at offset {i}");
        }
        // Public parser callers supply UTF-16 strings; the source budget is
        // UTF-8 bytes, matching the disk/CLI boundary, not code-unit count.
        if (System.Text.Encoding.UTF8.GetByteCount(source) > 1_048_576)
            throw new FormatException("Source exceeds 1 MiB UTF-8 parser limit");
    }

    private static List<Token> LexLinked(string source)
    {
        ValidateSourceInput(source);
        var lexer = new Lexer(source, true);
        var tokens = new List<Token>();
        do
        {
            tokens.Add(lexer.Read());
            if (tokens.Count > MaxTokens) throw new FormatException("Structured token budget exceeded");
        } while (tokens[^1].Text.Length != 0);
        RejectActorPipe(tokens); // before any module graph/import resolution
        RejectUnsupportedMutableSharing(tokens);
        RejectUnsupportedMacros(tokens);
        return tokens;
    }

    private static void RejectActorPipe(List<Token> tokens)
    {
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (ActorPipeAdmission.IsReservedForm(
                i > 2 ? tokens[i - 3].Text : "",
                i > 1 ? tokens[i - 2].Text : "",
                i > 0 ? tokens[i - 1].Text : "",
                tokens[i].Text, tokens[i + 1].Text))
                throw ActorPipeAdmission.Unsupported(tokens[i].Offset, tokens[i].Text);
        }
    }

    internal static IReadOnlyList<StaticImportGraph.Export> ParseFrozenExports(FrozenImportSnapshot snapshot, string expectedNamespace)
    {
        var inventory = new List<StaticImportGraph.Export>();
        _ = ParseCore(snapshot.ProviderSource(expectedNamespace), null, expectedNamespace, inventory);
        return inventory.AsReadOnly();
    }

    // Reuse the exact structured parser over the original immutable bytes.
    // A checked provider is not yet an executable module until CFG verification.
    internal static StructuredProgram ParseFrozenProvider(FrozenImportSnapshot snapshot, string expectedNamespace, string methodPrefix = "")
        => ParseCore(snapshot.ProviderSource(expectedNamespace), null, expectedNamespace,
            new List<StaticImportGraph.Export>(), methodPrefix).Program;

    private static StructuredLinkedUnit ParseCore(string source, LinkedModuleContext? linking,
        string? frozenNamespace = null, List<StaticImportGraph.Export>? frozenExports = null,
        string frozenMethodPrefix = "", Func<string,string,StructuredFunction>? frozenResolve = null,
        bool productionGuard = false,
        Func<string,string,Func<string,StructuredFunction>>? frozenNamespaceResolve = null,
        bool strictConditionals = false, bool allowDeferredStdout = false)
    {
        ValidateSourceInput(source);
        ActorPubSubSourceAdmission.RejectUnsupported(source);
        // No structural match lowering or readonly shape metadata in scalar CIL yet.
        StructuralShapeAdmission.RejectUnsupportedPatterns(source, allowCompileTimeRows: true);
        var lexer = new Lexer(source, linking is not null || frozenNamespace is not null,
            allowStaticTupleProjection: linking is null && frozenNamespace is null);
        var tokens = new List<Token>();
        // Distinguish a forbidden local nominal declaration from an
        // unsupported top-level class/actor before either reaches lowering.
        int braceDepth = 0;
        int firstNominalOffset = -1;
        while (true)
        {
            var token = lexer.Read();
            // Probe only direct top-level nominal struct declaration headers.
            // Expression-level with retains its original lexical gate.
            if (braceDepth == 0 && token.Text == "struct" && tokens.Count >= 3 &&
                tokens[^3].Text == "type" && IsSimpleNominalWord(tokens[^2].Text) &&
                tokens[^1].Text == "=")
                RejectAttachedStructTarget(source, token.Offset, tokens[^2].Text);
            if (braceDepth == 0 && token.Text == "{" && tokens.Count >= 3 &&
                tokens[^3].Text == "define" && tokens[^2].Text == "struct" &&
                IsSimpleNominalWord(tokens[^1].Text))
                RejectAttachedStructTarget(source, token.Offset, tokens[^1].Text, startAtBrace: true);
            // Comments and trivia are already stripped by this Lexer. Deny
            // namespace member access until ETS/pg isolation, ownership,
            // generational table handles and mailbox ingress exist in Native AOT.
            if (token.Text == "." && tokens.Count > 0 &&
                (tokens[^1].Text is "ets" or "pg"))
                throw new FormatException(
                    $"ETS/pg guest APIs require authenticated isolate, table and actor-mailbox lowering (unsupported .NET bootstrap) at offset {token.Offset}");
            // Durable M0 is metadata-only in the Java compiler. Never let
            // activity/workflow declarations silently reach scalar CIL lowering
            // before a typed history manifest, replay ABI and ownership gate exist.
            // Tokens are already comment/string-aware; do not regex raw source.
            if ((token.Text is "activity" or "workflow") && tokens.Count > 0 &&
                (tokens[^1].Text == "define" ||
                 (tokens[^1].Text == "pub" && tokens.Count > 1 &&
                  tokens[^2].Text == "define")))
                throw new FormatException(
                    $"DURABLE_M0_BACKEND_UNSUPPORTED: .NET durable {token.Text} requires versioned manifest and replay lowering at offset {token.Offset}");
            // The scalar bootstrap does not have class/actor dispatch or
            // ownership-aware lexical closures. Refuse these at source admission
            // with a stable capability diagnostic, not an accidental parse error.
            // Structured CFG, module linking and NativeAOT have no nominal
            // ModelGraph lowering. Reject before any source can reach SSA/CIL.
            // Match contextual declaration modifiers, never an ordinary
            // parameter or source comment containing the word "model".
            if (token.Text == "model" && tokens.Count > 0 &&
                (tokens[^1].Text == "define" ||
                 (tokens[^1].Text == "pub" && tokens.Count > 1 &&
                  tokens[^2].Text == "define")))
                throw new FormatException(
                    $"MODEL_LOWERING_UNSUPPORTED: .NET structured frontend requires typed nominal ModelGraph lowering at offset {token.Offset}");
            if (token.Text == "abstract" && tokens.Count > 0 &&
                (tokens[^1].Text == "define" ||
                 (tokens[^1].Text == "pub" && tokens.Count > 1 &&
                  tokens[^2].Text == "define")))
                throw new FormatException(
                    $"NET_PRIVATE_ABSTRACT_UNSUPPORTED: Private abstract class hooks require typed virtual dispatch and superclass-only invocation; inherited override authority and trusted actor-hook dispatch require typed lowering at offset {token.Offset}");
            if (token.Text == "inverted" && tokens.Count > 0 &&
                (tokens[^1].Text == "define" ||
                 (tokens[^1].Text == "pub" && tokens.Count > 1 &&
                  tokens[^2].Text == "define")))
            {
                // The structured scalar bootstrap cannot emit owner-backed
                // inverted rows. Look ahead with the actual lexer (which skips
                // comments) so an inverted STRUCT receives a stable diagnostic
                // instead of being misreported as an inverted CLASS.
                var invertedKind = lexer.Read();
                if (invertedKind.Text == "struct")
                    throw new FormatException(
                        $"Inverted struct requires checked value-row, one-level packed columns and ownership lowering in the .NET backend at offset {token.Offset}");
                // Only a class can receive the class-specific diagnostic.
                // A missing or unsupported kind is a malformed declaration,
                // not evidence that inverted class lowering was requested.
                if (invertedKind.Text != "class")
                    throw new FormatException(
                        $"Inverted declaration requires class or struct kind in the .NET backend at offset {invertedKind.Offset}");
                throw InvertedClassNativeAotAdmission.Unsupported(token.Offset);
            }
            // A supervisor is a distinct compiler-owned CONTROL authority,
            // not a class/actor or a scalar helper. Deny it before CFG/CIL
            // emission, including module-local and pub/define spellings.
            // Tokenization already excludes comments and string contents.
            if (token.Text == "supervisor" && tokens.Count > 0 &&
                (tokens[^1].Text == "define" ||
                 (tokens[^1].Text == "pub" && tokens.Count > 1 &&
                  tokens[^2].Text == "define")))
                throw new FormatException(
                    $"Source supervisors require typed Failure, owned CONTROL dispatch and Native AOT lowering; unsupported in .NET structured frontend at offset {token.Offset}");
            // Recognize actor declarations through all supported prefixes:
            // "define pub/shared/untrusted/serial http actor", "define isoactor",
            // and the legacy "shared actor". Until actor method slots, actor
            // inheritance and sealed state exist in CIL, do not allow a
            // modifier spelling to slip past the explicit capability gate.
            bool actorNominal = token.Text is "actor" or "isoactor";
            bool nominalStart = token.Text == "class" || actorNominal;
            int prefix = tokens.Count - 1;
            while (prefix >= 0 && tokens[prefix].Text is
                   ("pub" or "private" or "shared" or "untrusted" or
                    "serial" or "http" or "tcp" or "websocket" or
                    "unary" or "stream" or "abstract" or "transform" or "pipe"))
                prefix--;
            bool prefixedDefinition = prefix >= 0 && tokens[prefix].Text == "define";
            bool legacyActorPrefix = actorNominal &&
                (token.Text == "isoactor" ||
                 (tokens.Count > 0 && tokens[^1].Text is "shared" or "untrusted"));
            bool bareTopLevelActor = actorNominal && braceDepth == 0 &&
                (tokens.Count == 0 || tokens[^1].Text is "}" or "end" or ";" or "fnc" or "routine");
            if (nominalStart && (prefixedDefinition || legacyActorPrefix || bareTopLevelActor))
            {
                if (braceDepth > 0)
                    throw new FormatException(
                        $"Local class/actor declarations are not permitted at offset {token.Offset}");
                if (firstNominalOffset < 0) firstNominalOffset = token.Offset;
            }
            if (token.Text is "lex" or "nlex")
            {
                int after = token.Offset + token.Text.Length;
                while (after < source.Length && char.IsWhiteSpace(source[after])) after++;
                if (after < source.Length &&
                    (source[after] == '[' || source[after] == '|' ||
                     (token.Text == "nlex" && source[after] == '(')))
                    throw new FormatException(
                        $"Lexical captures require checked closure lowering at offset {token.Offset}");
            }
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
            if (token.Text == "namespace" && i > 0 && tokens[i - 1].Text == "use" &&
                frozenNamespace is null && !(linking is not null &&
                  linking.UnitIdentity.StartsWith("@atto.core/", StringComparison.Ordinal)))
                throw new FormatException(
                    $"NAMESPACE_RUNTIME_NATIVEAOT_UNSUPPORTED: file namespace requires authenticated compiler-owned Core source or verified package linker at offset {token.Offset}");
            // Reject as soon as the typed receiver header completes: later
            // body syntax may be outside this limited scalar lexer entirely.
            // Waiting for EOF could report an unrelated invalid character
            // before the missing NativeAOT method capability.
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
            // Canonical type-first mtd (Foo self) and (Foo mut self)
            // cannot enter this scalar-only NativeAOT lowering path.
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
            if (token.Text == "{") braceDepth++;
            else if (token.Text == "}" && braceDepth > 0) braceDepth--;
            if (tokens.Count > MaxTokens)
                throw new FormatException("Structured token budget exceeded");
            if (token.Text.Length == 0) break;
        }
        // An HTTP actor is not a scalar function or a generic mailbox actor.
        // The Kestrel NativeAOT *host* transport is not evidence that this
        // structured compiler can lower source HTTP actor dispatch or 3x4
        // request/message lifetimes. Detect actual lexer tokens, not comments.
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
                    $"HTTP_ACTOR_BACKEND_UNSUPPORTED: .NET source HTTP actor requires request/message lifetime admission, actor-state loans and physical shutdown lowering at offset {tokens[j].Offset}");
        }

        RejectActorPipe(tokens); // deny source graphs before CFG/CIL admission
        RejectUnsupportedMutableSharing(tokens);
        if (firstNominalOffset >= 0)
        {
            RejectDuplicateNominalMethodSlots(tokens);
            // Duplicate declarations are diagnosed above, but valid source
            // remains unsupported until typed owner/borrow and CIL lowering.
            throw new FormatException(
                $"NET_NOMINAL_METHODS_UNSUPPORTED: Nominal class/actor methods require receiver-erased method slots, type/borrow checking and Native AOT CIL lowering; unsupported in .NET structured frontend at offset {firstNominalOffset}");
        }
        // Keep the existing standalone scalar and linked admission boundaries.
        // The inline-task profile may declare suspension permission, but still
        // cannot execute a suspension. Recognize the token pair, not trivia.
        bool inlineProfile = linking is null && frozenNamespace is null && Enumerable.Range(0, tokens.Count - 1)
            .Any(i => tokens[i].Text == "sync" && tokens[i + 1].Text == "task");
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            // No protected state heap or virtual actor activation exists in
            // the structured Native AOT compiler. Recognize the two-word
            // declaration head after either visibility spelling, not merely
            // the following generic "expected fnc" parse error.
            if (tokens[i].Text == "define" ||
                (tokens[i].Text == "pub" && tokens[i + 1].Text == "define"))
            {
                int j = tokens[i].Text == "define" ? i + 1 : i + 2;
                if (j < tokens.Count && tokens[j].Text == "pub") j++;
                if (j + 1 < tokens.Count &&
                    tokens[j].Text == "state" && tokens[j + 1].Text == "container")
                    throw new FormatException(
                        $"STATE_CONTAINER_BACKEND_UNSUPPORTED: protected heap region and critical guard lowering required at offset {tokens[j].Offset}");
                if (j + 1 < tokens.Count &&
                    tokens[j].Text == "virtual" && tokens[j + 1].Text == "actor")
                    throw new FormatException(
                        $"VIRTUAL_ACTOR_BACKEND_UNSUPPORTED: logical key and activation fencing required at offset {tokens[j].Offset}");
            }
            // The separately exposed check-structured / compile-structured
            // pipeline must not bypass the shipping scalar concurrency gate.
            // This lexer has already removed comments, so no raw regex scan.
            string word = tokens[i].Text;
            string next = tokens[i + 1].Text;
            // Frozen package providers retain the prior closed scalar boundary.
            // Standalone task/guard/match support is not an imported ABI grant.
            if (frozenNamespace is not null && word is ("guard" or "match" or "suspends"))
                throw new FormatException($"E_UNSUPPORTED: frozen provider requires closed scalar lowering at offset {tokens[i].Offset}");
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
                    $"E_CRITICAL_UNSUPPORTED: structured CIL has no authenticated guard owner or FIFO continuation lowering at offset {tokens[i].Offset}");
            bool permittedMarker = inlineProfile && word == "suspends" &&
                next is ("fnc" or "routine");
            // The structured scalar parser can prove an immediate i64 await
            // outside inline tasks. Never relax frozen-provider or std.await
            // admission, and never infer a suspension from this lexical gate.
            bool immediateAwaitCandidate = word == "await" &&
                frozenNamespace is null && (i == 0 || tokens[i - 1].Text != ".") &&
                !IsInsideInlineTask(tokens, i);
            if ((word is "suspends" or "await") && !permittedMarker && !immediateAwaitCandidate)
            {
                string diagnostic = word == "await" && IsInsideInlineTask(tokens, i)
                    ? "E_SYNC_TASK_SUSPEND: this suspension lacks a same-thread continuation"
                    : "E_SUSPENDS_UNSUPPORTED: structured CIL lacks typed suspension/continuation lowering";
                throw new FormatException($"{diagnostic} at offset {tokens[i].Offset}");
            }
            if (tokens[i].Text == "select" &&
                StaticSelectAdmission.IsStaticSelectFollower(tokens[i + 1].Text))
                throw StaticSelectAdmission.Unsupported(tokens[i].Offset);
            // The structured/CIL CFG backend has no value-typed collection
            // IR yet. The separate scalar OresParser admits only immediate,
            // pure literal Tuple.of(...)[const] projections. Do not silently
            // reinterpret an owning aggregate as an i64 helper invocation.
            // Exception: fixed Array<int> literals have verified value-typed CFG/CIL
            // lowering (val-only, i64 elements, Option<int> checked get).
            if (i + 2 < tokens.Count && tokens[i + 1].Text == "." &&
                tokens[i + 2].Text == "of" &&
                tokens[i].Text is "Tuple" or "Map" or "List" or
                    "Vector" or "Set" or "Entry" or "Pair" or "Record")
                throw new FormatException(
                    $"E_COLLECTION_FACTORY_UNSUPPORTED: {tokens[i].Text}.of requires typed product/collection CFG and NativeAOT CIL lowering at offset {tokens[i].Offset}");
            if (tokens[i].Text == "~" && i + 1 < tokens.Count &&
                tokens[i + 1].Text is "tuple" or "map" or "array" or "list" or
                    "vector" or "set" or "entry" or "pair" or "record" or
                    "slice" or "fixed_array" or "fixed_list")
                throw new FormatException(
                    $"E_COLLECTION_FACTORY_REMOVED: ~{tokens[i + 1].Text} is removed; use Type.of(...), reserving ~ for structural typing at offset {tokens[i].Offset}");
            if ((tokens[i].Text is "tuple" or "structural") &&
                tokens[i + 1].Text == "(")
                throw new FormatException(
                    $"Tuple-v2 source requires typed tuple ownership/ABI lowering at offset {tokens[i].Offset}");
            if (i + 2 < tokens.Count && tokens[i].Text == "." &&
                tokens[i + 1].Text == "." && tokens[i + 2].Text == "." &&
                !InClosedAggregateInitializer(tokens, i))
                throw new FormatException(
                    $"Tuple-v2 explicit spread requires single-evaluation checked lowering at offset {tokens[i].Offset}");
        }
        RejectUnsupportedMacros(tokens);
        var reader = new Reader(tokens, linking, productionGuard: productionGuard,
            strictConditionals: strictConditionals, allowDeferredStdout: allowDeferredStdout);
        if (frozenNamespace is not null)
            return reader.ReadFrozenNamespace(frozenNamespace, frozenExports!, frozenMethodPrefix, frozenResolve, frozenNamespaceResolve);
        return linking is null
            ? new StructuredLinkedUnit(reader.ReadProgram(), null, new Dictionary<string, StructuredFunction>())
            : reader.ReadLinkedUnit();
    }

    // Inline tasks still reject await. Other immediate candidates must pass
    // the structured scalar parser's exact i64 type check.
    private static bool IsInsideInlineTask(IReadOnlyList<Token> tokens, int at)
    {
        int braces = 0;
        var taskBodies = new Stack<int>();
        int pendingBody = -1;
        for (int i = 0; i < at; i++)
        {
            if (tokens[i].Text == "sync" && i + 1 < at && tokens[i + 1].Text == "task")
            {
                int body = i + 2;
                if (body < at && tokens[body].Text == "<")
                {
                    int angles = 0;
                    do
                    {
                        if (tokens[body].Text == "<") angles++;
                        else if (tokens[body].Text == ">") angles--;
                        else if (tokens[body].Text is "{" or "}" or ";") break;
                        body++;
                    }
                    while (body < at && angles > 0);
                    if (angles != 0) continue;
                }
                if (body < at && tokens[body].Text == "{") pendingBody = body;
            }
            if (tokens[i].Text == "{")
            {
                braces++;
                if (i == pendingBody)
                {
                    taskBodies.Push(braces);
                    pendingBody = -1;
                }
            }
            else if (tokens[i].Text == "}")
            {
                if (taskBodies.Count != 0 && taskBodies.Peek() == braces)
                    taskBodies.Pop();
                braces--;
            }
        }
        return taskBodies.Count != 0;
    }

    // Syntax-only method-slot admission over tokens produced by the *same*
    // production lexer. This intentionally does not construct an executable
    // class AST or allow nominal source through to CIL. Ignore malformed or
    // richer declarations here: their ordinary capability gate stays closed.
    private static void RejectDuplicateNominalMethodSlots(IReadOnlyList<Token> tokens)
    {
        for (int i = 0; i + 3 < tokens.Count; i++)
        {
            if (tokens[i].Text != "define") continue;
            int head = i + 1;
            if (tokens[head].Text == "pub") head++;
            if (head + 3 >= tokens.Count ||
                tokens[head].Text is not ("class" or "actor") ||
                !IsSimpleNominalWord(tokens[head + 1].Text) ||
                tokens[head + 2].Text != "as") continue;
            string owner = tokens[head + 1].Text;
            var slots = new NominalReceiverMethodSlots();
            int member = head + 3;
            int examined = 0;
            while (member < tokens.Count && tokens[member].Text != "end")
            {
                if (++examined > MaxTokens)
                    throw new FormatException("Nominal method admission token budget exceeded");
                // A constructor or unsupported declaration may contain a
                // body. Never count calls inside that body as class methods.
                if (tokens[member].Text == "{")
                {
                    int braces = 1;
                    member++;
                    while (member < tokens.Count && braces > 0)
                    {
                        if (tokens[member].Text == "{") braces++;
                        else if (tokens[member].Text == "}") braces--;
                        member++;
                    }
                    if (braces != 0) return; // unsupported malformed body
                    continue;
                }
                int cursor = member;
                if (tokens[cursor].Text is "pub" or "private") cursor++;
                bool obsoleteMethodIntroducer = false;
                bool staticMethod = cursor < tokens.Count &&
                    tokens[cursor].Text == "static";
                if (staticMethod)
                {
                    // Static source methods use the same duplicate registry
                    // but a disjoint namespace and NO injected receiver.
                    cursor++;
                    if (cursor >= tokens.Count || tokens[cursor].Text != "fnc")
                    {
                        member++;
                        continue;
                    }
                    cursor++;
                }
                else if (cursor < tokens.Count && tokens[cursor].Text == "fnc")
                    cursor++; // compatibility introducer, never a callable name
                else if (cursor + 2 < tokens.Count &&
                    (tokens[cursor].Text is "mtd" or "method") &&
                    IsSimpleNominalWord(tokens[cursor + 1].Text) &&
                    tokens[cursor + 2].Text == "(")
                {
                    // Inspect a complete declaration before diagnosing the obsolete marker.
                    // A callable literally named method() is not an introducer.
                    obsoleteMethodIntroducer = tokens[cursor].Text == "method";
                    cursor++;
                }
                if (cursor < tokens.Count && tokens[cursor].Text == "constructor")
                {
                    // Constructors do not occupy ordinary method slots.
                    // Skip the signature and its body without counting calls.
                    member = cursor + 1;
                    while (member < tokens.Count &&
                           tokens[member].Text is not ("{" or ";" or "end"))
                        member++;
                    if (member < tokens.Count && tokens[member].Text == ";")
                        member++;
                    continue;
                }
                if (cursor + 1 >= tokens.Count ||
                    !IsSimpleNominalWord(tokens[cursor].Text) ||
                    tokens[cursor + 1].Text != "(")
                {
                    // Avoid interpreting an expression or field initializer
                    // as a method declaration. Class lowering stays disabled.
                    member++;
                    continue;
                }
                string name = tokens[cursor].Text;
                if (name is "constructor" or "fnc" or "static") {
                    member++;
                    continue;
                }
                int scan = cursor + 1;
                if (!TryReadNominalFormals(tokens, ref scan, out var first, owner, !staticMethod))
                {
                    member++;
                    continue;
                }
                bool explicitSelf = first.Count > 0 &&
                    (first[0] is "self" or "mut self");
                bool mutableSelf = first.Count > 0 && first[0] == "mut self";
                int values = explicitSelf ? first.Count - 1 : first.Count;
                if (explicitSelf && first.Count == 1 &&
                    scan < tokens.Count && tokens[scan].Text == "(")
                {
                    if (!TryReadNominalFormals(tokens, ref scan, out var second, owner, false))
                    {
                        member++;
                        continue;
                    }
                    values = second.Count;
                }
                // Only claim declaration identity for complete, simple
                // signatures with braced bodies, not accidental calls.
                if (scan + 2 >= tokens.Count ||
                    tokens[scan].Text is not (":" or "->") ||
                    !IsSimpleNominalWord(tokens[scan + 1].Text) ||
                    tokens[scan + 2].Text != "{")
                {
                    member++;
                    continue;
                }
                if (obsoleteMethodIntroducer)
                    throw new FormatException($"Obsolete method introducer; use mtd at offset {tokens[member].Offset}");
                try
                {
                    slots.DeclareLowered(owner, name, checked(values + (staticMethod ? 0 : 1)),
                        staticMethod ? ReceiverMethodNamespace.Static : ReceiverMethodNamespace.Instance,
                        explicitSelf: explicitSelf, mutableSelf: mutableSelf);
                }
                catch (FormatException error)
                {
                    throw new FormatException(
                        $"{error.Message}; nominal class/actor methods require receiver-erased method slots, " +
                        $"type/borrow checking and Native AOT CIL lowering at offset {tokens[cursor].Offset}", error);
                }
                int depth = 1;
                scan += 3;
                while (scan < tokens.Count && depth > 0)
                {
                    if (tokens[scan].Text == "{") depth++;
                    else if (tokens[scan].Text == "}") depth--;
                    scan++;
                }
                if (depth != 0) return; // malformed source stays fail-closed
                member = scan;
            }
            i = Math.Max(i,member);
        }
    }

    private static bool IsSimpleNominalWord(string value)
    {
        if (value.Length is < 1 or > MaxIdentifier) return false;
        if (!(value[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_'))
            return false;
        return value.Skip(1).All(c =>
            c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    }

    private static bool TryReadNominalFormals(
        IReadOnlyList<Token> tokens, ref int scan, out List<string> parameters,
        string owner, bool allowReceiver)
    {
        parameters = new List<string>();
        if (scan >= tokens.Count || tokens[scan++].Text != "(") return false;
        bool expectValue = true;
        while (scan < tokens.Count)
        {
            string next = tokens[scan].Text;
            if (next == ")")
            {
                if (expectValue && parameters.Count > 0) return false;
                scan++;
                return true;
            }
            if (next == "," || next == "(" || next == "{" || next == "}")
                return false;
            // Parse only scalar name/type groups, with no nested generics
            // or function type expressions in this syntax-only step.
            var parts = new List<string>();
            while (scan < tokens.Count &&
                   tokens[scan].Text is not ("," or ")"))
            {
                if (!IsSimpleNominalWord(tokens[scan].Text) || parts.Count >= 4)
                    return false;
                parts.Add(tokens[scan++].Text);
            }
            if (parts.Count is < 1 or > 3) return false;
            bool bareOrTypedReadSelf = parts[0] == "self";
            bool bareOrTypedMutSelf = parts.Count >= 2 &&
                parts[0] == "mut" && parts[1] == "self";
            if (bareOrTypedReadSelf || bareOrTypedMutSelf)
            {
                // Typed legacy receivers are declaration metadata, not values.
                // Only accept a typed receiver matching the nominal owner;
                // malformed or foreign receiver types stay fail-closed without
                // manufacturing a duplicate-method error.
                if (!allowReceiver || parameters.Count != 0) return false;
                if (bareOrTypedReadSelf)
                {
                    if (parts.Count > 2 ||
                        (parts.Count == 2 && parts[1] != owner)) return false;
                    parameters.Add("self");
                }
                else
                {
                    if (parts.Count < 2 || parts.Count > 3 ||
                        (parts.Count == 3 && parts[2] != owner)) return false;
                    parameters.Add("mut self");
                }
            }
            else
            {
                // Ordinary parameters require a type and a binding name;
                // a lone word cannot accidentally become an argument.
                if (parts.Count == 1 || parts[0] == "mut") return false;
                parameters.Add(string.Join(" ", parts));
            }
            if (scan < tokens.Count && tokens[scan].Text == ",")
            {
                scan++;
                expectValue=true;
            }
            else expectValue=false;
        }
        return false;
    }

    private readonly record struct Token(string Text, int Offset);
    private sealed class Lexer(string source, bool moduleMode = false,
        bool allowStaticTupleProjection = false, bool attachedStructTarget = false)
    {
        private int index;
        private bool previousWasWith;
        private bool previousWasString;
        private bool previousWasSync;
        private string previousToken = "", beforePreviousToken = "";
        private bool insideBooleanLiteralPack;
        private bool previousWasConst;
        private bool sawTupleDeclaration;
        public Token Read()
        {
            while (index < source.Length)
            {
                if (char.IsWhiteSpace(source[index])) { index++; continue; }
                if (source[index] == '/' && index + 1 < source.Length &&
                    source[index + 1] == '/')
                {
                    index += 2;
                    while (index < source.Length && source[index] is not ('\n' or '\r' or '\u0085' or '\u2028' or '\u2029')) index++;
                    continue;
                }
                // Match the shipping scalar lexer: comments are not source
                // tokens, including nested enum-looking text. Bound nesting
                // before any CFG/IR admission to avoid pathological input.
                if (source[index] == '/' && index + 1 < source.Length &&
                    source[index + 1] == '*')
                {
                    int start = index;
                    index += 2;
                    int depth = 1;
                    while (depth > 0)
                    {
                        if (index == source.Length)
                            throw new FormatException($"Unterminated block comment at offset {start}");
                        if (index + 1 < source.Length &&
                            source[index] == '/' && source[index + 1] == '*')
                        {
                            if (++depth > 128)
                                throw new FormatException($"Block comment nesting exceeds 128 at offset {start}");
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
                    continue;
                }
                break;
            }
            int offset = index;
            if (index == source.Length) return new Token("", index);
            char ch = source[index];
            bool precededByWith = previousWasWith;
            bool precededByString = previousWasString;
            previousWasWith = false;
            previousWasString = false;
            if (precededByWith && ch == '{' && !attachedStructTarget)
                throw new FormatException(
                    $"WITH_BACKEND_UNSUPPORTED: immutable with requires checked rt copy/take, "
                    + $"owned field layout/drop and Native AOT reconstruction at offset {index}");
            // Keep safe-collection denial from current main in the separate
            // structured/linked parser, including when Option<int> is enabled.
            // Only standalone source following a lexical const Tuple declaration
            // may defer bracket admission to the verified static projection reader.
            // Brackets remain denied everywhere except the tightly scoped
            // named Boolean literal pack. Reader independently validates
            // the intrinsic identity, types, arity and literal-only shape.
            bool booleanPackBracket = false;
            if (ch == '[' && !insideBooleanLiteralPack && previousToken == "(" &&
                beforePreviousToken is ("And" or "Or" or "Xor"))
            {
                insideBooleanLiteralPack = true;
                booleanPackBracket = true;
            }
            else if (ch == ']' && insideBooleanLiteralPack)
            {
                insideBooleanLiteralPack = false;
                booleanPackBracket = true;
            }
            if (!booleanPackBracket && !(allowStaticTupleProjection && sawTupleDeclaration))
                SafeCollectionSourceAdmission.RejectUnverifiedBracket(ch, offset);
            // Injection changes ownership and lifetime authority. The scalar
            // CIL bootstrap cannot treat an annotation as harmless metadata.
            // Perform this check after comments have been skipped so comment
            // text cannot trigger a false rejection.
            if (ch == '@')
            {
                int nameEnd = index + 1;
                nameEnd = PortableUnicodeIdentifier.End(source, nameEnd);
                string name = source[(index + 1)..nameEnd];
                if (name is "Inject" or "StateBacked" or "MutableStateContainer" or "StateContainer")
                    throw new FormatException(
                        $"STATE_INJECTION_UNSUPPORTED: @{name} requires typed container ownership, borrow checks and Native AOT lowering at offset {offset}");
                throw new FormatException(
                    $"Annotations require verified compiler lowering at offset {offset}");
            }
            if (ch is '\'' or '"' or '\u0060')
            {
                index = QuotedSourceSyntax.Scan(source, offset);
                beforePreviousToken = previousToken;
                previousToken = source[offset..index];
                return new Token(previousToken, offset);
            }
            if (Digit(ch))
            {
                index++;
                while (index < source.Length)
                {
                    if (Digit(source[index])) { index++; continue; }
                    if (source[index] != '_') break;
                    if (index + 1 >= source.Length || !Digit(source[index + 1]))
                        throw new FormatException($"Invalid i64 digit separator at offset {index}");
                    index++;
                }
                // Keep the structured i64-only parser aligned with the CIL
                // expression parser: no float/decimal token splitting.
                if (index < source.Length &&
                    ((source[index] == '.' && index + 1 < source.Length && Digit(source[index + 1])) ||
                     ((source[index] == 'e' || source[index] == 'E') && index + 1 < source.Length &&
                       (Digit(source[index + 1]) ||
                        ((source[index + 1] == '+' || source[index + 1] == '-') &&
                          index + 2 < source.Length && Digit(source[index + 2])))) ||
                     (source[index] == 'd' && (index + 1 == source.Length ||
                         PortableUnicodeIdentifier.PartWidth(source, index + 1) == 0))))
                    throw new FormatException($"Floating-point/decimal literal requires typed numeric lowering at offset {offset}");
            }
            else if (PortableUnicodeIdentifier.StartWidth(source, index) != 0)
            {
                index = PortableUnicodeIdentifier.End(source, index);
                string word = source[offset..index];
                // Check complete keyword suffix before any effect token is consumed.
                if ((word is "trap" or "throw" or "raise" or "panic" or "copy" or "take" or "move" or
                    "borrow" or "share" or "pin" or "drop" or "pipe" or "rpipe" or "stop" or "expand") &&
                    index < source.Length && source[index] == '!' &&
                    (index + 1 >= source.Length || source[index + 1] != '=') &&
                    !(source.AsSpan(index).StartsWith("!eq", StringComparison.Ordinal) &&
                      (index + 3 == source.Length ||
                       PortableUnicodeIdentifier.PartWidth(source, index + 3) == 0)))
                {
                    int after = index + (word == "copy" && index + 1 < source.Length && source[index + 1] == '!' ? 2 : 1);
                    if (after >= source.Length || source[after] is not (' ' or '\t' or '\r' or '\n' or '\v' or '\f'))
                        throw new FormatException($"E_BANG_KEYWORD_WHITESPACE: whitespace required after bang keyword at offset {offset}");
                }
                if (word is "trap" or "throw" or "panic" or "raise" && index < source.Length &&
                    source[index] == '!' && (index + 1 == source.Length || source[index + 1] != '=') &&
                    !(index + 2 < source.Length && source[index + 1] == 'e' && source[index + 2] == 'q' &&
                        (index + 3 == source.Length ||
                        PortableUnicodeIdentifier.PartWidth(source, index + 3) == 0)))
                    index++;
                if (word == "select" &&
                    StaticSelectAdmission.FollowedByStaticSelect(source, offset))
                    throw StaticSelectAdmission.Unsupported(offset);
                // Ownership spelling is recognized from lexical tokens, not
                // raw regexes over comments/strings. The structured scalar
                // backend has no typed borrow/move/share HIR, so refuse it
                // before generic '!' or macro syntax can swallow the effect.
                // 'copy !foo' is not an ownership prefix: the verb must touch '!'.
                if ((word is "copy" or "take" or "move" or "borrow" or "share") &&
                    index < source.Length && source[index] == '!' &&
                    !(index + 1 < source.Length && source[index + 1] == '=') &&
                    !(index + 2 < source.Length && source[index + 1] == 'e' &&
                      source[index + 2] == 'q' &&
                      (index + 3 == source.Length ||
                       PortableUnicodeIdentifier.PartWidth(source, index + 3) == 0)))
                {
                    // Only the checked structured scalar profile admits copy.
                    // Borrow, move, share and linked-module ownership still fail closed.
                    if (word != "copy" || moduleMode)
                        throw new FormatException(
                            $"OWNERSHIP_BANG_BACKEND_UNSUPPORTED: '{word}!' requires typed ownership and actor-arena Native AOT lowering at offset {offset}");
                    index++;
                    if (index < source.Length && source[index] == '!')
                        index++;
                    if (index < source.Length && source[index] == '!')
                        throw new FormatException($"E_COPY_SYNTAX: at most two adjacent copy bangs at offset {offset}");
                }
                // The linked-module lexer also enforces the unsafe keyword
                // reservation before typed module binding and CIL emission.
                if (word == "unsafe")
                    throw new FormatException(
                        $"unsafe boundary requires checked effects, native grants and Native AOT lowering at offset {offset}");
                // The structured CFG frontend admits statically terminal
                // boolean guards. Its lexer must preserve the real token so
                // the typed Statement path validates and lowers it below.
                // The separate shipping scalar lexer remains fail-closed.
                if (word is "cpu" or "gpu" or "quantum" ||
                    (word == "nb" && !StaticSelectAdmission.FollowedByStaticSelect(source, index)))
                    throw new FormatException(
                        $"execution region '{word}' requires verified CPU/GPU Task lowering; no implicit CPU fallback at offset {offset}");
                if (word == "task" && !previousWasSync)
                {
                    if (ContextualTaskBoundary.OpensBlock(source, index))
                        throw new FormatException(
                            $"execution task requires scheduler-aware Future/continuation lowering; no implicit inline execution at offset {offset}");
                }
            }
            // A source-level method may spell its result type with '->'.
            // Tokenize the pair even in scalar mode so nominal declarations
            // reach their bounded duplicate-slot check. The scalar Reader
            // still rejects arrow syntax outside its admitted grammar.
            else if (ch == '-' && index + 1 < source.Length &&
                     source[index + 1] == '>') index += 2;
            else if (attachedStructTarget && ch == '\u2022') index++;
            else if (attachedStructTarget && ch == '<' && index + 1 < source.Length && source[index + 1] == '-') index += 2;
            else if (ch == '~')
                throw new FormatException(
                    $"TILDE_AGGREGATE_BACKEND_UNSUPPORTED: typed Map/Record literals require a checked AST, key domains, ownership, transactional constructor and NativeAOT layout at offset {offset}");
            else if (ch == '%')
                throw new FormatException(
                    $"noncanonical '%' source operator; use named Integer.rem/mod at offset {offset}");
            else if (ch == '^')
            {
                // The reader admits single caret only in return position.
                // Longest contiguous caret operator first; trivia cannot synthesize ^^=.
                index += index + 2 < source.Length && source[index + 1] == '^' && source[index + 2] == '=' ? 3 :
                    index + 1 < source.Length && source[index + 1] == '^' ? 2 : 1;
            }
            else if (ch == '!' && index + 2 < source.Length &&
                     source[index + 1] == 'e' && source[index + 2] == 'q' &&
                     (index + 3 == source.Length ||
                      PortableUnicodeIdentifier.PartWidth(source, index + 3) == 0))
            {
                // Contiguous AttoLang !eq alias; never split !equation.
                // Lower into the existing static typed CIL NotEqualI64 path.
                index += 3;
            }
            else if (ch == '=' && index + 1 < source.Length &&
                     source[index + 1] == '=') index += 2;
            // AttoLang's !eq is one indivisible binary token. It must not be
            // lexed as unary ! + identifier eq, nor as a prefix of !eqname.
            // Reuse the existing statically typed NotEqualI64 -> CIL Ceq+invert
            // pipeline. Legacy != remains admitted pending a versioned strict
            // source-profile migration; this change is additive only.
            else if (ch == '!' && index + 2 < source.Length &&
                     source[index + 1] == 'e' && source[index + 2] == 'q' &&
                     (index + 3 == source.Length ||
                      PortableUnicodeIdentifier.PartWidth(source, index + 3) == 0))
                index += 3;
            else if (ch == '!' && index + 1 < source.Length &&
                     source[index + 1] == '=') index += 2;
            else if (index + 1 < source.Length &&
                (((ch == '<' || ch == '>') && source[index + 1] == '=') ||
                 source.Substring(index, 2) is "->" or "::" or "&&" or "||"))
                index += 2;
            // Functional value application is a checked expression operator,
            // never an actor graph or a general closure capability.
            else if (index + 1 < source.Length &&
                     ((ch == '|' && source[index + 1] == '>') ||
                      (ch == '<' && source[index + 1] == '|')))
                index += 2;
            else if (ch == '|' && !moduleMode && !precededByString)
                throw new FormatException(
                    $"Lexical captures require checked closure lowering at offset {offset}");
            // Prefix logical not already has typed boolean lowering in Unary.
            // Preserve the earlier != / !eq recognition and the separate
            // fail-closed macro and ownership admission gates.
            else if (ch is '!' or '¬') index++;
            else if (ch is '⧖' or '∂') index++;
            else if (booleanPackBracket || "()+-*/{}:;=<>.,?~".IndexOf(ch) >= 0 ||
                (ch == '|' && precededByString) ||
                (allowStaticTupleProjection && sawTupleDeclaration && ch is ('[' or ']')) ||
                (moduleMode && "[]/>!|&".IndexOf(ch) >= 0)) index++;
            else throw new FormatException($"Invalid character at offset {offset}");
            var tokenText = source[offset..index];
            if (tokenText is "susp" or "⧖" or "∂") tokenText = "suspends";
            if (tokenText == "import" && !moduleMode)
                throw new FormatException("source imports require verified module graph lowering");
            if (tokenText == "enum")
                throw new FormatException(
                    $"nominal enum overloads require compiler-bound dispatch (unsupported .NET bootstrap) at offset {offset}");
            previousWasWith = tokenText == "with";
            // Linked source retains the existing closed tagged-family boundary.
            if (moduleMode && tokenText is ("Result" or "Ok" or "Err"))
                throw new FormatException($"E_CORE_TYPE_LOWERING_UNSUPPORTED: linked Result ABI is not verified at offset {offset}");
            if (moduleMode && previousWasSync && tokenText == "task")
                throw new FormatException($"E_LINKED_SYNC_TASK_UNSUPPORTED: linked inline task ABI is not verified at offset {offset}");
            if (moduleMode && tokenText is ("trap" or "trap!" or "throw" or "throw!" or
                "raise" or "raise!" or "panic" or "panic!"))
                throw new FormatException($"E_LINKED_GUEST_EFFECT_UNSUPPORTED: linked guest effect ABI is not verified at offset {offset}");
            previousWasString = tokenText is "String" or "string";
            previousWasSync = tokenText == "sync";
            sawTupleDeclaration |= previousWasConst && tokenText == "Tuple";
            previousWasConst = tokenText == "const";
            beforePreviousToken = previousToken;
            previousToken = tokenText;
            return new Token(tokenText, offset);
        }
    }

    private sealed partial class Reader(List<Token> tokens, LinkedModuleContext? linking = null,
        bool allowCoreNamespace = false, bool productionGuard = false,
        bool strictConditionals = false, bool allowDeferredStdout = false)
    {
        private sealed record LocalBinding(IrValueType Type, StructuredBindingKind Kind,
            long? ConstantValue);
        private readonly List<Dictionary<string, LocalBinding>> scopes = new();
        private readonly List<HashSet<string>> localTypeScopes = new();
        // Constant scalar aggregates are compile-time values only; no hidden
        // managed object, dynamic descriptor or NativeAOT aggregate ABI.
        private sealed record StaticAggregate(ClosedAggregateShape Shape,
            IReadOnlyDictionary<string, StructuredExpr> Values, string? Wrapper = null);
        private sealed record NamedAggregateType(string Wrapper, ClosedAggregateShape Shape);
        private readonly List<Dictionary<string, NamedAggregateType>> namedAggregateScopes = new();

        private NamedAggregateType? FindNamedAggregateType(string name)
        {
            for (int i = namedAggregateScopes.Count - 1; i >= 0; --i)
                if (namedAggregateScopes[i].TryGetValue(name, out var type)) return type;
            return null;
        }
        private readonly List<Dictionary<string, StaticAggregate>> aggregateScopes = new();
        private int aggregateSentinels;
        private StaticAggregate? FindAggregate(string name)
        {
            for (int i = aggregateScopes.Count - 1; i >= 0; --i)
                if (aggregateScopes[i].TryGetValue(name, out var value)) return value;
            return null;
        }

        private readonly Dictionary<string, StructuredFunction> functions = linking is null
            ? new(StringComparer.Ordinal) : new(linking.Methods, StringComparer.Ordinal);
        private readonly Dictionary<string, StructuredFunction> visible = linking is null
            ? new(StringComparer.Ordinal) : new(linking.Bindings, StringComparer.Ordinal);
        private readonly Dictionary<string, StructuredFunction> exports = new(StringComparer.Ordinal);
        private readonly List<string> functionOrder = linking is null ? new() : new(linking.MethodOrder);
        // Closed source-local single-int generic identity specialization.
        private readonly Dictionary<string, string> scalarGenericParameters = new(StringComparer.Ordinal);
        private string? declaredModule;
        private bool namespaceHeader;
        private int index, statements, loopDepth;
        private IrValueType activeReturnType = IrValueType.I64;
        // Only the scalar binding clause of an explicit -> Type :: body
        // admits comma as a statement boundary. Nested ordinary blocks do not.
        private bool commaBindingTerminator;
        // Track actual AST height, not just grammar recursion depth. A flat
        // 50,000-term sum must not construct a 50,000-level tree for later passes.
        // Records have structural equality; identity avoids recursive hashing.
        private readonly Dictionary<StructuredExpr, int> expressionHeights =
            new(ReferenceEqualityComparer.Instance);
        private int Height(StructuredExpr expression) =>
            expressionHeights.TryGetValue(expression, out int height) ? height : 1;
        private StructuredExpr CheckedBinary(int at, StructuredOperator op,
            StructuredExpr left, StructuredExpr right, IrValueType resultType)
        {
            int height = Math.Max(Height(left), Height(right)) + 1;
            if (height > MaxDepth)
                throw new FormatException($"Structured AST depth exceeds limit at offset {at}");
            var node = new StructuredBinary(at, op, left, right, resultType);
            expressionHeights.Add(node, height);
            return node;
        }
        private StructuredExpr CheckedConditional(int at, StructuredExpr test,
            StructuredExpr yes, StructuredExpr no)
        {
            int height = Math.Max(Height(test), Math.Max(Height(yes), Height(no))) + 1;
            if (height > MaxDepth)
                throw new FormatException($"Structured AST depth exceeds limit at offset {at}");
            var node = new StructuredConditional(at, test, yes, no, yes.Type);
            expressionHeights.Add(node, height);
            return node;
        }
        private StructuredExpr CheckedNegate(int at, StructuredExpr value)
        {
            int height = Height(value) + 1;
            if (height > MaxDepth)
                throw new FormatException($"Structured AST depth exceeds limit at offset {at}");
            var node = new StructuredNegate(at, value);
            expressionHeights.Add(node, height);
            return node;
        }
        private Token Current => tokens[index];
        private bool Take(string value)
        {
            if (Current.Text != value) return false;
            index++;
            return true;
        }
        private void Expect(string value)
        {
            if (!Take(value)) throw Error($"Expected '{value}'");
        }
        private FormatException Error(string reason) =>
            new($"{reason} at offset {Current.Offset}");
        private void Budget(int depth)
        {
            if (depth > MaxDepth) throw Error("Structured syntax nesting exceeds limit");
        }
        private readonly HashSet<int> verifiedTupleBrackets = new();
        private readonly HashSet<int> verifiedBooleanPackBrackets = new();
        public StructuredProgram ReadProgram()
        {
            var program = ReadDeclarations(true);
            foreach (var token in tokens)
                if (token.Text is "[" or "]" && !verifiedTupleBrackets.Contains(token.Offset) && !verifiedBooleanPackBrackets.Contains(token.Offset))
                    SafeCollectionSourceAdmission.RejectUnverifiedBracket(token.Text[0], token.Offset);
            return program;
        }

        internal IReadOnlyList<SourceImport> ReadImports()
        {
            string? currentNamespace = null;
            // File-level namespace metadata is not a runtime object or an
            // ambient import. It is admitted here only for SHA-pinned Core
            // source identities; all other namespaces remain fail-closed.
            if (Take("use"))
            {
                Expect("namespace");
                if (Name() != "atto") throw Error("Core namespace root must be atto");
                Expect(".");
                if (Name() != "core") throw Error("Core namespace tier must be core");
                Expect(".");
                string leaf = Name();
                if (leaf is not ("math" or "hash" or "cell"))
                    throw Error("Unregistered compiler-owned Core namespace");
                Expect(";");
                if (linking is null && !allowCoreNamespace)
                    throw Error("Core namespace requires authenticated Core import provenance");
                currentNamespace = "atto.core." + leaf;
                if (linking is not null)
                {
                    if (!string.Equals(linking.UnitIdentity, "@atto.core/" + leaf + ".atto",
                        StringComparison.Ordinal))
                        throw Error("Core namespace does not match compiler-owned unit identity");
                    declaredModule = leaf; // export adapter for legacy import module consumers
                    namespaceHeader = true;
                }
            }
            var imports = new List<SourceImport>();
            while (Take("import"))
            {
                int offset = Current.Offset;
                // Validate contextual path shape before any filesystem or
                // external lookup. Linking requires authenticated package metadata.
                if (Current.Text is "self" or "super" ||
                    (Current.Text == "pkg" && index + 1 < tokens.Count &&
                     tokens[index + 1].Text is "." or ";"))
                {
                    string logical = Current.Text;
                    index++;
                    int components = 1;
                    while (Take("."))
                    {
                        if (++components > 8 || Current.Text.Length > 64)
                            throw Error("E_IMPORT_PATH: contextual path exceeds budget");
                        if (Current.Text is "." or ";" or "")
                            throw Error("E_IMPORT_PATH: missing contextual path component");
                        logical += "." + Current.Text;
                        index++;
                    }
                    if (Take("as")) _ = Name();
                    Expect(";");
                    _ = ContextualImportPath.Normalize(logical, currentNamespace);
                    throw Error("E_UNSUPPORTED: contextual import requires authenticated package-export graph");
                }
                // Closed compiler-owned namespace-first aliases only. Emit the
                // identical import record used by the existing authenticated
                // Core wildcard lane, never a caller-supplied path.
                if (Take("namespace"))
                {
                    string alias = Name();
                    if (!Take("from"))
                        throw Error("import namespace requires manifest-verified multi-file package linker");
                    string selector = Current.Text;
                    if (selector.Length == 0) throw Error("import namespace requires manifest-verified multi-file package linker");
                    index++;
                    if (selector.Length >= 2 && (selector[0] == '"' || selector[0] == '\'') &&
                        selector[^1] == selector[0])
                        selector = selector[1..^1];
                    else if (selector == "core" && Take("."))
                    {
                        if (Current.Text.Length == 0) throw Error("import namespace requires manifest-verified multi-file package linker");
                        selector += "." + Current.Text;
                        index++;
                    }
                    string? canonical = selector switch
                    {
                        "core.math" => "atto.core/math",
                        "core.hash" => "atto.core/hash",
                        "core.cell" => "atto.core/cell",
                        _ => null
                    };
                    if (canonical is null || Current.Text != ";")
                        throw Error("import namespace requires manifest-verified multi-file package linker");
                    UnsafeStdlibImportAdmission.RequireGuestSafe(canonical);
                    Expect(";");
                    if (imports.Count >= 64) throw Error("Import declaration budget exceeded");
                    imports.Add(new SourceImport("builtin_namespace", alias, canonical, offset));
                    continue;
                }
                string kind;
                string name;
                if (Take("*"))
                {
                    // Narrow canonical builtin import selector:
                    // import * as math from "atto.core/math";
                    // Explicit 'pkg', local-file wildcards and third-party
                    // package namespaces require a separate verified linker.
                    Expect("as");
                    name = Name();
                    kind = "builtin_namespace";
                }
                else
                {
                    kind = Current.Text;
                    if (kind is not ("fnc" or "module"))
                        throw Error("Linked source supports fnc, module and authenticated Core wildcard imports");
                    index++;
                    name = Name();
                }
                Expect("from");
                string quoted = Current.Text;
                if (quoted.Length < 3 ||
                    (quoted[0] != '"' && quoted[0] != '\'') ||
                    quoted[^1] != quoted[0] || quoted.Contains('\\'))
                    throw Error("Import requires an unescaped single- or double-quoted local source string");
                // Reject before physical lookup and graph linking, independently
                // of the standalone scalar parser's generic import denial.
                UnsafeStdlibImportAdmission.RequireGuestSafe(quoted[1..^1]);
                if (kind == "builtin_namespace" &&
                    quoted[1..^1] is not ("atto.core/math" or "atto.core/hash" or "atto.core/cell"
                        or "core/math" or "core/hash" or "core/cell"))
                    throw Error("E_CORE_NAMESPACE_IMPORT_AUTHORITY: wildcard imports require a compiler-owned Core namespace");
                index++;
                Expect(";");
                if (imports.Count >= 64) throw Error("Import declaration budget exceeded");
                imports.Add(new SourceImport(kind, name, quoted[1..^1], offset));
            }
            RejectUnlinkedAliases();
            return imports.AsReadOnly();
        }

        // A bounded declaration batch is normalized before any source module
        // discovery. Do not treat an acyclic alias as an executable constant.
        private bool AtAlias() => Current.Text == "define" && index + 1 < tokens.Count
            && tokens[index + 1].Text == "alias";

        private string ReadAliasPath()
        {
            string first = Name();
            if (first.Length > 64) throw Error("E_IMPORT_PATH: alias component too long");
            string path = first;
            int count = 1;
            while (Take("."))
            {
                if (++count > 8) throw Error("E_IMPORT_PATH: alias path too deep");
                string part = Name();
                if (part.Length > 64) throw Error("E_IMPORT_PATH: alias component too long");
                path += "." + part;
            }
            return path;
        }

        private void RejectUnlinkedAliases()
        {
            if (!AtAlias()) return;
            var aliases = new List<StaticImportGraph.Alias>();
            while (AtAlias())
            {
                index += 2;
                if (aliases.Count >= 256) throw Error("E_SOURCE_LIMIT: namespace alias budget exceeded");
                string left = ReadAliasPath();
                string name, reference;
                if (Take("=")) { name = left; reference = ReadAliasPath(); }
                else { Expect("as"); reference = left; name = Name(); }
                Expect(";");
                aliases.Add(new StaticImportGraph.Alias(name, reference));
            }
            StaticImportGraph.PrecheckAliases(aliases);
            throw Error("E_UNSUPPORTED: namespace aliases require authenticated static binding integration");
        }

        private bool frozenProvider;
        private string frozenMethodPrefix = "";
        private readonly Dictionary<string,Func<string,StructuredFunction>> frozenNamespaceBindings=new(StringComparer.Ordinal);
        internal StructuredLinkedUnit ReadFrozenNamespace(string expected, List<StaticImportGraph.Export> inventory, string methodPrefix, Func<string,string,StructuredFunction>? resolve,
            Func<string,string,Func<string,StructuredFunction>>? resolveNamespace)
        {
            // This entry point accepts bytes only from a verified snapshot.
            // Do not confer compiler-owned Core authority on any package.
            Expect("use"); Expect("namespace");
            string[] parts = expected.Split('.');
            for (int i=0; i<parts.Length; i++)
            {
                if (i != 0) Expect(".");
                if (Current.Text != parts[i]) throw Error("E_MANIFEST: provider namespace mismatch");
                index++;
            }
            Expect(";");
            var importedNames=new HashSet<string>(StringComparer.Ordinal);
            while(Take("import")) {
                if(resolve is null) throw Error("E_UNSUPPORTED: frozen provider imports require complete dependency closure");
                if(importedNames.Count>=32) throw Error("E_SOURCE_LIMIT: provider import budget exceeded");
                bool namespaceImport=Take("namespace");
                if(!namespaceImport) Expect("fnc");
                string member=Name(), binding=member;
                if(Take("as")) binding=Name();
                Expect("from"); Expect("pkg");
                string raw=Current.Text;
                if(raw.Length<3 || raw[0] is not ('"' or '\'') || raw[^1]!=raw[0] || raw.Contains('\\'))
                    throw Error("E_IMPORT_PATH: expected static package subpath");
                index++;
                Expect(";");
                if(!importedNames.Add(binding)) throw Error("E_DUPLICATE: provider import binding");
                if(namespaceImport) {
                    if(resolveNamespace is null) throw Error("E_UNSUPPORTED: frozen namespace import requires verified dependency binding");
                    frozenNamespaceBindings.Add(binding,resolveNamespace(raw[1..^1],member));
                } else {
                    var target=resolve(raw[1..^1],member);
                    visible.Add(binding,target);
                }
            }
            frozenProvider = true;
            frozenMethodPrefix = methodPrefix;
            var program = ReadDeclarations(false);
            inventory.Add(new StaticImportGraph.Export(expected,StaticImportGraph.Kind.Namespace,true));
            foreach (string name in visible.Keys.Where(x=>!importedNames.Contains(x)).OrderBy(x=>x,StringComparer.Ordinal))
                inventory.Add(new StaticImportGraph.Export(expected+"."+name,StaticImportGraph.Kind.Fnc,exports.ContainsKey(name)));
            return new StructuredLinkedUnit(program, null,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, StructuredFunction>(exports));
        }

        internal StructuredLinkedUnit ReadLinkedUnit()
        {
            _ = ReadImports();
            if (Current.Text == "define" && index + 2 < tokens.Count &&
                tokens[index + 1].Text == "pub" && tokens[index + 2].Text == "module")
            {
                index += 3;
                declaredModule = Name();
                Expect("as");
            }
            else if (Current.Text == "pub" && index + 2 < tokens.Count &&
                     tokens[index + 1].Text == "define" && tokens[index + 2].Text == "module")
            {
                index += 3;
                declaredModule = Name();
                Expect("as");
            }
            else if (Current.Text == "define" && index + 1 < tokens.Count &&
                     tokens[index + 1].Text == "module")
            {
                index += 2;
                declaredModule = Name();
                Expect("as");
            }
            var program = ReadDeclarations(linking!.Entry);
            return new StructuredLinkedUnit(program, declaredModule,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, StructuredFunction>(exports));
        }

        private StructuredProgram Finish(string name, IReadOnlyList<StructuredStmt> body)
        {
            if (declaredModule is not null && !namespaceHeader) Expect("end");
            if (Current.Text.Length != 0) throw Error("Unexpected trailing token");
            return new StructuredProgram(name, body,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, StructuredFunction>(
                    new Dictionary<string, StructuredFunction>(functions, StringComparer.Ordinal)),
                Array.AsReadOnly(functionOrder.ToArray()));
        }

        private StructuredProgram ReadDeclarations(bool entry)
        {
            // Acyclic helper declarations must precede main. The declaration
            // registry only contains *fully typechecked* earlier functions:
            // recursion, mutual recursion and forward references fail closed.
            while (true)
            {
                if (!entry && (Current.Text.Length == 0 || Current.Text == "end"))
                    return Finish("library", Array.Empty<StructuredStmt>());
                bool defined = false, exposed = false;
                while (Current.Text is "define" or "pub")
                {
                    if (Take("define"))
                    {
                        if (defined) throw Error("Duplicate define modifier");
                        defined = true;
                    }
                    else
                    {
                        Expect("pub");
                        if (exposed) throw Error("Duplicate pub modifier");
                        exposed = true;
                    }
                }
                // A callable may declare suspension permission even when
                // this profile can prove it has no suspension points.
                if (!Take("susp")) Take("suspends");
                bool trapped = Take("trap") || Take("trap!");
                if (Current.Text is "class" or "actor")
                    throw Error("Nominal class/actor methods require receiver-erased method slots, type/borrow checking and Native AOT CIL lowering; unsupported in .NET structured frontend");
                bool ordinaryFunction = Take("fnc");
                if (!ordinaryFunction)
                {
                    if (frozenProvider) throw Error("E_UNSUPPORTED: frozen provider supports ordinary root functions only");
                    Expect("routine");
                }
                int at = Current.Offset;
                string name = Name();
                // This NativeAOT slice does not lower once-only initialization
                // barriers. Do not silently compile a lifecycle hook into an
                // ordinary public CIL method reachable through imports.
                if (name == "init")
                    throw Error("module init requires once-only reachable initialization lowering");
                if (Take("<"))
                {
                    // Admit only a provably parametric identity body. No
                    // runtime generic ABI, reflection, or erased fallback.
                    if (!ordinaryFunction || !entry || exposed || trapped ||
                        linking is not null || frozenProvider || declaredModule is not null ||
                        name == "main" || visible.ContainsKey(name) ||
                        scalarGenericParameters.Count >= 16 || functions.Count >= MaxHelpers)
                        throw Error("E_GENERIC_UNSUPPORTED: private root identity profile required");
                    string generic = Name();
                    if (generic == name || generic == "_" || generic == "void")
                        throw Error("E_GENERIC_BINDER: invalid type binder");
                    Expect(">");
                    Expect("(");
                    string parameter;
                    if (Current.Text == generic &&
                        index + 1 < tokens.Count && tokens[index + 1].Text != ":")
                    {
                        Expect(generic);
                        parameter = Name();
                    }
                    else
                    {
                        parameter = Name();
                        Expect(":");
                        Expect(generic);
                    }
                    if (parameter == generic || parameter == name || parameter == "_")
                        throw Error("E_GENERIC_BINDER: invalid value binder");
                    Expect(")");
                    if (!Take("->")) Expect(":");
                    Expect(generic);
                    Expect("{");
                    Expect("return");
                    Expect(parameter);
                    Expect(";");
                    Expect("}");
                    var read = new StructuredRead(at, parameter, IrValueType.I64);
                    var identity = new StructuredFunction(name,
                        Array.AsReadOnly(new[] { new StructuredParameter(parameter, IrValueType.I64) }),
                        IrValueType.I64, read,
                        Array.AsReadOnly<StructuredStmt>(new StructuredStmt[] { new StructuredReturn(at, read) }));
                    scalarGenericParameters.Add(name, generic);
                    functions.Add(name, identity);
                    visible.Add(name, identity);
                    functionOrder.Add(name);
                    continue;
                }
                Expect("(");
                if (Current.Text == "(")
                    throw Error("Tuple-v2 shorthand parameter requires typed tuple ownership/ABI lowering");
                var parameters = new List<StructuredParameter>();
                var parameterTypes = new Dictionary<string, LocalBinding>(StringComparer.Ordinal);
                if (Current.Text != ")")
                {
                    do
                    {
                        if (parameters.Count >= MaxParameters)
                            throw Error("Typed helper parameter budget exceeded");
                        // Both x: int and int x use the same static ABI slot.
                        // The name-level '?' in x?: int is a distinct,
                        // as-yet-unlowered omission contract, not Option<T>.
                        string? nameFirst = null;
                        if (index + 2 < tokens.Count &&
                            tokens[index + 1].Text == "?" &&
                            tokens[index + 2].Text == ":")
                            throw Error("E_OPTIONAL_FORMAL_ABI_UNSUPPORTED: name-first 'x?: T' requires checked omission and full-arity ABI");
                        if (index + 1 < tokens.Count && tokens[index + 1].Text == ":")
                        {
                            nameFirst = Name();
                            Expect(":");
                        }
                        IrValueType type = Type();
                        if (type == IrValueType.FixedArrayI64)
                            throw Error("E_ARRAY_ABI_UNSUPPORTED: fixed array parameters need verified scalar layout ABI");
                        string parameter = nameFirst ?? Name();
                        if (Current.Text is "?" or "=")
                            throw Error("E_OPTIONAL_FORMAL_ABI_UNSUPPORTED: name-level '?' and parameter defaults require checked full-arity binding");
                        if (!parameterTypes.TryAdd(parameter,
                            new LocalBinding(type, StructuredBindingKind.Val, null)))
                            throw Error("Duplicate typed helper parameter");
                        parameters.Add(new StructuredParameter(parameter, type));
                    }
                    while (Take(","));
                }
                Expect(")");
                if (parameters.Sum(p => p.Type == IrValueType.OptionI64 ? 2 : 1)
                    > OresCfgVerifier.MaxMethodParameters)
                    throw Error("E_OPTION_ABI_BUDGET: flattened parameters exceed the verified method slot limit");
                bool arrowReturn = Take("->");
                if (!arrowReturn) Expect(":");
                if (Current.Text == "(")
                    throw Error("Tuple-v2 shorthand return requires typed tuple ownership/ABI lowering");
                IrValueType resultType = Take("void") ? IrValueType.Void : Type();
                if (resultType == IrValueType.FixedArrayI64)
                    throw Error("E_ARRAY_ABI_UNSUPPORTED: fixed array method results need verified scalar layout ABI");
                if (name == "main" && entry)
                {
                    if (visible.ContainsKey(name)) throw Error("Entry main conflicts with an imported callable");
                    activeReturnType = IrValueType.I64;
                    if (trapped || parameters.Count != 0 || resultType != IrValueType.I64)
                        throw new FormatException($"main must have signature ():int at offset {at}");
                    var (body, returns) = arrowReturn && Current.Text == "::"
                        ? CommaBody(0) : Block(0);
                    if (!returns) throw Error("RETURN_EXPLICIT_REQUIRED: not every main path returns int");
                    return Finish("main", body);
                }
                if (frozenNamespaceBindings.ContainsKey(name))
                    throw Error("E_DUPLICATE: provider callable shadows namespace binding");
                if (visible.ContainsKey(name))
                    throw new FormatException($"Duplicate typed helper at offset {at}");
                if (functions.Count + (trapped ? 2 : 1) > MaxHelpers)
                    throw Error("Typed helper declaration budget exceeded");
                // All helper bodies now share the same checked typed block
                // parser as main. The separate MethodDef ABI isolates locals.
                activeReturnType = resultType;
                scopes.Add(parameterTypes);
                var (helperBody, helperReturns) = arrowReturn && Current.Text == "::"
                    ? CommaBody(0) : Block(0);
                scopes.RemoveAt(scopes.Count - 1);
                if (!helperReturns && resultType != IrValueType.Void)
                    throw Error("RETURN_EXPLICIT_REQUIRED: not every non-void helper path returns");
                if (!helperReturns && resultType == IrValueType.Void)
                {
                    // Compiler-owned empty-stack terminator for permissible
                    // void fallthrough, not an implicit source value return.
                    var completed = helperBody.ToList();
                    completed.Add(new StructuredReturnVoid(at));
                    helperBody = Array.AsReadOnly(completed.ToArray());
                }
                StructuredExpr? inlineReturn =
                    helperBody.Count == 1 && helperBody[0] is StructuredReturn only
                        ? only.Value : null;
                string methodName = linking?.CanonicalName(name) ?? (frozenMethodPrefix + name);
                var declaration = new StructuredFunction(methodName,
                    Array.AsReadOnly(parameters.ToArray()), resultType,
                    inlineReturn, helperBody);
                if (trapped) declaration = WrapTrappedCallable(declaration, at);
                functions.Add(methodName, declaration);
                visible.Add(name, declaration);
                if (exposed) exports.Add(name, declaration);
                functionOrder.Add(methodName);
            }
        }
        /// <summary>
        /// Typed comma expression body. A final expression implicitly returns;
        /// previous bindings and intermediate expressions preserve strict order
        /// through the existing structured AST/CIL local machinery. This parser
        /// remains a scalar subset: nominal class/actor methods fail closed.
        /// </summary>
        private (IReadOnlyList<StructuredStmt> Body, bool Returns) CommaBody(int depth)
        {
            Budget(depth);
            if (productionGuard)
                throw Error("RETURN_EXPLICIT_REQUIRED: production structured declarations require a block with an explicit value return");
            Expect("::");
            scopes.Add(new Dictionary<string, LocalBinding>(StringComparer.Ordinal));
            localTypeScopes.Add(new HashSet<string>(StringComparer.Ordinal));
            aggregateScopes.Add(new Dictionary<string, StaticAggregate>(StringComparer.Ordinal));
            namedAggregateScopes.Add(new Dictionary<string, NamedAggregateType>(StringComparer.Ordinal));
            var body = new List<StructuredStmt>();
            bool terminated = false;
            while (Current.Text != "end")
            {
                if (Current.Text.Length == 0) throw Error("Unterminated comma expression body");
                if (Current.Text == ",") throw Error("Empty comma expression body item");
                if (terminated) throw Error("Unreachable comma expression body item");
                if (++statements > MaxStatements)
                    throw Error("Structured statement budget exceeded");
                if ((Current.Text is "let" or "val" or "const") || StartsScalarTypeFirst())
                {
                    bool previous = commaBindingTerminator;
                    commaBindingTerminator = true;
                    try
                    {
                        var (declaration, returns) = Statement(depth + 1);
                        body.Add(declaration);
                        terminated = returns;
                    }
                    finally { commaBindingTerminator = previous; }
                    if (!Take(",")) throw Error("Comma body local requires following ','");
                    if (Current.Text is "," or "end" or "")
                        throw Error("Comma body requires an item after ','");
                    continue;
                }
                if (Take("return"))
                {
                    // Compatibility with the Java :: callable parser:
                    // the trailing semicolon is optional at 'end'. Checking
                    // the terminal type still goes through the same rule
                    // as a braced return, not an unchecked implicit value.
                    int returnAt = tokens[index - 1].Offset;
                    var returned = Expr(depth + 1);
                    Require(returned, activeReturnType,
                        "Comma body explicit return does not match callable result type");
                    if (Current.Text != "end") Expect(";");
                    body.Add(new StructuredReturn(returnAt, returned));
                    terminated = true;
                    if (Current.Text != "end") throw Error("Return must terminate comma body");
                    continue;
                }
                int at = Current.Offset;
                var expression = Expr(depth + 1);
                if (Take(","))
                {
                    if (Current.Text is "," or "end" or "")
                        throw Error("Comma body requires an item after ','");
                    // A hidden local forces exactly-once evaluation in source
                    // order, including any checked helper-call side effects.
                    string temporary = "$comma_effect_" + body.Count.ToString(CultureInfo.InvariantCulture);
                    if (scopes[^1].ContainsKey(temporary))
                        throw Error("Duplicate compiler-owned comma temporary");
                    scopes[^1].Add(temporary,
                        new LocalBinding(expression.Type, StructuredBindingKind.Readonly, null));
                    body.Add(new StructuredLet(at, temporary, expression.Type, expression,
                        StructuredBindingKind.Readonly));
                    continue;
                }
                Require(expression, activeReturnType,
                    "Comma body result does not match callable result type");
                body.Add(new StructuredReturn(at, expression));
                terminated = true;
            }
            Expect("end");
            scopes.RemoveAt(scopes.Count - 1);
            localTypeScopes.RemoveAt(localTypeScopes.Count - 1);
            aggregateScopes.RemoveAt(aggregateScopes.Count - 1);
            namedAggregateScopes.RemoveAt(namedAggregateScopes.Count - 1);
            if (!terminated) throw Error("Non-void comma body requires terminal expression");
            return (Array.AsReadOnly(body.ToArray()), true);
        }

        private (IReadOnlyList<StructuredStmt> Body, bool Returns) Block(int depth)
        {
            Budget(depth);
            Expect("{");
            scopes.Add(new Dictionary<string, LocalBinding>(StringComparer.Ordinal));
            localTypeScopes.Add(new HashSet<string>(StringComparer.Ordinal));
            aggregateScopes.Add(new Dictionary<string, StaticAggregate>(StringComparer.Ordinal));
            namedAggregateScopes.Add(new Dictionary<string, NamedAggregateType>(StringComparer.Ordinal));
            var body = new List<StructuredStmt>();
            bool terminated = false;
            while (Current.Text != "}")
            {
                if (Current.Text.Length == 0) throw Error("Unterminated block");
                if (terminated) throw Error("Unreachable statement");
                if (++statements > MaxStatements)
                    throw Error("Structured statement budget exceeded");
                var (statement, returns) = Statement(depth + 1);
                body.Add(statement);
                terminated = returns;
            }
            Expect("}");
            scopes.RemoveAt(scopes.Count - 1);
            localTypeScopes.RemoveAt(localTypeScopes.Count - 1);
            aggregateScopes.RemoveAt(aggregateScopes.Count - 1);
            namedAggregateScopes.RemoveAt(namedAggregateScopes.Count - 1);
            return (Array.AsReadOnly(body.ToArray()), terminated);
        }
        // The existing bracketed CFG path remains available. Canonical ores.core
        // also uses 'if predicate; do ... [else ...] fi' without braces.
        // Close lexical scopes at each arm; never turn source into rewritten C#.
        private (IReadOnlyList<StructuredStmt> Body, bool Returns) DelimitedArm(int depth, bool outerElse = false)
        {
            Budget(depth);
            scopes.Add(new Dictionary<string, LocalBinding>(StringComparer.Ordinal));
            localTypeScopes.Add(new HashSet<string>(StringComparer.Ordinal));
            aggregateScopes.Add(new Dictionary<string, StaticAggregate>(StringComparer.Ordinal));
            namedAggregateScopes.Add(new Dictionary<string, NamedAggregateType>(StringComparer.Ordinal));
            var body = new List<StructuredStmt>();
            bool terminated = false;
            while (Current.Text is not ("else" or "fi"))
            {
                if (Current.Text.Length == 0 || Current.Text == "}")
                    throw Error("Unterminated Oreslang if/do/fi arm");
                if (terminated) throw Error("Unreachable statement");
                if (++statements > MaxStatements)
                    throw Error("Structured statement budget exceeded");
                var (statement, returns) = Statement(depth + 1, outerElse, keywordArm: true);
                body.Add(statement);
                terminated = returns;
            }
            scopes.RemoveAt(scopes.Count - 1);
            localTypeScopes.RemoveAt(localTypeScopes.Count - 1);
            aggregateScopes.RemoveAt(aggregateScopes.Count - 1);
            namedAggregateScopes.RemoveAt(namedAggregateScopes.Count - 1);
            return (Array.AsReadOnly(body.ToArray()), terminated);
        }

        // Guard's else/end failure arm has its own lexical scope, exactly
        // like Block({ ... }). It cannot capture or leak local bindings into
        // the continuation, and all reachable paths must be terminal.
        private (IReadOnlyList<StructuredStmt> Body, bool Returns) GuardKeywordArm(int depth)
        {
            Budget(depth);
            scopes.Add(new Dictionary<string, LocalBinding>(StringComparer.Ordinal));
            localTypeScopes.Add(new HashSet<string>(StringComparer.Ordinal));
            aggregateScopes.Add(new Dictionary<string, StaticAggregate>(StringComparer.Ordinal));
            namedAggregateScopes.Add(new Dictionary<string, NamedAggregateType>(StringComparer.Ordinal));
            try
            {
                var body = new List<StructuredStmt>();
                bool terminated = false;
                while (Current.Text != "end")
                {
                    if (Current.Text.Length == 0 || Current.Text is "}" or "fi")
                        throw Error("E_GUARD: expected 'end' to close keyword guard");
                    if (terminated) throw Error("E_GUARD: unreachable statement after terminal failure");
                    if (++statements > MaxStatements)
                        throw Error("Structured guard statement budget exceeded");
                    var (statement, returns) = Statement(depth + 1);
                    body.Add(statement);
                    terminated = returns;
                }
                Expect("end");
                return (Array.AsReadOnly(body.ToArray()), terminated);
            }
            finally
            {
                scopes.RemoveAt(scopes.Count - 1);
                localTypeScopes.RemoveAt(localTypeScopes.Count - 1);
                aggregateScopes.RemoveAt(aggregateScopes.Count - 1);
                namedAggregateScopes.RemoveAt(namedAggregateScopes.Count - 1);
            }
        }

        // Type-first aggregate binder, limited to the existing checked scalar
        // structured CIL profile. Tuple slots use decimal keys internally;
        // their wrapper kind is separate from Record and struct.
        private bool StartsNamedAggregate()
        {
            if (Current.Text != "const" || index + 3 >= tokens.Count) return false;
            string kind = tokens[index + 1].Text;
            if (kind is not ("Record" or "Tuple" or "struct")) return false;
            if (tokens[index + 2].Text == "<") return true;
            return index + 4 < tokens.Count && tokens[index + 3].Text == "=" &&
                tokens[index + 4].Text == (kind == "struct" ? "{" : "(");
        }

        // Aggregate names are erased by static scalar lowering. Do not let
        // this path accept language keywords that the ordinary parser rejects
        // (or that belong to unsupported statement forms).
        private string NamedAggregateIdentifier(bool typeAlias = false)
        {
            string name = Current.Text;
            if (name is "infer" or "unsafe" or "sync" or "task" or "pipe" or "rpipe"
                or "def" or "namespace" or "contract" or "conforms" or "interface"
                or "trait" or "types" or "typeof" or "abstract" or "extends" or "implements"
                or "impl" or "using" or "pure" or "suspends" or "generator" or "yield"
                or "nlex" or "begin" or "self" or "super" or "static" or "private" or "mut"
                or "then" or "elif" or "elseif" or "done" or "for" or "of" or "block"
                or "switch" or "maybe" or "matches" or "over" or "default" or "recover"
                or "defer" or "persistent" or "durable" or "shared" or "untrusted"
                or "spawn" or "stop" or "rt" or "await" or "nb" or "cb" or "select"
                or "readch" or "writech" or "eq" or "neq" or "null" or "void"
                or "obj" or "arr" or "cpu" or "gpu" or "quantum")
                throw Error("E_TYPE_NAME: reserved aggregate identifier");
            if (typeAlias && (CoreTypeRegistry.Lookup(name) is not null ||
                CoreTypeRegistry.IsProtectedIdentity(name) ||
                name is "Record" or "Tuple" or "Pointer" or "ActorRef" or "ActorMail"
                    or "ReadLoan" or "WriteLoan" or "AtomicInt" or "AtomicBool"
                    or "RwLock" or "Mutex" or "boolean"))
                throw Error("E_TYPE_NAME: compiler-owned aggregate type name");
            return Name();
        }

        private (StructuredStmt Statement, bool Returns) NamedAggregate(int depth, int at)
        {
            Expect("const");
            string wrapper = Current.Text;
            index++;
            string? alias = null;
            bool introduce = false;
            NamedAggregateType? expected = null;
            if (Take("<"))
            {
                introduce = Take("infer");
                alias = NamedAggregateIdentifier(typeAlias: true);
                if (alias is "infer" or "Record" or "Tuple" or "Map" or "Set" or "String" or "string"
                    or "Integer" or "Bool" or "Any" or "Unknown" or "void" or "null" or "self"
                    or "List" or "Vector" or "Slice" or "Pair" or "Entry" or "Future" or "Channel")
                    throw Error("E_TYPE_NAME: reserved aggregate alias");
                Expect(">");
                expected = FindNamedAggregateType(alias);
                if (introduce && (expected is not null ||
                    localTypeScopes.Any(scope => scope.Contains(alias))))
                    throw Error("E_DUPLICATE_TYPE: inferred aggregate alias already declared");
                if (!introduce && expected is null)
                    throw Error("E_UNKNOWN_TYPE: missing inferred aggregate alias");
                if (!introduce && expected!.Wrapper != wrapper)
                    throw Error("E_AGGREGATE_KIND: incompatible named aggregate");
            }
            string rowName = NamedAggregateIdentifier();
            if (scopes.Any(scope => scope.ContainsKey(rowName)) ||
                aggregateScopes.Any(scope => scope.ContainsKey(rowName)))
                throw Error("Duplicate aggregate binding");
            Expect("=");
            string close = wrapper == "struct" ? "}" : ")";
            Expect(wrapper == "struct" ? "{" : "(");
            var plan = new ClosedAggregateSpreadPlan(wrapper == "struct"
                ? ClosedAggregateKind.ExactStruct : ClosedAggregateKind.ReadonlyRecord);
            var values = new Dictionary<string, StructuredExpr>(StringComparer.Ordinal);
            do
            {
                if (values.Count >= 64) throw Error("AGGREGATE_FIELD_BUDGET");
                int offset = Current.Offset;
                string field = wrapper == "Tuple"
                    ? values.Count.ToString(CultureInfo.InvariantCulture) : NamedAggregateIdentifier();
                if (wrapper != "Tuple") Expect(wrapper == "struct" ? ":" : "=");
                if (values.ContainsKey(field)) throw Error("E_DUPLICATE_FIELD: duplicate aggregate field");
                var expr = Expr(depth + 1);
                if (expr.Type is not (IrValueType.Bool or IrValueType.I64))
                    throw Error("E_TYPE_INFER_AMBIGUOUS: only bool/int constant fields admitted");
                long value;
                try { value = ConstantValue(expr); }
                catch (OverflowException) { throw Error("Aggregate const field overflow"); }
                catch (DivideByZeroException) { throw Error("Aggregate const field division by zero"); }
                plan.AddNamed(field, expr.Type == IrValueType.Bool ? "bool" : "int");
                values.Add(field, expr.Type == IrValueType.Bool
                    ? new StructuredBool(offset, value != 0) : new StructuredInt(offset, value));
            } while (Take(","));
            Expect(close);
            Expect(";");
            var shape = plan.InferShape();
            if (!introduce && expected is not null &&
                !shape.Fields.SequenceEqual(expected.Shape.Fields))
                throw Error("E_TYPE_MISMATCH: named aggregate shape differs");
            if (introduce)
            {
                localTypeScopes[^1].Add(alias!);
                namedAggregateScopes[^1].Add(alias!, new NamedAggregateType(wrapper, shape));
            }
            aggregateScopes[^1].Add(rowName, new StaticAggregate(shape,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, StructuredExpr>(values),
                wrapper));
            return (new StructuredLet(at, "$aggregate_" + aggregateSentinels++, IrValueType.I64,
                new StructuredInt(at, 0), StructuredBindingKind.Const), false);
        }

        private (StructuredStmt Statement, bool Returns) Statement(int depth, bool outerElse = false, bool keywordArm = false)
        {
            Budget(depth);
            int at = Current.Offset;
            if (StartsNamedAggregate()) return NamedAggregate(depth, at);
            if (Current.Text is "class" or "actor" ||
                (Current.Text == "define" && index + 1 < tokens.Count &&
                 (tokens[index + 1].Text is "class" or "actor")))
                throw new FormatException(
                    $"Local class/actor declarations are not permitted; use a local struct/record value type or a top-level nominal type at offset {at}");
            if (Current.Text is "struct" or "record")
            {
                var kind = Current.Text == "struct"
                    ? StructuredLocalTypeKind.Struct
                    : StructuredLocalTypeKind.Record;
                index++;
                int nameAt = Current.Offset;
                string name = Name();
                if (localTypeScopes.Any(scope => scope.Contains(name)))
                    throw new FormatException(
                        $"Duplicate local value type '{name}' at offset {nameAt}");
                Expect("{");
                var fields = new List<StructuredLocalField>();
                var fieldNames = new HashSet<string>(StringComparer.Ordinal);
                while (Current.Text != "}")
                {
                    if (Current.Text.Length == 0)
                        throw Error("Unterminated local value type");
                    if (fields.Count >= MaxLocalTypeFields)
                        throw Error("Local value type field budget exceeded");
                    var fieldType = Type();
                    int fieldAt = Current.Offset;
                    string fieldName = Name();
                    if (!fieldNames.Add(fieldName))
                        throw new FormatException(
                            $"Duplicate local value-type field '{fieldName}' at offset {fieldAt}");
                    fields.Add(new StructuredLocalField(fieldName, fieldType));
                    if (Take(";") || Take(",")) continue;
                    if (Current.Text != "}")
                        throw Error("Expected ',', ';' or '}' after local value-type field");
                }
                Expect("}");
                Take(";");
                localTypeScopes[^1].Add(name);
                return (new StructuredLocalTypeDecl(at, name, kind,
                    Array.AsReadOnly(fields.ToArray())), false);
            }
            // Immutable runtime-valued rows have private scalar SSA snapshots.
            // 'const' retains its compile-time-constant requirement below.
            if (Current.Text == "val" && index + 3 < tokens.Count &&
                tokens[index + 2].Text == "=" &&
                (tokens[index + 3].Text is "infer" or "record"))
            {
                Expect("val");
                int nameAt = Current.Offset;
                string rowName = Name();
                if (scopes.Any(scope => scope.ContainsKey(rowName)) ||
                    aggregateScopes.Any(scope => scope.ContainsKey(rowName)))
                    throw new FormatException($"Duplicate aggregate binding at offset {nameAt}");
                Expect("=");
                Take("infer");
                ClosedAggregateKind aggregateKind;
                if (Take("struct")) aggregateKind = ClosedAggregateKind.ExactStruct;
                else if (Take("record")) aggregateKind = ClosedAggregateKind.ReadonlyRecord;
                else throw Error("val aggregate requires inline struct/record");
                var plan = new ClosedAggregateSpreadPlan(aggregateKind);
                var values = new Dictionary<string, StructuredExpr>(StringComparer.Ordinal);
                var writes = new List<StructuredAggregateScalarWrite>();
                int elements = 0;
                Expect("{");
                while (Current.Text != "}")
                {
                    if (++elements > 1024)
                        throw Error("AGGREGATE_SPREAD_PLAN_BUDGET");
                    if (Current.Text.Length == 0)
                        throw Error("Unterminated runtime aggregate");
                    if (Current.Text == ".")
                    {
                        Expect("."); Expect("."); Expect(".");
                        int sourceAt = Current.Offset;
                        string sourceName = Name();
                        var source = FindAggregate(sourceName);
                        if (source is null)
                            throw new FormatException(
                                $"Aggregate spread requires an immutable earlier row at offset {sourceAt}");
                        if (source.Wrapper == "Tuple") throw Error("Tuple cannot spread into a named row");
                        plan.AddCheckedSpread(source.Shape);
                        // Iterate canonical checked source shape, never a dynamic dictionary.
                        foreach (var field in source.Shape.Fields)
                        {
                            if (!source.Values.TryGetValue(field.Name, out var projected))
                                throw Error("Aggregate spread source shape mismatch");
                            string place = "$atto_row_" + aggregateSentinels++;
                            writes.Add(new StructuredAggregateScalarWrite(place,
                                projected.Type, projected));
                            values[field.Name] = new StructuredRead(sourceAt, place, projected.Type);
                        }
                    }
                    else
                    {
                        int fieldAt = Current.Offset;
                        string name = Name();
                        Expect(":");
                        var field = Expr(depth + 1);
                        string type = field.Type == IrValueType.Bool ? "bool" : "int";
                        plan.AddNamed(name, type);
                        string place = "$atto_row_" + aggregateSentinels++;
                        writes.Add(new StructuredAggregateScalarWrite(place,
                            field.Type, field));
                        values[name] = new StructuredRead(fieldAt, place, field.Type);
                    }
                    if (!Take(",")) break;
                }
                Expect("}");
                Expect(";");
                var shape = plan.InferShape();
                if (shape.Fields.Count != values.Count)
                    throw Error("Aggregate final runtime field count mismatch");
                foreach (var field in shape.Fields)
                    if (!values.TryGetValue(field.Name, out var projected) ||
                        projected.Type != (field.Type == "bool"
                            ? IrValueType.Bool : IrValueType.I64))
                        throw Error("Aggregate runtime field type mismatch");
                aggregateScopes[^1].Add(rowName, new StaticAggregate(shape,
                    new System.Collections.ObjectModel.ReadOnlyDictionary<string, StructuredExpr>(values)));
                return (new StructuredAggregateRuntimeLet(at,
                    Array.AsReadOnly(writes.ToArray())), false);
            }
            // Experimental constant-only aggregate execution. The full
            // source is typechecked; all scalar fields are compile-time values
            // and every projection is rewritten to a typed scalar literal.
            // The shipping one-function CIL parser remains fail-closed.
            if (Current.Text == "const" &&
                index + 3 < tokens.Count &&
                tokens[index + 2].Text == "=" &&
                (tokens[index + 3].Text is "infer" or "record"))
            {
                index++; // const
                int nameAt = Current.Offset;
                string rowName = Name();
                if (scopes.Any(scope => scope.ContainsKey(rowName)) ||
                    aggregateScopes.Any(scope => scope.ContainsKey(rowName)))
                    throw new FormatException($"Duplicate aggregate binding at offset {nameAt}");
                Expect("=");
                Take("infer");
                ClosedAggregateKind aggregateKind;
                if (Take("struct")) aggregateKind = ClosedAggregateKind.ExactStruct;
                else if (Take("record")) aggregateKind = ClosedAggregateKind.ReadonlyRecord;
                else throw Error("infer requires an inline struct or readonly record");
                var plan = new ClosedAggregateSpreadPlan(aggregateKind);
                var values = new Dictionary<string, StructuredExpr>(StringComparer.Ordinal);
                int elementCount = 0;
                Expect("{");
                while (Current.Text != "}")
                {
                    if (++elementCount > 1024)
                        throw Error("AGGREGATE_SPREAD_PLAN_BUDGET");
                    if (Current.Text.Length == 0) throw Error("Unterminated inferred aggregate");
                    if (Current.Text == ".")
                    {
                        Expect("."); Expect("."); Expect(".");
                        int offset = Current.Offset;
                        string sourceName = Name();
                        var origin = FindAggregate(sourceName);
                        if (origin is null)
                            throw new FormatException(
                                $"Aggregate spread requires a previous immutable closed local at offset {offset}");
                        if (origin.Wrapper == "Tuple")
                            throw Error("Tuple cannot spread into a named row");
                        // A const value requires every spread field to be a
                        // compile-time scalar. A runtime val row may only be
                        // spread into another runtime val aggregate.
                        if (origin.Values.Values.Any(field =>
                            field is not StructuredInt and not StructuredBool))
                            throw Error("const aggregate spread requires compile-time constant source");
                        plan.AddCheckedSpread(origin.Shape);
                        foreach (var field in origin.Values)
                            values[field.Key] = field.Value;
                    }
                    else
                    {
                        int offset = Current.Offset;
                        string fieldName = Name();
                        Expect(":");
                        var expr = Expr(depth + 1);
                        long constant;
                        try { constant = ConstantValue(expr); }
                        catch (OverflowException)
                        {
                            throw new FormatException(
                                $"Aggregate const field overflow at offset {offset}");
                        }
                        catch (DivideByZeroException)
                        {
                            throw new FormatException($"Aggregate const field division by zero at offset {offset}");
                        }
                        string fieldType = expr.Type == IrValueType.Bool ? "bool" : "int";
                        plan.AddNamed(fieldName, fieldType);
                        values[fieldName] = expr.Type == IrValueType.Bool
                            ? new StructuredBool(offset, constant != 0)
                            : new StructuredInt(offset, constant);
                    }
                    if (!Take(",")) break;
                }
                Expect("}");
                Expect(";");
                var shape = plan.InferShape();
                if (shape.Fields.Count != values.Count)
                    throw Error("Aggregate final field count differs from checked shape");
                foreach (var field in shape.Fields)
                {
                    if (!values.TryGetValue(field.Name, out var actual) ||
                        (field.Type == "bool" ? IrValueType.Bool : IrValueType.I64) != actual.Type)
                        throw Error("Aggregate inferred field type mismatch");
                }
                aggregateScopes[^1].Add(rowName, new StaticAggregate(shape,
                    new System.Collections.ObjectModel.ReadOnlyDictionary<string,StructuredExpr>(
                        values)));
                // No runtime aggregate object exists in this restricted slice.
                // Emit a compiler-private inert local so CFG ordering remains
                // structural; it cannot be referenced by source identifiers.
                var placeholder = "$aggregate_" + aggregateSentinels++;
                return (new StructuredLet(at, placeholder, IrValueType.I64,
                    new StructuredInt(at, 0), StructuredBindingKind.Const), false);
            }
            if (Take("guard"))
            {
                // Both forms use the exact same checked CFG and type rules.
                // A successful guard falls through; a failed one must exit.
                var condition = Expr(depth + 1);
                Require(condition, IrValueType.Bool, "E_GUARD: condition must be bool");
                // The comma-separated contract is left-to-right and lazy.
                // The existing LogicalAnd CFG lowers RHS into a guarded
                // branch; never eagerly evaluate an unproven later test.
                while (Take(","))
                {
                    var next = Expr(depth + 1);
                    Require(next, IrValueType.Bool, "E_GUARD: each condition must be bool");
                    condition = CheckedBinary(at, StructuredOperator.LogicalAnd,
                        condition, next, IrValueType.Bool);
                }
                var inverted = CheckedBinary(at, StructuredOperator.EqualBool,
                    condition, new StructuredBool(at, false), IrValueType.Bool);
                IReadOnlyList<StructuredStmt> failure;
                bool returns;
                // Guard failure explicitly targets the callable, unlike ordinary
                // task and match-arm returns. The verified scalar/tagged CFG
                // has no admitted defer or resource-loan cleanup to skip.
                var previousLocalReturn = localReturn;
                localReturn = null;
                guardFailureDepth++;
                try
                {
                    if (Current.Text == "{")
                    {
                        (failure, returns) = Block(depth + 1);
                        if (!outerElse && Current.Text == "else")
                            throw Error("E_GUARD: a brace guard cannot also have an else/end arm");
                    }
                    else if (Take("else"))
                        (failure, returns) = GuardKeywordArm(depth + 1);
                    else
                        throw Error("E_GUARD: expected '{ ... }' or 'else ... end'");
                }
                finally
                {
                    guardFailureDepth--;
                    localReturn = previousLocalReturn;
                }
                if (!returns) throw Error("E_GUARD: failure block must return on every path");
                // A guard does not itself terminate the success continuation.
                return (new StructuredIf(at, inverted, failure, null), false);
            }
            if (Take("return") || Take("^"))
            {
                if (localReturn is { } scope) return (ReadLocalExit(at, depth, scope), true);
                if (activeReturnType == IrValueType.Void)
                {
                    Take("void");
                    if (Current.Text != ";")
                        throw Error("RETURN_VOID_VALUE: void function cannot return a value");
                    Expect(";");
                    return (new StructuredReturnVoid(at), true);
                }
                if (Current.Text == ";")
                    throw Error("RETURN_VALUE_REQUIRED: non-void function requires a value");
                var value = ExpectedExpr(depth + 1, activeReturnType);
                Require(value, activeReturnType,
                    "Return value does not match function result type");
                Expect(";");
                return (new StructuredReturn(at, value), true);
            }
            // An unqualified 'mut' is neither a declaration kind nor an
            // implicit 'const mut'. Keep this error distinct from syntax
            // errors and from the unsupported owned const-mut lowering.
            if (Current.Text == "mut")
                throw new FormatException(
                    $"E_MUT_QUALIFIER: bare mut needs const, val or let at offset {Current.Offset}");
            if (Current.Text is "throw" or "throw!" or "panic" or "panic!" or "raise" or "raise!")
            {
                if (guardFailureDepth != 0)
                    throw Error("E_GUARD: exceptional exit does not replace a guard return");
                var effect = Current.Text switch
                {
                    "throw" or "throw!" => CfgGuestEffect.Throw,
                    "panic" or "panic!" => CfgGuestEffect.Panic,
                    _ => CfgGuestEffect.Raise
                };
                index++;
                var value = Expr(depth + 1);
                Require(value, IrValueType.GuestError, $"{effect.ToString().ToLowerInvariant()} requires Error");
                Expect(";");
                return (new StructuredSignal(at, value, effect), true);
            }
            if (Current.Text == "defer" && lexicalDeferScope == scopes.Count &&
                inlineTaskDepth == 1 && trapOperandDepth == 1 && guardFailureDepth == 0)
            {
                index++;
                if (Current.Text is "stdio" or "process")
                {
                    if (!allowDeferredStdout)
                        throw Error("E_LEXICAL_CLEANUP_UNSUPPORTED: E_PERMISSION: deferred stdout requires explicit host compile grant");
                    string receiver = Current.Text;
                    // Never reinterpret a lexical binding as a privileged process endpoint.
                    if (scopes.Any(scope => scope.ContainsKey(receiver)) ||
                        aggregateScopes.Any(scope => scope.ContainsKey(receiver)) ||
                        localTypeScopes.Any(scope => scope.Contains(receiver)) ||
                        namedAggregateScopes.Any(scope => scope.ContainsKey(receiver)) ||
                        visible.ContainsKey(receiver) || frozenNamespaceBindings.ContainsKey(receiver))
                        throw Error("E_LEXICAL_CLEANUP_UNSUPPORTED: deferred stdout requires an unshadowed endpoint");
                    bool processStdout = Current.Text == "process";
                    Expect(processStdout ? "process" : "stdio"); Expect("."); Expect(processStdout ? "stdout" : "out"); Expect("."); Expect("write"); Expect("(");
                    string literal = ErrorMessage();
                    if (literal.Length > 256 || literal.Any(ch => ch < ' ' || ch > '~'))
                        throw Error("E_LEXICAL_CLEANUP_UNSUPPORTED: deferred stdout slice accepts bounded printable ASCII literals");
                    Expect(")"); Expect(";");
                    return (new StructuredLexicalDefer(at, new StructuredDeferredStdout(at, literal)), false);
                }
                // The first cleanup slice accepts only direct scalar assignments
                // at task-root scope; no closure, call, nested registration or IO.
                if (index + 1 >= tokens.Count || tokens[index + 1].Text != "=")
                    throw Error("E_LEXICAL_CLEANUP_UNSUPPORTED: expected nonthrowing scalar assignment");
                var (cleanup, terminal) = Statement(depth + 1);
                if (terminal || cleanup is not StructuredSet set || !TrapInlinePure(set.Value))
                    throw Error("E_LEXICAL_CLEANUP_UNSUPPORTED: expected nonthrowing scalar assignment");
                return (new StructuredLexicalDefer(at, set), false);
            }
            if ((localReturn is not null || guardFailureDepth != 0) && Current.Text is ("defer" or "using"))
                throw Error("E_LEXICAL_CLEANUP_UNSUPPORTED: resource cleanup requires a verified unwind continuation");
            if (Take("try"))
            {
                if (localReturn is not null)
                    throw Error("E_LOCAL_TRY_RETURN_UNSUPPORTED: try/catch return requires a lexical continuation");
                if (activeReturnType != IrValueType.I64)
                    throw Error("Native Error catch v1 supports only int-returning functions");
                Expect("{");
                Expect("return");
                var protectedValue = Expr(depth + 1);
                Require(protectedValue, IrValueType.I64,
                    "Protected Error catch return must be int");
                Expect(";");
                Expect("}");
                Expect("catch");
                Expect("(");
                int bindingAt = Current.Offset;
                string catchBinding = Name();
                if (scopes.Any(scope => scope.ContainsKey(catchBinding)))
                    throw new FormatException(
                        $"Error catch catchBinding '{catchBinding}' shadows local at offset {bindingAt}");
                Expect(")");
                Expect("{");

                string? typeTest = null;
                long matchValue;
                long fallbackValue;
                if (Take("if"))
                {
                    if (Name() != catchBinding)
                        throw Error("Error type test must use catch catchBinding");
                    Expect("is");
                    Expect("type");
                    typeTest = Name();
                    if (typeTest is not ("ArithmeticOverflowError" or "Error"))
                        throw Error(
                            "Error catch v1 supports only ArithmeticOverflowError or Error");
                    Expect("{");
                    Expect("return");
                    var matched = Expr(depth + 1);
                    if (matched is not StructuredInt matchedInt)
                        throw Error("Error type-match return must be integer literal");
                    matchValue = matchedInt.Value;
                    Expect(";");
                    Expect("}");
                    Expect("else");
                    Expect("{");
                    Expect("return");
                    var fallback = Expr(depth + 1);
                    if (fallback is not StructuredInt fallbackInt)
                        throw Error("Error type-mismatch return must be integer literal");
                    fallbackValue = fallbackInt.Value;
                    Expect(";");
                    Expect("}");
                    Expect("fi");
                }
                else
                {
                    Expect("return");
                    var handled = Expr(depth + 1);
                    if (handled is not StructuredInt handledInt)
                        throw Error("Error catch v1 handler must be integer literal");
                    matchValue = handledInt.Value;
                    fallbackValue = handledInt.Value;
                    Expect(";");
                }
                Expect("}");
                Expect("finally");
                Expect("{");
                if (Current.Text != "}")
                    throw Error("Error catch v1 requires empty finally");
                Expect("}");
                return (new StructuredOverflowCatchReturn(
                    at, protectedValue, typeTest, matchValue, fallbackValue), true);
            }
            if ((Current.Text is "let" or "val" or "const") || StartsScalarTypeFirst())
            {
                // In the v1 scalar subset, a type-first local is exactly const:
                // both spellings need a statically evaluable initializer.
                // val is the fixed runtime binding; let is rebindable.
                bool typeFirstImmutable = StartsScalarTypeFirst();
                StructuredBindingKind kind = typeFirstImmutable
                    ? StructuredBindingKind.Const
                    : Current.Text switch
                    {
                        "let" => StructuredBindingKind.Let,
                        "val" => StructuredBindingKind.Val,
                        _ => StructuredBindingKind.Const
                    };
                if (!typeFirstImmutable && kind == StructuredBindingKind.Const &&
                    index + 1 < tokens.Count && tokens[index + 1].Text == "mut")
                    throw new FormatException(
                        $"E_CONST_MUT_UNSUPPORTED: mutable constant instances require owned object lowering at offset {at}");

                bool annotated;
                IrValueType type = IrValueType.I64;
                string name;
                int nameAt;
                if (typeFirstImmutable)
                {
                    annotated = true;
                    type = Type();
                    nameAt = Current.Offset;
                    name = Name();
                }
                else
                {
                    index++;
                    // Prefix forms admit const int x=e, const x:int=e, const x=e.
                    // A declaration's type is a check; it is not a cast.
                    annotated = StartsScalarTypeFirst() || Current.Text is "Option" or "Array" or "Result" or "Error";
                    if (annotated)
                    {
                        type = Type();
                        nameAt = Current.Offset;
                        name = Name();
                    }
                    else
                    {
                        nameAt = Current.Offset;
                        name = Name();
                        if (Take(":"))
                        {
                            type = Type();
                            annotated = true;
                        }
                    }
                }
                if (scopes.Any(scope => scope.ContainsKey(name)) ||
                    aggregateScopes.Any(scope => scope.ContainsKey(name)))
                    throw new FormatException($"Duplicate local binding at offset {nameAt}");
                Expect("=");
                // Only complete unconditional statement values publish
                // writes. Conditional expression joins remain fail-closed.
                var initializer = ReadStatementValue(depth + 1, annotated ? type : null,
                    allowWriteback: kind != StructuredBindingKind.Const && !commaBindingTerminator);
                if (annotated)
                    Require(initializer, type, "Local initializer has wrong type");
                else
                    type = initializer.Type; // No i64 fallback for bool or unsupported types.
                if (type == IrValueType.FixedArrayI64 && kind != StructuredBindingKind.Val)
                    throw Error("E_ARRAY_IMMUTABLE_ONLY: fixed Array<int> requires val; mutable borrow, resize and const folding are unsupported");
                long? constantValue = null;
                // Any const Option<int> fails closed, not only scalar-shaped initializers.
                if (kind == StructuredBindingKind.Const && type is not (IrValueType.I64 or IrValueType.Bool))
                    throw new FormatException(
                        $"E_OPTION_CONST_UNSUPPORTED: non-scalar const folding needs a typed proof at offset {at}");
                // Typed scalar const (type-first `int x=e`, `const int x=e`,
                // `const x:int=e`) must be compile-time evaluable (spec #49,
                // java #3066). Untyped `const x=e` keeps java's stage-one
                // meaning: folded when constant, otherwise a fixed readonly
                // runtime binding.
                if (kind == StructuredBindingKind.Const && annotated &&
                    !IsCompileTimeScalar(initializer))
                    throw new FormatException(
                        $"E_NON_CONST_INITIALIZER: typed const scalar initializer must be compile-time evaluable at offset {initializer.Offset}");
                if (kind == StructuredBindingKind.Const && IsCompileTimeScalar(initializer))
                {
                    try { constantValue = ConstantValue(initializer); }
                    catch (OverflowException)
                    {
                        throw new FormatException(
                            $"E_CONST_OVERFLOW: compile-time const overflow at offset {initializer.Offset}");
                    }
                    catch (DivideByZeroException)
                    {
                        throw new FormatException($"Compile-time const division by zero at offset {initializer.Offset}");
                    }
                    initializer = type == IrValueType.Bool
                        ? new StructuredBool(initializer.Offset, constantValue.Value != 0)
                        : new StructuredInt(initializer.Offset, constantValue.Value);
                }
                if (!commaBindingTerminator || Current.Text != ",") Expect(";");
                // Bind only after checking initializer (no self-reference).
                scopes[^1].Add(name, new LocalBinding(type, kind, constantValue));
                return (new StructuredLet(at, name, type, initializer, kind), false);
            }
            bool explicitDo = Take("do");
            if (Current.Text is "if" or "ifnot" or "unless")
            {
                // All three forms use the same typed CFG. Negation is checked
                // boolean equality to false, never arithmetic minus or truthiness.
                bool inverted = Current.Text != "if";
                index++;
                var condition = Expr(depth + 1);
                Require(condition, IrValueType.Bool, "If condition must be bool");
                if (inverted)
                    condition = CheckedBinary(at, StructuredOperator.EqualBool,
                        condition, new StructuredBool(at, false), IrValueType.Bool);
                // Preserve the legacy '; do ... fi' and brace spellings;
                // canonical 'then ... fi' may omit its optional semicolon.
                bool semicolon = Take(";");
                bool delimited = Take("then");
                if (strictConditionals && (semicolon || (delimited && !explicitDo)))
                    throw Error("E_CONDITIONAL_STATEMENT: use 'do if' (or do ifnot/unless) with then/fi, or a brace statement");
                if (explicitDo && !delimited)
                    throw Error("do if/ifnot/unless requires 'then'");
                // Suffix do is first-class in strict and migration profiles.
                if (!explicitDo && !delimited && !semicolon)
                    delimited = Take("do");
                // Preserve modern-main legacy if/ifnot/unless ... then ...
                // imperative acceptance; a separate spec migration must not
                // silently break older compiled source.
                if (semicolon && !delimited)
                {
                    Expect("do");
                    delimited = true;
                }
                IReadOnlyList<StructuredStmt> yes;
                bool yesReturns;
                if (delimited) (yes, yesReturns) = DelimitedArm(depth + 1, outerElse: true);
                else (yes, yesReturns) = Block(depth + 1);
                IReadOnlyList<StructuredStmt>? no = null;
                bool noReturns = false;
                // A completed brace arm leaves an enclosing keyword else/fi
                // untouched. A braced else must itself begin with a brace.
                if ((delimited || !keywordArm ||
                        (index + 1 < tokens.Count && tokens[index + 1].Text == "{")) && Take("else"))
                {
                    if (delimited) (no, noReturns) = DelimitedArm(depth + 1);
                    else (no, noReturns) = Block(depth + 1);
                }
                if (delimited) Expect("fi");
                else if (!keywordArm && Take("fi") && strictConditionals)
                    throw Error("E_CONDITIONAL_STATEMENT: brace statements must not end with fi"); // Legacy entry points retain the optional fi.
                return (new StructuredIf(at, condition, yes, no),
                    yesReturns && noReturns);
            }
            if (Current.Text == "rt" && tokens[index + 1].Text == "cooperate")
            {
                if (inlineTaskDepth == 0)
                    throw Error("E_COOPERATE_CONTEXT: this profile admits same-thread cooperation inside sync task");
                index += 2;
                Expect(";");
                return (new StructuredInlineCooperate(at), false);
            }
            if (explicitDo && Take("sync"))
            {
                Expect("task");
                var task = ReadInlineTask(depth + 1, imperative: true);
                Take(";");
                return (new StructuredDoSyncTask(at, task), task.ExitsScope);
            }
            if (explicitDo && Current.Text == "match")
            {
                if (guardFailureDepth != 0)
                    throw Error("E_GUARD: imperative match inside a failure arm requires verified callable-return analysis");
                Expect("match");
                var match = ReadMatch(depth + 1, imperative: true);
                return (new StructuredDoMatch(at, match), !match.Completes);
            }
            if (Current.Text == "match")
                throw Error("MATCH_VALUE_REQUIRED: use match as a value or do match as a statement");
            if (explicitDo)
                throw Error("Expected if, ifnot, unless, match or sync task after 'do'");
            if (AtSyncTask())
                throw Error("E_SYNTAX: a standalone sync task requires do");
            if (Take("break"))
            {
                // The existing block terminal flag includes loop transfers.
                // They cannot prove a callable return for a guard failure.
                if (guardFailureDepth != 0)
                    throw Error("E_GUARD: loop transfer does not replace a guard return");
                // A loop opened inside the current lexical scope owns its
                // break/continue edges. Never jump to a pre-existing outer loop.
                if (localReturn is { } lexical && loopDepth <= lexical.LoopFloor)
                    throw Error(lexical.Diagnostic == "MATCH_VOID_ARM"
                        ? "E_MATCH_LOOP_TRANSFER_UNSUPPORTED: lexical match exit needs a verified join"
                        : "E_SYNC_TASK_LOOP_TRANSFER_UNSUPPORTED: loop transfer needs a verified lexical continuation");
                if (loopDepth == 0) throw Error("break requires an enclosing loop");
                Expect(";");
                return (new StructuredBreak(at), true);
            }
            if (Take("continue"))
            {
                // The existing block terminal flag includes loop transfers.
                // They cannot prove a callable return for a guard failure.
                if (guardFailureDepth != 0)
                    throw Error("E_GUARD: loop transfer does not replace a guard return");
                // A loop opened inside the current lexical scope owns its
                // break/continue edges. Never jump to a pre-existing outer loop.
                if (localReturn is { } lexical && loopDepth <= lexical.LoopFloor)
                    throw Error(lexical.Diagnostic == "MATCH_VOID_ARM"
                        ? "E_MATCH_LOOP_TRANSFER_UNSUPPORTED: lexical match exit needs a verified join"
                        : "E_SYNC_TASK_LOOP_TRANSFER_UNSUPPORTED: loop transfer needs a verified lexical continuation");
                if (loopDepth == 0) throw Error("continue requires an enclosing loop");
                Expect(";");
                return (new StructuredContinue(at), true);
            }
            if (Take("while") || Take("loop"))
            {
                bool unconditional = tokens[index - 1].Text == "loop";
                StructuredExpr condition = new StructuredBool(at, true);
                if (!unconditional)
                {
                    condition = Expr(depth + 1);
                    Require(condition, IrValueType.Bool, "While condition must be bool");
                }
                if (loopDepth >= MaxDepth) throw Error("Loop nesting limit exceeded");
                loopDepth++;
                IReadOnlyList<StructuredStmt> body;
                try { (body, _) = Block(depth + 1); }
                finally { loopDepth--; }
                // Even a constant-true loop is not accepted as a return proof.
                return (new StructuredWhile(at, condition, body), false);
            }
            int variableAt = Current.Offset;
            string variable = Name();
            var binding = Lookup(variable, variableAt);
            if (binding.Kind != StructuredBindingKind.Let)
                throw new FormatException(
                    $"E_IMMUTABLE_REBIND: Cannot reassign {binding.Kind.ToString().ToLowerInvariant()} binding '{variable}' at offset {variableAt}");
            if (inlineValueBindingFloor is int floor && scopes.Take(floor).Any(scope => scope.ContainsKey(variable)))
                throw Error("E_SYNC_TASK_WRITEBACK_UNSUPPORTED: value-context sync task cannot yet assign a surrounding local");
            bool compoundXor = Take("^^=");
            if (!compoundXor) Expect("=");
            if (compoundXor && binding.Type != IrValueType.I64)
                throw Error("Compound XOR requires a mutable int local");
            // A compound RHS must not acquire the special complete-statement
            // writeback permission of an ordinary assignment. Preserve the
            // existing lexical task, suspension and ownership gates.
            var assigned = ReadStatementValue(depth + 1, binding.Type, allowWriteback: !compoundXor);
            Require(assigned, binding.Type, "Assignment has wrong type");
            if (compoundXor)
                assigned = CheckedBinary(variableAt, StructuredOperator.BitXor,
                    new StructuredRead(variableAt, variable, IrValueType.I64), assigned, IrValueType.I64);
            Expect(";");
            return (new StructuredSet(at, variable, assigned), false);
        }
        // The compatibility spelling remains a contextual type name. A value
        // named boolean followed by = or : retains its existing interpretation.
        private bool StartsScalarTypeFirst() => Current.Text is "int" or "bool" ||
            (Current.Text == "boolean" && index + 1 < tokens.Count &&
             tokens[index + 1].Text is { Length: > 0 } next &&
             PortableUnicodeIdentifier.StartWidth(next, 0) != 0);

        private IrValueType Type()
        {
            if (Current.Text == "Error" && linking is null)
            {
                CoreTypeRegistry.Require("Error", 0);
                index++;
                return IrValueType.GuestError;
            }
            if (linking is null && Take("Result"))
            {
                CoreTypeRegistry.Require("Result", 2);
                // Parse before erasure, so a disallowed error type or payload
                // cannot accidentally select an existing generated ABI.
                Expect("<");
                if (Current.Text is not ("int" or "Option"))
                    throw Error("E_RESULT_PAYLOAD_ABI_UNSUPPORTED: expected int or Option<int>");
                var payload = Type();
                Expect(",");
                IrValueType result;
                if (Take("Error")) result = GuestResultTypes.Wrap(payload);
                else if (payload == IrValueType.I64 && (Take("String") || Take("string")))
                {
                    result = IrValueType.ResultI64Text;
                    if (Take("|"))
                    {
                        Expect("Error");
                        result = IrValueType.ResultI64TextOrError;
                    }
                }
                else throw Error("E_RESULT_ERROR_ABI_UNSUPPORTED: expected Error or a supported String error");
                Expect(">");
                if (payload is not (IrValueType.I64 or IrValueType.OptionI64))
                    throw Error("E_RESULT_PAYLOAD_ABI_UNSUPPORTED: expected int or Option<int>");
                return result;
            }
            // Every builtin type lookup is checked against the canonical
            // registry before backend-specific AST/SSA admission. A catalog
            // entry alone never grants a backend lowering.
            if (Current.Text is { Length: > 0 } typeName &&
                CoreTypeRegistry.Lookup(typeName) is { Kind: CoreTypeRegistry.Kind.Type } typeInfo &&
                !typeInfo.IsExecutableInStructuredNet)
                throw new FormatException(
                    $"E_CORE_TYPE_LOWERING_UNSUPPORTED: {typeInfo.CanonicalIdentity} has no verified .NET structured/NativeAOT lowering at offset {Current.Offset}");
            if (Take("int"))
            {
                CoreTypeRegistry.RequireExecutableStructuredNet("int", 0);
                if (!Take("?")) return IrValueType.I64;
                if (Current.Text == "?")
                    throw Error("E_OPTION_NESTED_UNSUPPORTED: int?? requires verified nested Option ABI");
                return IrValueType.OptionI64;
            }
            if (Take("bool") || Take("boolean"))
            {
                CoreTypeRegistry.RequireExecutableStructuredNet("bool", 0);
                if (Take("?"))
                    throw Error("E_OPTION_I64_ONLY: bool? requires Option<bool> lowering");
                return IrValueType.Bool;
            }
            if (Take("Array"))
            {
                CoreTypeRegistry.RequireExecutableStructuredNet("Array", 1);
                Expect("<");
                if (!Take("int"))
                    throw Error("E_ARRAY_I64_ONLY: fixed arrays support only Array<int>");
                if (Current.Text == ",")
                    throw new FormatException(
                        $"E_CORE_TYPE_ARITY: {CoreTypeRegistry.Lookup("Array")!.CanonicalIdentity} expects {CoreTypeRegistry.Lookup("Array")!.MinGenericArity} type argument at offset {Current.Offset}");
                Expect(">");
                return IrValueType.FixedArrayI64;
            }
            if (Take("Option"))
            {
                CoreTypeRegistry.RequireExecutableStructuredNet("Option", 1);
                Expect("<");
                if (Current.Text == ">")
                    throw new FormatException(
                        $"E_CORE_TYPE_ARITY: {CoreTypeRegistry.Lookup("Option")!.CanonicalIdentity} requires one type argument at offset {Current.Offset}");
                if (!Take("int"))
                    throw Error("E_OPTION_I64_ONLY: this NativeAOT slice admits only Option<int>; no nested or erased generic Option");
                if (Current.Text == ",")
                    throw new FormatException(
                        $"E_CORE_TYPE_ARITY: {CoreTypeRegistry.Lookup("Option")!.CanonicalIdentity} requires one type argument at offset {Current.Offset}");
                Expect(">");
                if (Take("?"))
                    throw Error("E_OPTION_NESTED_UNSUPPORTED: Option<int>? requires nested Option ABI");
                return IrValueType.OptionI64;
            }
            throw Error("Expected int, bool or closed Option<int> type");
        }
        private string Name()
        {
            string name = Current.Text;
            int at = Current.Offset;
            // A catalogued prelude type this backend cannot lower fails as
            // unsupported (it is a use, not a forgery); lowered identities such
            // as Option/Some/None/int cannot be redeclared.
            if (CoreTypeRegistry.Lookup(name) is { Kind: CoreTypeRegistry.Kind.Type } unlowered &&
                !unlowered.IsExecutableInStructuredNet)
                throw new FormatException(
                    $"E_CORE_TYPE_LOWERING_UNSUPPORTED: {unlowered.CanonicalIdentity} has no verified .NET structured/NativeAOT lowering at offset {at}");
            if (CoreTypeRegistry.IsProtectedIdentity(name))
                throw new FormatException($"E_PRELUDE_SPOOF: {name} is a compiler-owned type/constructor identity at offset {at}");
            if (name.Length is < 1 or > MaxIdentifier ||
                !PortableUnicodeIdentifier.IsName(name) ||
                name.StartsWith(TrappedBodyPrefix, StringComparison.Ordinal) ||
                name is "if" or "ifnot" or "unless" or "else" or "while" or "loop" or "break" or "continue" or "return" or "let"
                    or "int" or "bool" or "true" or "false" or "and" or "or" or "xor"
                    or "define" or "pub" or "fnc" or "const" or "val" or "final"
                    or "struct" or "record" or "class" or "actor"
                    or "module" or "import" or "from" or "as" or "end"
                    or "do" or "fi" or "try" or "catch" or "finally"
                    or "is" or "type"
                    or "routine" or "async" or "isoactor" or "guard"
                    or "Some" or "None" or "Option" or "Array" or "trap" or "Result" or "Error"
                    or "Ok" or "Err" or "throw" or "raise" or "panic" or "new" or "match")
                throw new FormatException($"Invalid identifier at offset {at}");
            index++;
            return name;
        }
        private LocalBinding Lookup(string name, int at)
        {
            for (int i = scopes.Count - 1; i >= 0; i--)
                if (scopes[i].TryGetValue(name, out var binding)) return binding;
            throw new FormatException($"Undefined local '{name}' at offset {at}");
        }

        // Typed const and bare type-first scalars must be statically
        // evaluable; runtime calls/reads belong under val or let. Untyped
        // const folds only when this holds. Failed evaluation rejects source
        // before CFG/CIL emission.
        private static bool IsCompileTimeScalar(StructuredExpr expression) =>
            expression switch
            {
                StructuredInt or StructuredBool => true,
                StructuredNegate unary => IsCompileTimeScalar(unary.Value),
                StructuredBinary binary => IsCompileTimeScalar(binary.Left)
                                           && IsCompileTimeScalar(binary.Right),
                _ => false
            };

        // Java/Graal and LLVM admit shift distances 0..63 only; a const shift
        // outside that range fails at compile time rather than masking.
        private static int ConstantShiftDistance(long distance) =>
            distance is >= 0 and <= 63 ? (int)distance
                : throw new FormatException(
                    $"Shift distance {distance} is outside 0..63");

        private static long ConstantValue(StructuredExpr expression)
        {
            switch (expression)
            {
                case StructuredInt intLiteral: return intLiteral.Value;
                case StructuredBool boolLiteral: return boolLiteral.Value ? 1L : 0L;
                case StructuredNegate negate:
                    return checked(-ConstantValue(negate.Value));
                case StructuredConditional conditional:
                    return ConstantValue(conditional.Test) != 0
                        ? ConstantValue(conditional.WhenTrue)
                        : ConstantValue(conditional.WhenFalse);
                case StructuredBinary binary:
                    long left = ConstantValue(binary.Left);
                    if (binary.Operator == StructuredOperator.LogicalAnd && left == 0)
                        return 0;
                    if (binary.Operator == StructuredOperator.LogicalOr && left != 0)
                        return 1;
                    long right = ConstantValue(binary.Right);
                    return binary.Operator switch
                    {
                        StructuredOperator.Add => checked(left + right),
                        StructuredOperator.Subtract => checked(left - right),
                        StructuredOperator.Multiply => checked(left * right),
                        StructuredOperator.Divide => checked(left / right),
                        StructuredOperator.Remainder => Ores.Net.Runtime.StrictSigned64Arithmetic.Rem(left, right),
                        StructuredOperator.Modulo => Ores.Net.Runtime.StrictSigned64Arithmetic.Mod(left, right),
                        StructuredOperator.BitXor => left ^ right,
                        StructuredOperator.ShiftLeft => left << ConstantShiftDistance(right),
                        StructuredOperator.ShiftRightLogical => (long)((ulong)left >> ConstantShiftDistance(right)),
                        StructuredOperator.ShiftRightArithmetic => left >> ConstantShiftDistance(right),
                        StructuredOperator.EqualI64 => left == right ? 1L : 0L,
                        StructuredOperator.NotEqualI64 => left != right ? 1L : 0L,
                        StructuredOperator.LessThanI64 => left < right ? 1L : 0L,
                        StructuredOperator.GreaterThanI64 => left > right ? 1L : 0L,
                        StructuredOperator.LessThanOrEqualI64 => left <= right ? 1L : 0L,
                        StructuredOperator.GreaterThanOrEqualI64 => left >= right ? 1L : 0L,
                        StructuredOperator.EqualBool => left == right ? 1L : 0L,
                        StructuredOperator.NotEqualBool => left != right ? 1L : 0L,
                        StructuredOperator.LogicalAnd => left != 0 && right != 0 ? 1L : 0L,
                        StructuredOperator.LogicalOr => left != 0 || right != 0 ? 1L : 0L,
                        StructuredOperator.LogicalXor => (left != 0) ^ (right != 0) ? 1L : 0L,
                        _ => throw new FormatException(
                            $"Unsupported const operator at offset {binary.Offset}")
                    };
                default:
                    throw new FormatException(
                        $"const initializer must be a compile-time scalar at offset {expression.Offset}");
            }
        }
        // Only closed Option<int> inspection operations are admitted.
        // Eager unwrap_or evaluates its fallback exactly once, even for Some.
        // Unsafe .unwrap(), ?. chaining and implicit conversion are forbidden.
        private StructuredExpr OptionPostfix(StructuredExpr option, int depth)
        {
            if (option.Type != IrValueType.OptionI64 || !Take("."))
                return option;
            int at = Current.Offset;
            if (Take("is_some"))
            {
                Expect("(");
                Expect(")");
                return new StructuredOptionPresent(at, option);
            }
            if (Take("is_none"))
            {
                Expect("(");
                Expect(")");
                return new StructuredOptionAbsent(at, option);
            }
            if (Take("unwrap"))
            {
                Expect("(");
                Expect(")");
                return TypedNode(new StructuredOptionUnwrap(at, option), option);
            }
            if (Take("unwrap_or"))
            {
                Expect("(");
                var fallback = Expr(depth + 1);
                Require(fallback, IrValueType.I64, "Option<int>.unwrap_or fallback must be int");
                Expect(")");
                return new StructuredOptionOr(at, option, fallback);
            }
            throw Error("E_OPTION_METHOD_UNSUPPORTED: use is_some(), is_none(), unwrap(), or unwrap_or(int)");
        }
        // The structured NativeAOT subset admits only already-declared,
        // statically bound unary named helper functions. It deliberately
        // does NOT implement actor graphs, inline closures, partial calls,
        // asynchronous flattening, or an implicit dynamic fallback.
        // f <| g <| x is right associative and uses the same typed call CFG
        // as x |> g |> f, with no repeated evaluation of x.
        private StructuredExpr Expr(int depth)
        {
            Budget(depth);
            var reverse = new List<(string Name, int Offset)>();
            while (index + 1 < tokens.Count && tokens[index + 1].Text == "<|")
            {
                if (reverse.Count >= 64)
                    throw Error("E_VALUE_PIPE_BUDGET: reverse application exceeds 64 stages");
                int at = Current.Offset;
                string stage = Name();
                Expect("<|");
                reverse.Add((stage, at));
            }

            var value = Conditional(depth + 1);
            if (reverse.Count > 0 && Current.Text == "|>")
                throw Error("E_VALUE_PIPE_DIRECTION: parenthesize mixed <| and |> applications");
            for (int i = reverse.Count - 1; i >= 0; --i)
                value = ApplyValueStage(reverse[i].Name, reverse[i].Offset, value);

            int forwards = 0;
            while (Current.Text == "|>")
            {
                int at = Current.Offset;
                index++;
                if (++forwards > 64)
                    throw Error("E_VALUE_PIPE_BUDGET: forward application exceeds 64 stages");
                if (Take("await"))
                {
                    if (Current.Text is "(" or "<")
                        throw Error("E_VALUE_PIPE_STAGE: await takes only the left-hand value");
                    Require(value, IrValueType.I64, "E_SUSPENDS_UNSUPPORTED: immediate await requires int");
                    continue;
                }
                if (Take("match"))
                {
                    value = ReadMatchValue(at, depth + 1, value);
                    continue;
                }
                if (Current.Text is "if" or "ifnot" or "unless")
                {
                    bool inverted = Current.Text != "if";
                    index++;
                    var predicate = ReceiverPredicate(value, depth + 1);
                    Require(predicate, IrValueType.Bool, "Value if condition requires bool");
                    if (inverted)
                        predicate = CheckedBinary(at, StructuredOperator.EqualBool,
                            predicate, new StructuredBool(at, false), IrValueType.Bool);
                    Expect("then");
                    var yes = ReadConditionalStatementArm(depth + 1, false, "else");
                    Expect("else");
                    var no = ReadConditionalStatementArm(depth + 1, false, "fi");
                    Expect("fi");
                    Require(no, yes.Type, "Value if result types differ");
                    value = CheckedConditional(at, predicate, yes, no);
                    continue;
                }
                if (Current.Text == "?" || ReceiverPredicateStarts())
                {
                    value = ReceiverPredicate(value, depth + 1);
                    if (Take("?"))
                    {
                        Require(value, IrValueType.Bool, "Conditional test requires bool");
                        var yes = ReadTernaryArm(depth + 1, false, ":");
                        Expect(":");
                        var no = ReadTernaryArm(depth + 1, false, ";", conditionalOnly: true);
                        Require(no, yes.Type, "Conditional result types differ");
                        value = CheckedConditional(at, value, yes, no);
                    }
                    continue;
                }
                string name = Name();
                value = ApplyValueStage(name, at, value);
            }
            if (Current.Text == "<|")
                throw Error("E_VALUE_PIPE_DIRECTION: parenthesize mixed |> and <| applications");
            if (Current.Text == "^")
                throw Error("Reserved AttoLang operator '^' in expressions; use Integer.bitXor(a, b)");
            return value;
        }

        private bool ReceiverPredicateStarts() =>
            Current.Text is "is" or "<" or ">" or "<=" or ">=" or "eq" or "==" or "neq" or "!=";

        private StructuredExpr ReceiverPredicate(StructuredExpr input, int depth)
        {
            Budget(depth);
            int at = Current.Offset;
            if (Take("is"))
            {
                Take("type");
                Expect("int");
                Require(input, IrValueType.I64, "Receiver int type test requires proven int");
                // Preserve evaluation of the operand even for an always-true
                // checked scalar test; never erase a potentially failing call.
                return CheckedBinary(at, StructuredOperator.GreaterThanOrEqualI64,
                    input, new StructuredInt(at, long.MinValue), IrValueType.Bool);
            }
            if (!ReceiverPredicateStarts()) return input;
            string op = Current.Text;
            index++;
            bool equality = op is "eq" or "==" or "neq" or "!=";
            var right = equality ? Compare(depth + 1) : Shift(depth + 1);
            if (equality)
            {
                Require(right, input.Type, "Equality requires matching operands");
                if (input.Type is not (IrValueType.I64 or IrValueType.Bool))
                    throw Error("Receiver equality requires int or bool");
                bool neq = op is "neq" or "!=";
                var comparison = input.Type == IrValueType.Bool
                    ? (neq ? StructuredOperator.NotEqualBool : StructuredOperator.EqualBool)
                    : (neq ? StructuredOperator.NotEqualI64 : StructuredOperator.EqualI64);
                return CheckedBinary(at, comparison, input, right, IrValueType.Bool);
            }
            Require(input, IrValueType.I64, "Comparison requires int operands");
            Require(right, IrValueType.I64, "Comparison requires int operands");
            return CheckedBinary(at, op switch
            {
                "<" => StructuredOperator.LessThanI64,
                ">" => StructuredOperator.GreaterThanI64,
                "<=" => StructuredOperator.LessThanOrEqualI64,
                ">=" => StructuredOperator.GreaterThanOrEqualI64,
                _ => throw Error("Unrecognized receiver comparison")
            }, input, right, IrValueType.Bool);
        }

        private StructuredExpr ApplyValueStage(string name, int at, StructuredExpr input)
        {
            if (scopes.Any(scope => scope.ContainsKey(name)))
                throw new FormatException(
                    $"E_VALUE_PIPE_STAGE: local '{name}' is not a proven callable at offset {at}");
            if (!visible.TryGetValue(name, out var target))
                throw new FormatException(
                    $"E_VALUE_PIPE_STAGE: named helper '{name}' is undefined or not visible at offset {at}");
            if (target.Parameters.Count != 1)
                throw new FormatException(
                    $"E_VALUE_PIPE_ARITY: '{name}' must have exactly one typed parameter at offset {at}");
            Require(input, target.Parameters[0].Type, "Value-pipeline stage input type mismatch");
            int height = Height(input) + 1;
            if (height > MaxDepth)
                throw new FormatException($"Value-pipeline AST depth exceeds limit at offset {at}");
            var call = new StructuredCall(at, target.Name,
                Array.AsReadOnly(new StructuredExpr[] { input }), target.ResultType);
            expressionHeights.Add(call, height);
            return call;
        }
        private StructuredExpr Conditional(int depth)
        {
            Budget(depth);
            int expressionStart = Current.Offset;
            if (Current.Text is "if" or "ifnot" or "unless")
            {
                int valueIfAt = Current.Offset;
                bool writebackRoot = conditionalWritebackRoot == valueIfAt;
                bool inverted = Current.Text is "ifnot" or "unless";
                index++;
                var predicate = Expr(depth + 1);
                Require(predicate, IrValueType.Bool, "Value if condition requires bool");
                if (inverted)
                    predicate = CheckedBinary(valueIfAt, StructuredOperator.EqualBool,
                        predicate, new StructuredBool(valueIfAt, false), IrValueType.Bool);
                Expect("then");
                if (Current.Text is "break" or "continue" or "return")
                    throw Error("Value if branch must produce a value; control transfers require 'do if'");
                var whenTrue = ReadConditionalStatementArm(depth + 1, writebackRoot, "else");
                Expect("else");
                if (Current.Text is "break" or "continue" or "return")
                    throw Error("Value if branch must produce a value; control transfers require 'do if'");
                var whenFalse = ReadConditionalStatementArm(depth + 1, writebackRoot, "fi");
                Expect("fi");
                if (whenTrue.Type != whenFalse.Type)
                    throw Error("Value if result types differ");
                return CheckedConditional(valueIfAt, predicate, whenTrue, whenFalse);
            }
            var test = LogicalOr(depth + 1);
            if (Current.Text != "?") return test;
            int at = Current.Offset;
            index++;
            Require(test, IrValueType.Bool, "Conditional test requires bool");
            bool ternaryWritebackRoot = conditionalWritebackRoot == expressionStart;
            var yes = ReadTernaryArm(depth + 1, ternaryWritebackRoot, ":");
            Expect(":");
            var no = ReadTernaryArm(depth + 1, ternaryWritebackRoot, ";",
                conditionalOnly: true);
            if (yes.Type != no.Type)
                throw new FormatException($"Conditional result types differ at offset {at}");
            return CheckedConditional(at, test, yes, no);
        }
        // A direct brace always commits to an explicit result block.
        // Single-result blocks require no new local lifetime or task writeback
        // authority. Preserve the existing typed conditional CFG join.
        private StructuredExpr ReadTernaryArm(int depth, bool root, string terminator,
            bool conditionalOnly = false)
        {
            Budget(depth);
            if (!Take("{"))
                return ReadConditionalStatementArm(depth, root, terminator, conditionalOnly);
            if (!Take("return") && !Take("^"))
                throw Error("E_VALUE_ARM: ternary block requires explicit return or ^; lexical prefixes are not lowered");
            var value = ReadConditionalStatementArm(depth + 1, false, "}");
            Take(";");
            if (!Take("}"))
                throw Error("E_VALUE_ARM: single-result ternary block requires closing '}'");
            return value;
        }
        private StructuredExpr LogicalOr(int depth)
        {
            Budget(depth);
            var left = LogicalXor(depth + 1);
            while (Current.Text is "or" or "||")
            {
                int at = Current.Offset;
                index++;
                var right = LogicalXor(depth + 1);
                Require(left, IrValueType.Bool, "Logical operator requires bool operands");
                Require(right, IrValueType.Bool, "Logical operator requires bool operands");
                left = CheckedBinary(at, StructuredOperator.LogicalOr,
                    left, right, IrValueType.Bool);
            }
            return left;
        }
        private StructuredExpr LogicalXor(int depth)
        {
            Budget(depth);
            var left = LogicalAnd(depth + 1);
            while (Current.Text == "xor")
            {
                int at = Current.Offset;
                index++;
                var right = LogicalAnd(depth + 1);
                Require(left, IrValueType.Bool, "Logical operator requires bool operands");
                Require(right, IrValueType.Bool, "Logical operator requires bool operands");
                left = CheckedBinary(at, StructuredOperator.LogicalXor,
                    left, right, IrValueType.Bool);
            }
            return left;
        }
        private StructuredExpr LogicalAnd(int depth)
        {
            Budget(depth);
            var left = IntegerXor(depth + 1);
            while (Current.Text is "and" or "&&")
            {
                int at = Current.Offset;
                index++;
                var right = IntegerXor(depth + 1);
                Require(left, IrValueType.Bool, "Logical operator requires bool operands");
                Require(right, IrValueType.Bool, "Logical operator requires bool operands");
                left = CheckedBinary(at, StructuredOperator.LogicalAnd,
                    left, right, IrValueType.Bool);
            }
            return left;
        }
        // Both get(index) and get_copy(index) perform a checked, Copy<int>
        // lookup over the SAME immutable, fixed-size scalar SSA values.
        // The signed index is evaluated once, and no address/loan escapes.
        // This does not enable mutable Array<T>, general copy traits, or
        // LLVM's full actor-owned dynamic collection semantics.
        private StructuredExpr ArrayPostfix(StructuredExpr array, int depth)
        {
            if (array.Type != IrValueType.FixedArrayI64)
                return array;
            // Linked/module parsing tokenizes brackets for unrelated syntax.
            // A fixed array must never slip past the typed Option access
            // verifier through an indexed postfix, even on an out-of-bounds
            // miss. Keep this negative compiler-owned, not a parse accident.
            if (Current.Text == "[")
                throw Error("E_SAFE_COLLECTION_ACCESS_UNSUPPORTED: use typed checked get/get_copy; [] lowering is unsupported");
            if (!Take("."))
                return array;
            int at = Current.Offset;
            string operation = Current.Text;
            if (!Take("get") && !Take("get_copy"))
                throw Error("E_ARRAY_METHOD_UNSUPPORTED: fixed Array<int> only supports get(int) or get_copy(int)");
            Expect("(");
            var atIndex = Expr(depth + 1);
            Require(atIndex, IrValueType.I64,
                $"Array<int>.{operation} index must be int");
            Expect(")");
            return OptionPostfix(new StructuredArrayGet(at, array, atIndex), depth);
        }
        // Equality binds more tightly than integer XOR, which binds more
        // tightly than logical conjunction. Boolean punctuation XOR is invalid.
        private StructuredExpr IntegerXor(int depth)
        {
            Budget(depth);
            var left = Equal(depth + 1);
            while (Current.Text == "^^")
            {
                int at = Current.Offset;
                index++;
                var right = Equal(depth + 1);
                Require(left, IrValueType.I64, "Integer XOR requires int operands");
                Require(right, IrValueType.I64, "Integer XOR requires int operands");
                left = CheckedBinary(at, StructuredOperator.BitXor, left, right, IrValueType.I64);
            }
            return left;
        }
        private StructuredExpr Equal(int depth)
        {
            var left = Compare(depth + 1);
            while (Current.Text is "==" or "eq" or "!=" or "neq" or "!eq")
            {
                int at = Current.Offset;
                bool notEqual = Current.Text is "!=" or "neq" or "!eq";
                index++;
                var right = left.Type == IrValueType.GuestText && IsOrdinaryText(Current.Text)
                    ? new StructuredGuestText(Current.Offset, ErrorMessage()) : Compare(depth + 1);
                if (left.Type == IrValueType.OptionI64 ||
                    right.Type == IrValueType.OptionI64)
                {
                    Require(left, IrValueType.OptionI64,
                        "E_OPTION_EQUALITY_TYPE: Option<int> equality requires two Option<int> operands");
                    Require(right, IrValueType.OptionI64,
                        "E_OPTION_EQUALITY_TYPE: Option<int> equality requires two Option<int> operands");
                    left = new StructuredOptionEqual(at, left, right, notEqual);
                }
                else
                {
                    if (left.Type != right.Type || left.Type is not
                        (IrValueType.Bool or IrValueType.I64 or IrValueType.GuestError or IrValueType.GuestText))
                        throw new FormatException($"Equality requires matching int or bool operands at offset {at}");
                    var op = left.Type switch
                    {
                        IrValueType.Bool => notEqual ? StructuredOperator.NotEqualBool : StructuredOperator.EqualBool,
                        IrValueType.GuestError => notEqual ? StructuredOperator.NotEqualGuestError : StructuredOperator.EqualGuestError,
                        IrValueType.GuestText => notEqual ? StructuredOperator.NotEqualGuestText : StructuredOperator.EqualGuestText,
                        _ => notEqual ? StructuredOperator.NotEqualI64 : StructuredOperator.EqualI64
                    };
                    left = CheckedBinary(at, op, left, right, IrValueType.Bool);
                }
            }
            return left;
        }
        private StructuredExpr Compare(int depth)
        {
            var left = Shift(depth + 1);
            while (true)
            {
                if (Take("is"))
                {
                    Require(left, IrValueType.GuestError, "Type inspection requires Error in this backend");
                    string name = Current.Text;
                    if (name is not ("Error" or "ArithmeticOverflowError" or "DivisionByZeroError" or "OptionUnwrapError" or "ResultUnwrapError"))
                        throw Error("Unsupported nominal Error type test");
                    index++;
                    left = TypedNode(new StructuredErrorTest(left.Offset, left, name, IsType: true), left);
                    continue;
                }
                if (Current.Text == "as")
                {
                    int castAt = Current.Offset;
                    index++;
                    if (Take("?"))
                        throw new FormatException(
                            $"Optional cast as? requires verified Option<T> lowering at offset {castAt}");
                    if (Current.Text is not ("int" or "bool" or "boolean"))
                        throw new FormatException(
                            $"Checked cast requires a reifiable supported scalar target at offset {castAt}");
                    IrValueType target = Type();
                    // .NET scalar bootstrap has no tagged dynamic value:
                    // checked identity is the only provable cast. Do not bitcast.
                    if (left.Type != target)
                        throw new FormatException(
                            $"Impossible checked scalar cast at offset {castAt}");
                    // Static checked identity: deliberately retain the operand,
                    // once evaluated, rather than add a conversion node.
                    continue;
                }
                if (Current.Text is not ("<" or ">" or "<=" or ">=")) break;
                int at = Current.Offset;
                StructuredOperator comparison = Current.Text switch
                {
                    "<" => StructuredOperator.LessThanI64,
                    ">" => StructuredOperator.GreaterThanI64,
                    "<=" => StructuredOperator.LessThanOrEqualI64,
                    ">=" => StructuredOperator.GreaterThanOrEqualI64,
                    _ => throw Error("Unrecognized relational operator")
                };
                index++;
                var right = Shift(depth + 1);
                Require(left, IrValueType.I64, "Comparison requires int operands");
                Require(right, IrValueType.I64, "Comparison requires int operands");
                left = CheckedBinary(at, comparison, left, right, IrValueType.Bool);
            }
            return left;
        }
        private bool AdjacentTokenAt(int at, string text) =>
            at < tokens.Count && tokens[at].Text == text &&
            tokens[at].Offset == tokens[at - 1].Offset + 1;

        // atto-spec bitwise-and-stream-operators-v1: shift binds less tightly
        // than additive and more tightly than comparison. `<<<` (left), `>>>`
        // (logical right) and the infix word `ashr` (arithmetic right). The
        // lexer keeps '<' and '>' single, so arrows must be adjacent tokens.
        // `<<`/`>>` are typed channel/stream I/O, never shifts, and this
        // scalar compiler has no channel I/O lowering: they fail closed.
        private StructuredExpr Shift(int depth)
        {
            Budget(depth);
            var left = Add(depth + 1);
            while (true)
            {
                int at = Current.Offset;
                StructuredOperator op;
                if (Current.Text == "ashr")
                {
                    index++;
                    op = StructuredOperator.ShiftRightArithmetic;
                }
                else if (Current.Text is "<" or ">" && AdjacentTokenAt(index + 1, Current.Text))
                {
                    string arrow = Current.Text;
                    if (!AdjacentTokenAt(index + 2, arrow))
                        throw new FormatException(
                            $"'{arrow}{arrow}' is typed channel/stream I/O, not a bit shift, and this " +
                            $"backend has no channel I/O lowering; use '<<<', '>>>' or 'ashr' at offset {at}");
                    index += 3;
                    op = arrow == "<" ? StructuredOperator.ShiftLeft
                        : StructuredOperator.ShiftRightLogical;
                }
                else break;
                var right = Add(depth + 1);
                Require(left, IrValueType.I64, "Shift requires int operands");
                Require(right, IrValueType.I64, "Shift requires int operands");
                left = CheckedBinary(at, op, left, right, IrValueType.I64);
            }
            return left;
        }
        private StructuredExpr Add(int depth)
        {
            Budget(depth);
            var left = Multiply(depth + 1);
            while (Current.Text is "+" or "-")
            {
                int at = Current.Offset;
                StructuredOperator op = Take("+") ? StructuredOperator.Add :
                    (Take("-") ? StructuredOperator.Subtract :
                    throw Error("Invalid additive operator"));
                var right = Multiply(depth + 1);
                Require(left, IrValueType.I64, "Arithmetic requires int operands");
                Require(right, IrValueType.I64, "Arithmetic requires int operands");
                left = CheckedBinary(at, op, left, right, IrValueType.I64);
            }
            return left;
        }
        private StructuredExpr Multiply(int depth)
        {
            Budget(depth);
            var left = PropagatePostfix(depth + 1);
            while (Current.Text is "*" or "/")
            {
                int at = Current.Offset;
                var operation = Current.Text == "*" ? StructuredOperator.Multiply : StructuredOperator.Divide;
                index++;
                var right = PropagatePostfix(depth + 1);
                Require(left, IrValueType.I64, "Multiplication and division require int operands");
                Require(right, IrValueType.I64, "Multiplication and division require int operands");
                left = CheckedBinary(at, operation,
                    left, right, IrValueType.I64);
            }
            return left;
        }
        private StructuredExpr Unary(int depth)
        {
            Budget(depth);
            int offset = Current.Offset;
            if (Current.Text == "spawn")
                throw new FormatException($"E_SPAWN_OPTIONS_UNSUPPORTED: actor spawn requires typed initialization, resource admission and runtime lowering at offset {offset}");
            if (Take("match")) return ReadMatchValue(offset, depth);
            if (AtSyncTask())
            {
                index += 2;
                if (trapOperandDepth != 0)
                    throw Error("E_TRAP_INLINE_TASK_UNSUPPORTED: outlining a sync task requires lexical writeback on failure");
                return TaggedPostfix(new StructuredSyncTaskValue(offset, ReadInlineTask(depth + 1, imperative: false,
                    writeBack: directInlineWritebackOffset == offset)), depth);
            }
            if (inlineTaskDepth != 0 && Current.Text is "await" or "yield")
                throw Error("E_SYNC_TASK_SUSPEND: this suspension lacks a same-thread continuation");
            if (Take("await"))
            {
                if (Current.Text == "task")
                    throw Error("E_SUSPENDS_UNSUPPORTED: scheduled task await requires typed continuation lowering");
                var operand = Unary(depth + 1);
                Require(operand, IrValueType.I64, "E_SUSPENDS_UNSUPPORTED: immediate await requires int");
                return operand;
            }
            if (Take("trap") || Take("trap!")) return TrapOperand(offset, depth);
            if (Take("Ok")) return TaggedPostfix(NewResult(offset, success: true, depth), depth);
            if (Take("Err")) return TaggedPostfix(NewResult(offset, success: false, depth), depth);
            if (Take("new") || Current.Text == "Error")
            {
                Expect("Error");
                Expect("(");
                string message = ErrorMessage();
                Expect(")");
                return TaggedPostfix(new StructuredGuestError(offset, message), depth);
            }
            if (Current.Text == "Integer" && index + 2 < tokens.Count &&
                tokens[index + 1].Text == "." && tokens[index + 2].Text is "rem" or "mod" or "bitXor")
            {
                var operation = tokens[index + 2].Text switch
                {
                    "rem" => StructuredOperator.Remainder,
                    "mod" => StructuredOperator.Modulo,
                    _ => StructuredOperator.BitXor
                };
                index += 3;
                Expect("(");
                var left = Expr(depth + 1);
                Expect(",");
                var right = Expr(depth + 1);
                Expect(")");
                Require(left, IrValueType.I64, "Named integer arithmetic requires int operands");
                Require(right, IrValueType.I64, "Named integer arithmetic requires int operands");
                return CheckedBinary(offset, operation, left, right, IrValueType.I64);
            }
            if (Current.Text.Length > 0 && Current.Text[0] is '\'' or '"' or '\u0060')
                throw new FormatException(
                    $"STRING_BACKEND_UNSUPPORTED: quoted strings and template interpolation require typed String CFG/CIL/NativeAOT lowering at offset {Current.Offset}");
            if (Current.Text == "rt")
            {
                int at = Current.Offset;
                index++;
                string operation = Current.Text;
                if (operation is "ref" or "unref" or "deref" or "raw")
                    throw new FormatException(
                        $"Pointer-style operation 'rt {operation}' is not valid Oreslang source; use rt copy/share/take/borrow or rt mut borrow; unsupported in the .NET structured backend at offset {at}");

                string ownership = operation;
                if (operation == "mut" && index + 1 < tokens.Count &&
                    tokens[index + 1].Text == "borrow")
                    ownership = "mut borrow";
                if (operation == "borrow" && index + 1 < tokens.Count &&
                    tokens[index + 1].Text == "mut")
                    ownership = "mut borrow"; // compatible legacy order, same unsupported typed gate
                if (ownership is "copy" or "share" or "take" or "borrow" or "mut borrow")
                    throw new FormatException(
                        $"Ownership operation 'rt {ownership}' requires typed actor-arena lowering; unsupported in the .NET structured backend at offset {at}");

                throw new FormatException(
                    $"Runtime ownership operation requires typed lowering; unsupported in the .NET structured backend at offset {at}");
            }
            if (Take("copy!") || Take("copy!!"))
            {
                // I64 and Bool are identity-free values. Lower their copy to
                // the existing typed expression exactly once; no reference
                // or ownership capability is manufactured.
                var operand = Unary(depth + 1);
                if (operand.Type is not (IrValueType.I64 or IrValueType.Bool))
                    throw new FormatException($"E_COPY_UNSUPPORTED_TYPE: non-scalar copy needs checked ownership lowering at offset {offset}");
                return operand;
            }
            if (Take("+"))
            {
                var operand = Unary(depth + 1);
                Require(operand, IrValueType.I64, "Unary plus requires int");
                return operand;
            }
            // Prefix logical not on a bool operand. Lowered as a typed
            // conditional (operand ? false : true) over the existing bool CFG.
            if (Current.Text is "!" or "¬")
            {
                int at = Current.Offset;
                index++;
                var operand = Unary(depth + 1);
                Require(operand, IrValueType.Bool, "Logical not requires bool");
                return CheckedConditional(at, operand,
                    new StructuredBool(at, false), new StructuredBool(at, true));
            }
            if (Current.Text == "-")
            {
                int at = Current.Offset;
                index++;
                if (Current.Text.Replace("_", "", StringComparison.Ordinal).TrimStart('0') == "9223372036854775808")
                {
                    index++;
                    return new StructuredInt(at, long.MinValue);
                }
                var operand = Unary(depth + 1);
                Require(operand, IrValueType.I64, "Unary minus requires int");
                return CheckedNegate(at, operand);
            }
            if (Take("("))
            {
                var inner = Expr(depth + 1);
                Expect(")");
                return TaggedPostfix(inner, depth);
            }
            if (Take("Array"))
            {
                Expect(".");
                Expect("of");
                Expect("(");
                var elements = new List<StructuredExpr>();
                if (Current.Text != ")")
                {
                    do
                    {
                        if (elements.Count >= OresCfgVerifier.MaxFixedArrayElements)
                            throw Error("E_ARRAY_SIZE: fixed Array<int> exceeds admitted element budget");
                        var item = Expr(depth + 1);
                        Require(item, IrValueType.I64, "Array.of elements must be int");
                        elements.Add(item);
                    } while (Take(","));
                }
                Expect(")");
                return ArrayPostfix(new StructuredFixedArray(
                    offset, Array.AsReadOnly(elements.ToArray())), depth);
            }
            if (Take("Some"))
            {
                Expect("(");
                var payload = Expr(depth + 1);
                Require(payload, IrValueType.I64, "Some(int) payload must be int");
                Expect(")");
                return OptionPostfix(new StructuredSome(offset, payload), depth);
            }
            if (Take("None"))
                return OptionPostfix(new StructuredNone(offset), depth);
            if (Take("true")) return new StructuredBool(offset, true);
            if (Take("false")) return new StructuredBool(offset, false);
            string raw = Current.Text;
            if (raw.Length > 0 && Digit(raw[0]))
            {
                index++;
                if (!long.TryParse(raw.Replace("_", "", StringComparison.Ordinal),
                    NumberStyles.None, CultureInfo.InvariantCulture, out long n))
                    throw new FormatException($"Invalid i64 at offset {offset}");
                return new StructuredInt(offset, n);
            }
            var name = Name();
            var aggregate = FindAggregate(name);
            if (aggregate is not null)
            {
                string field;
                if (aggregate.Wrapper == "Tuple")
                {
                    int openAt = Current.Offset;
                    Expect("[");
                    if (!int.TryParse(Current.Text, NumberStyles.None, CultureInfo.InvariantCulture,
                        out int slot) || slot < 0)
                        throw Error("Tuple projection requires a nonnegative constant index");
                    index++;
                    int closeAt = Current.Offset;
                    Expect("]");
                    field = slot.ToString(CultureInfo.InvariantCulture);
                    if (!aggregate.Values.ContainsKey(field)) throw Error("Tuple projection outside checked static shape");
                    verifiedTupleBrackets.Add(openAt);
                    verifiedTupleBrackets.Add(closeAt);
                }
                else
                {
                    if (!Take(".")) throw Error("Aggregate value cannot escape its compile-time local");
                    field = Name();
                }
                if (!aggregate.Values.TryGetValue(field, out var exact))
                    throw Error("Unknown inferred aggregate field");
                return exact switch
                {
                    StructuredBool flag => new StructuredBool(offset, flag.Value),
                    StructuredInt number => new StructuredInt(offset, number.Value),
                    StructuredRead read => new StructuredRead(offset, read.Name, read.Type),
                    _ => throw Error("Unverified aggregate field projection")
                };
            }
            bool localBinding = scopes.Any(scope => scope.ContainsKey(name));
            // Option methods on local values must not be reinterpreted as
            // linked module qualification or static method dispatch.
            if (localBinding && Lookup(name, offset).Type is var taggedType &&
                (taggedType is IrValueType.OptionI64 or IrValueType.GuestError or IrValueType.GuestText || taggedType.IsResult()))
                return TaggedPostfix(new StructuredRead(offset, name, taggedType), depth);
            if (localBinding && Lookup(name, offset).Type == IrValueType.FixedArrayI64)
                return ArrayPostfix(new StructuredRead(offset, name, IrValueType.FixedArrayI64), depth);
            StructuredFunction? frozenTarget=null;
            if(frozenProvider&&frozenNamespaceBindings.TryGetValue(name,out var namespaceBinding)) {
                if(localBinding) throw Error("E_IMPORT_KIND: local shadows provider namespace");
                if(!Take(".")) throw Error("E_IMPORT_KIND: namespace is not a runtime value");
                string member=Name();
                if(Current.Text!="(") throw Error("E_IMPORT_KIND: expected direct namespace function call");
                frozenTarget=namespaceBinding(member);
            } else if (linking is not null)
                while (Take("."))
                {
                    if (localBinding) throw Error("Qualified access on local values requires nominal type lowering");
                    name += "." + Name();
                }
            bool explicitGeneric = false;
            bool scalarGeneric = scalarGenericParameters.TryGetValue(name, out string? genericParameter);
            if (scalarGeneric && !localBinding && Take("<"))
            {
                explicitGeneric = true;
                if (index + 1 < tokens.Count && tokens[index + 1].Text == "=")
                {
                    string label = Name();
                    Expect("=");
                    if (label != genericParameter)
                        throw Error("E_GENERIC_BINDER: unknown named generic parameter");
                }
                Expect("int");
                Expect(">");
                Expect("(");
            }
            if (explicitGeneric || Take("("))
            {
                if (localBinding) throw Error("Local value is not a statically bound callable");
                var target=frozenTarget;
                // Only the unqualified, unshadowed core gates are admitted.
                // Frozen/linking profiles keep ordinary resolved dispatch.
                if (target is null && linking is null && !frozenProvider &&
                    (name is "And" or "Or" or "Xor") && !visible.ContainsKey(name))
                {
                    int packOpen = Current.Offset;
                    bool pack = Take("[");
                    var operands = new List<StructuredExpr>();
                    if (Current.Text != (pack ? "]" : ")"))
                    {
                        do
                        {
                            if (operands.Count >= 32)
                                throw Error("Boolean reduction operand budget exceeded");
                            var operand = Expr(depth + 1);
                            Require(operand, IrValueType.Bool, name + " requires bool operands");
                            // Only literal packs in this profile. No premature
                            // lazy evaluation of effectful array construction.
                            if (pack && operand is not StructuredBool)
                                throw Error("Boolean array pack requires bool literals");
                            operands.Add(operand);
                        }
                        while (Take(","));
                    }
                    if (pack)
                    {
                        int packClose = Current.Offset;
                        Expect("]");
                        verifiedBooleanPackBrackets.Add(packOpen);
                        verifiedBooleanPackBrackets.Add(packClose);
                    }
                    Expect(")");
                    if (!pack && operands.Count < 2)
                        throw Error(name + " requires at least two scalar operands");
                    if (operands.Count == 0)
                        return new StructuredBool(offset, name == "And");
                    var op = name switch
                    {
                        "And" => StructuredOperator.LogicalAnd,
                        "Or" => StructuredOperator.LogicalOr,
                        _ => StructuredOperator.LogicalXor
                    };
                    var result = operands[0];
                    for (int i = 1; i < operands.Count; i++)
                        result = CheckedBinary(offset, op, result, operands[i], IrValueType.Bool);
                    return result;
                }
                if (target is null&&!visible.TryGetValue(name, out target))
                    throw new FormatException(
                        $"Undefined, recursive or forward typed helper call at offset {offset}");
                var args = new List<StructuredExpr>();
                if (Current.Text != ")")
                {
                    do
                    {
                        if (args.Count >= MaxParameters)
                            throw Error("Typed helper call argument budget exceeded");
                        if (scalarGeneric && index + 1 < tokens.Count && tokens[index + 1].Text == ":")
                        {
                            string label = Name();
                            Expect(":");
                            if (args.Count != 0 || label != target.Parameters[0].Name)
                                throw Error("E_GENERIC_BINDER: invalid named runtime argument");
                        }
                        args.Add(args.Count < target.Parameters.Count
                            ? ExpectedExpr(depth + 1, target.Parameters[args.Count].Type)
                            : Expr(depth + 1));
                    }
                    while (Take(","));
                }
                Expect(")");
                if (args.Count != target.Parameters.Count)
                    throw new FormatException($"Typed helper call arity mismatch at offset {offset}");
                for (int i = 0; i < args.Count; i++)
                    Require(args[i], target.Parameters[i].Type, "Typed helper argument type mismatch");
                int height = 1;
                foreach (var arg in args) height = Math.Max(height, Height(arg) + 1);
                if (height > MaxDepth)
                    throw new FormatException($"Typed helper call AST depth exceeds limit at offset {offset}");
                var node = new StructuredCall(offset, target.Name,
                    Array.AsReadOnly(args.ToArray()), target.ResultType);
                expressionHeights.Add(node, height);
                return TaggedPostfix(node, depth);
            }
            var binding = Lookup(name, offset);
            if (binding.Kind == StructuredBindingKind.Const && binding.ConstantValue.HasValue)
            {
                long constant = binding.ConstantValue.Value;
                return binding.Type == IrValueType.Bool
                    ? new StructuredBool(offset, constant != 0)
                    : new StructuredInt(offset, constant);
            }
            return new StructuredRead(offset, name, binding.Type);
        }
        private static void Require(StructuredExpr expression,
            IrValueType expected, string reason)
        {
            if (expression.Type != expected)
                throw new FormatException($"{reason} at offset {expression.Offset}");
        }
    }
    private static bool Alpha(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
    private static bool Digit(char c) => c is >= '0' and <= '9';
}
