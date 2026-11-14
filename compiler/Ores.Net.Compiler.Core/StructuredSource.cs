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
    EqualBool, NotEqualBool, LogicalAnd, LogicalOr, LogicalXor }
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
public sealed record StructuredSet(int At, string Name, StructuredExpr Value)
    : StructuredStmt(At);
public sealed record StructuredReturn(int At, StructuredExpr Value) : StructuredStmt(At);
// Explicit return; and compiler-inserted void fallthrough share a terminator.
public sealed record StructuredReturnVoid(int At) : StructuredStmt(At);
public sealed record StructuredIf(int At, StructuredExpr Condition,
    IReadOnlyList<StructuredStmt> Then, IReadOnlyList<StructuredStmt>? Else)
    : StructuredStmt(At);
public sealed record StructuredWhile(int At, StructuredExpr Condition,
    IReadOnlyList<StructuredStmt> Body) : StructuredStmt(At);
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

    internal static StructuredLinkedUnit ParseLinked(string source, LinkedModuleContext linking)
        => ParseCore(source, linking);

    internal static IReadOnlyList<SourceImport> ReadLinkedImports(string source)
    {
        var reader = new Reader(LexLinked(source));
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
                throw ActorPipeAdmission.Unsupported(tokens[i].Offset);
        }
    }

    private static StructuredLinkedUnit ParseCore(string source, LinkedModuleContext? linking)
    {
        ValidateSourceInput(source);
        ActorPubSubSourceAdmission.RejectUnsupported(source);
        // No structural match lowering or readonly shape metadata in scalar CIL yet.
        StructuralShapeAdmission.RejectUnsupportedPatterns(source);
        var lexer = new Lexer(source, linking is not null);
        var tokens = new List<Token>();
        // Distinguish a forbidden local nominal declaration from an
        // unsupported top-level class/actor before either reaches lowering.
        int braceDepth = 0;
        int firstNominalOffset = -1;
        while (true)
        {
            var token = lexer.Read();
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
                    $"Private abstract class hooks require typed virtual dispatch and superclass-only invocation at offset {token.Offset}");
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
                throw new FormatException(
                    $"Inverted class requires checked bracket-row and ownership lowering in the .NET backend at offset {token.Offset}");
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
            if ((token.Text == "class" || token.Text == "actor") &&
                tokens.Count > 0 &&
                (tokens[^1].Text == "define" ||
                 (tokens[^1].Text == "pub" && tokens.Count > 1 &&
                  tokens[^2].Text == "define")))
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
            if (token.Text == "namespace" && i > 0 && tokens[i - 1].Text == "use")
                throw new FormatException(
                    $"NAMESPACE_RUNTIME_NATIVEAOT_UNSUPPORTED: file namespace requires verified nominal actor/linker identity at offset {token.Offset}");
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
                $"Nominal class/actor methods require receiver-erased method slots, type/borrow checking and Native AOT CIL lowering; unsupported in .NET structured frontend at offset {firstNominalOffset}");
        }
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
            if (word is "suspends" or "await")
                throw new FormatException(
                    $"E_SUSPENDS_UNSUPPORTED: structured CIL lacks typed suspension/continuation lowering at offset {tokens[i].Offset}");
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
                    "vector" or "set" or "entry" or "pair" or "record")
                throw new FormatException(
                    $"E_COLLECTION_FACTORY_UNSUPPORTED: ~{tokens[i + 1].Text} requires typed product/collection CFG and NativeAOT CIL lowering at offset {tokens[i].Offset}");
            if ((tokens[i].Text is "tuple" or "structural") &&
                tokens[i + 1].Text == "(")
                throw new FormatException(
                    $"Tuple-v2 source requires typed tuple ownership/ABI lowering at offset {tokens[i].Offset}");
            if (i + 2 < tokens.Count && tokens[i].Text == "." &&
                tokens[i + 1].Text == "." && tokens[i + 2].Text == ".")
                throw new FormatException(
                    $"Tuple-v2 explicit spread requires single-evaluation checked lowering at offset {tokens[i].Offset}");
        }
        var reader = new Reader(tokens, linking);
        return linking is null
            ? new StructuredLinkedUnit(reader.ReadProgram(), null, new Dictionary<string, StructuredFunction>())
            : reader.ReadLinkedUnit();
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
                else if (cursor < tokens.Count &&
                    (tokens[cursor].Text is "mtd" or "method" or "fnc"))
                    cursor++;
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
    private sealed class Lexer(string source, bool moduleMode = false)
    {
        private int index;
        private bool previousWasWith;
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
            previousWasWith = false;
            if (precededByWith && ch == '{')
                throw new FormatException(
                    $"WITH_BACKEND_UNSUPPORTED: immutable with requires checked rt copy/take, "
                    + $"owned field layout/drop and Native AOT reconstruction at offset {index}");
            // Keep safe-collection denial from current main in the separate
            // structured/linked parser, including when Option<int> is enabled.
            SafeCollectionSourceAdmission.RejectUnverifiedBracket(ch, offset);
            // Injection changes ownership and lifetime authority. The scalar
            // CIL bootstrap cannot treat an annotation as harmless metadata.
            // Perform this check after comments have been skipped so comment
            // text cannot trigger a false rejection.
            if (ch == '@')
            {
                int nameEnd = index + 1;
                while (nameEnd < source.Length &&
                       (Alpha(source[nameEnd]) || Digit(source[nameEnd]) ||
                        source[nameEnd] == '_')) nameEnd++;
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
                return new Token(source[offset..index], offset);
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
                         !(char.IsLetterOrDigit(source[index + 1]) || source[index + 1] == '_')))))
                    throw new FormatException($"Floating-point/decimal literal requires typed numeric lowering at offset {offset}");
            }
            else if (Alpha(ch) || ch == '_')
            {
                do { index++; } while (index < source.Length &&
                    (Alpha(source[index]) || Digit(source[index]) || source[index] == '_'));
                string word = source[offset..index];
                // The linked-module lexer also enforces the unsafe keyword
                // reservation before typed module binding and CIL emission.
                if (word == "unsafe")
                    throw new FormatException(
                        $"unsafe boundary requires checked effects, native grants and Native AOT lowering at offset {offset}");
                // Reject before the condition is lexed: `guard 1>0` contains
                // characters this lexer refuses outside module mode.
                if (word == "guard")
                    throw new FormatException(
                        $"GUARD_BACKEND_UNSUPPORTED: early-exit CFG and ownership lowering required at offset {offset}");
                // Match/default is a true expression grammar extension, not a
                // valid scalar identifier or CIL fallthrough. This lexer skips
                // comments before this check; no source-substring blacklist.
                if (word == "match")
                    throw new FormatException(
                        $"MATCH_DEFAULT_BACKEND_UNSUPPORTED: typed match requires real pattern admission, ordered CFG fallback and exhaustiveness before CIL emission at offset {offset}");
                if (word is "cpu" or "gpu" or "quantum" ||
                    (word == "nb" && !StaticSelectAdmission.FollowedByStaticSelect(source, index)))
                    throw new FormatException(
                        $"execution region '{word}' requires verified CPU/GPU Task lowering; no implicit CPU fallback at offset {offset}");
                if (word == "task")
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
            else if (ch == '~')
                throw new FormatException(
                    $"TILDE_AGGREGATE_BACKEND_UNSUPPORTED: typed Map/Record literals require a checked AST, key domains, ownership, transactional constructor and NativeAOT layout at offset {offset}");
            else if (ch == '%')
                throw new FormatException(
                    $"noncanonical '%' source operator; use named Integer.rem/mod at offset {offset}");
            else if (ch == '^')
            {
                // `^^` is the canonical eager boolean XOR (spec ch.16); single `^`
                // stays out of the grammar (integer XOR is Integer.bitXor).
                if (index + 1 < source.Length && source[index + 1] == '^') index += 2;
                else throw new FormatException(
                    $"Reserved AttoLang operator '^'; use Integer.bitXor(a, b) at offset {offset}");
            }
            else if (ch == '!' && index + 2 < source.Length &&
                     source[index + 1] == 'e' && source[index + 2] == 'q' &&
                     (index + 3 == source.Length ||
                      !(Alpha(source[index + 3]) || Digit(source[index + 3]) ||
                        source[index + 3] == '_')))
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
                      !(char.IsLetterOrDigit(source[index + 3]) ||
                        source[index + 3] == '_')))
                index += 3;
            else if (ch == '!' && index + 1 < source.Length &&
                     source[index + 1] == '=') index += 2;
            else if (index + 1 < source.Length &&
                (((ch == '<' || ch == '>') && source[index + 1] == '=') ||
                 (source.Substring(index, 2) is "&&" or "||") ||
                 (moduleMode && source.Substring(index, 2) is "->")))
                index += 2;
            else if (ch == '|' && !moduleMode)
                throw new FormatException(
                    $"Lexical captures require checked closure lowering at offset {offset}");
            else if ("()+-*{}:;=<>.,?~".IndexOf(ch) >= 0 ||
                (moduleMode && "[]/>!|&".IndexOf(ch) >= 0)) index++;
            else throw new FormatException($"Invalid character at offset {offset}");
            var tokenText = source[offset..index];
            if (tokenText == "import" && !moduleMode)
                throw new FormatException("source imports require verified module graph lowering");
            if (tokenText == "enum")
                throw new FormatException(
                    $"nominal enum overloads require compiler-bound dispatch (unsupported .NET bootstrap) at offset {offset}");
            previousWasWith = tokenText == "with";
            // The opt-in structured compiler has independently verified closed
            // Option<int> lowering; unlike the shipping scalar frontend it
            // must admit Option/Some/None tokens. Everything else in the
            // tagged family is still unsupported, even when reached via
            // linked-unit discovery, and must fail before typed SSA/CIL.
            // This is a complete-token check after skipping source trivia.
            if (tokenText is "Result" or "Ok" or "Err" or "trap")
                throw new FormatException(
                    $"Option/Result/trap requires verified tagged-sum CIL and NativeAOT guest-control lowering at offset {offset}");
            return new Token(tokenText, offset);
        }
    }

    private sealed class Reader(List<Token> tokens, LinkedModuleContext? linking = null)
    {
        private sealed record LocalBinding(IrValueType Type, StructuredBindingKind Kind,
            long? ConstantValue);
        private readonly List<Dictionary<string, LocalBinding>> scopes = new();
        private readonly List<HashSet<string>> localTypeScopes = new();
        private readonly Dictionary<string, StructuredFunction> functions = linking is null
            ? new(StringComparer.Ordinal) : new(linking.Methods, StringComparer.Ordinal);
        private readonly Dictionary<string, StructuredFunction> visible = linking is null
            ? new(StringComparer.Ordinal) : new(linking.Bindings, StringComparer.Ordinal);
        private readonly Dictionary<string, StructuredFunction> exports = new(StringComparer.Ordinal);
        private readonly List<string> functionOrder = linking is null ? new() : new(linking.MethodOrder);
        private string? declaredModule;
        private int index, statements;
        private IrValueType activeReturnType = IrValueType.I64;
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
        public StructuredProgram ReadProgram() => ReadDeclarations(true);

        internal IReadOnlyList<SourceImport> ReadImports()
        {
            var imports = new List<SourceImport>();
            while (Take("import"))
            {
                int offset = Current.Offset;
                string kind = Current.Text;
                // Until the NativeAOT linker has pinned manifest-defined
                // PackageInstanceId routing, never interpret namespace imports
                // as a source-file wildcard or ambient search path.
                if (kind == "namespace")
                    throw Error("import namespace requires manifest-verified multi-file package linker");
                if (kind is not ("fnc" or "module")) throw Error("Linked source supports fnc and module imports");
                index++;
                string name = Name();
                Expect("from");
                string quoted = Current.Text;
                if (quoted.Length < 3 ||
                    (quoted[0] != '"' && quoted[0] != '\'') ||
                    quoted[^1] != quoted[0] || quoted.Contains('\\'))
                    throw Error("Import requires an unescaped single- or double-quoted local source string");
                // Reject before physical lookup and graph linking, independently
                // of the standalone scalar parser's generic import denial.
                UnsafeStdlibImportAdmission.RequireGuestSafe(quoted[1..^1]);
                index++;
                Expect(";");
                if (imports.Count >= 64) throw Error("Import declaration budget exceeded");
                imports.Add(new SourceImport(kind, name, quoted[1..^1], offset));
            }
            return imports.AsReadOnly();
        }

        internal StructuredLinkedUnit ReadLinkedUnit()
        {
            _ = ReadImports();
            if (Current.Text == "define" && index + 1 < tokens.Count && tokens[index + 1].Text == "module")
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
            if (declaredModule is not null) Expect("end");
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
                if (Current.Text is "class" or "actor")
                    throw Error("Nominal class/actor methods require receiver-erased method slots, type/borrow checking and Native AOT CIL lowering; unsupported in .NET structured frontend");
                Expect("fnc");
                int at = Current.Offset;
                string name = Name();
                // This NativeAOT slice does not lower once-only initialization
                // barriers. Do not silently compile a lifecycle hook into an
                // ordinary public CIL method reachable through imports.
                if (name == "init")
                    throw Error("module init requires once-only reachable initialization lowering");
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
                        IrValueType type = Type();
                        // The pair ABI is intentionally not exposed across
                        // calls until exact typed method signatures exist.
                        if (type == IrValueType.OptionI64)
                            throw Error("E_OPTION_ABI_UNSUPPORTED: Option<int> method parameters need verified two-word ABI");
                        if (type == IrValueType.FixedArrayI64)
                            throw Error("E_ARRAY_ABI_UNSUPPORTED: fixed array parameters need verified scalar layout ABI");
                        string parameter = Name();
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
                if (!Take(":"))
                {
                    if (linking is null) Expect(":");
                    else Expect("->");
                }
                if (Current.Text == "(")
                    throw Error("Tuple-v2 shorthand return requires typed tuple ownership/ABI lowering");
                IrValueType resultType = Take("void") ? IrValueType.Void : Type();
                if (resultType == IrValueType.OptionI64)
                    throw Error("E_OPTION_ABI_UNSUPPORTED: Option<int> method results need verified two-word ABI");
                if (resultType == IrValueType.FixedArrayI64)
                    throw Error("E_ARRAY_ABI_UNSUPPORTED: fixed array method results need verified scalar layout ABI");
                if (name == "main" && entry)
                {
                    if (visible.ContainsKey(name)) throw Error("Entry main conflicts with an imported callable");
                    activeReturnType = IrValueType.I64;
                    if (parameters.Count != 0 || resultType != IrValueType.I64)
                        throw new FormatException($"main must have signature ():int at offset {at}");
                    var (body, returns) = Block(0);
                    if (!returns) throw Error("RETURN_EXPLICIT_REQUIRED: not every main path returns int");
                    return Finish("main", body);
                }
                if (visible.ContainsKey(name))
                    throw new FormatException($"Duplicate typed helper at offset {at}");
                if (functions.Count >= MaxHelpers)
                    throw Error("Typed helper declaration budget exceeded");
                // All helper bodies now share the same checked typed block
                // parser as main. The separate MethodDef ABI isolates locals.
                activeReturnType = resultType;
                scopes.Add(parameterTypes);
                var (helperBody, helperReturns) = Block(0);
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
                string methodName = linking?.CanonicalName(name) ?? name;
                var declaration = new StructuredFunction(methodName,
                    Array.AsReadOnly(parameters.ToArray()), resultType,
                    inlineReturn, helperBody);
                functions.Add(methodName, declaration);
                visible.Add(name, declaration);
                if (exposed) exports.Add(name, declaration);
                functionOrder.Add(methodName);
            }
        }
        private (IReadOnlyList<StructuredStmt> Body, bool Returns) Block(int depth)
        {
            Budget(depth);
            Expect("{");
            scopes.Add(new Dictionary<string, LocalBinding>(StringComparer.Ordinal));
            localTypeScopes.Add(new HashSet<string>(StringComparer.Ordinal));
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
            return (Array.AsReadOnly(body.ToArray()), terminated);
        }
        // The existing bracketed CFG path remains available. Canonical ores.core
        // also uses 'if predicate; do ... [else ...] fi' without braces.
        // Close lexical scopes at each arm; never turn source into rewritten C#.
        private (IReadOnlyList<StructuredStmt> Body, bool Returns) DelimitedArm(int depth)
        {
            Budget(depth);
            scopes.Add(new Dictionary<string, LocalBinding>(StringComparer.Ordinal));
            localTypeScopes.Add(new HashSet<string>(StringComparer.Ordinal));
            var body = new List<StructuredStmt>();
            bool terminated = false;
            while (Current.Text is not ("else" or "fi"))
            {
                if (Current.Text.Length == 0 || Current.Text == "}")
                    throw Error("Unterminated Oreslang if/do/fi arm");
                if (terminated) throw Error("Unreachable statement");
                if (++statements > MaxStatements)
                    throw Error("Structured statement budget exceeded");
                var (statement, returns) = Statement(depth + 1);
                body.Add(statement);
                terminated = returns;
            }
            scopes.RemoveAt(scopes.Count - 1);
            localTypeScopes.RemoveAt(localTypeScopes.Count - 1);
            return (Array.AsReadOnly(body.ToArray()), terminated);
        }

        private (StructuredStmt Statement, bool Returns) Statement(int depth)
        {
            Budget(depth);
            int at = Current.Offset;
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
            if (Current.Text == "guard")
                throw new FormatException(
                    $"GUARD_BACKEND_UNSUPPORTED: early-exit CFG and ownership lowering required at offset {at}");
            if (Take("return"))
            {
                if (activeReturnType == IrValueType.Void)
                {
                    if (Current.Text != ";")
                        throw Error("RETURN_VOID_VALUE: void function cannot return a value");
                    Expect(";");
                    return (new StructuredReturnVoid(at), true);
                }
                if (Current.Text == ";")
                    throw Error("RETURN_VALUE_REQUIRED: non-void function requires a value");
                var value = Expr(depth + 1);
                Require(value, activeReturnType,
                    "Return value does not match function result type");
                Expect(";");
                return (new StructuredReturn(at, value), true);
            }
            if (Current.Text is "let" or "val" or "const" or "int" or "bool")
            {
                // Bare type-first locals are immutable by default:
                //   int x=e;  == const int x=e;
                // Explicit let is the only rebindable scalar binding.
                bool typeFirstImmutable = Current.Text is "int" or "bool";
                // A runtime-initialized readonly scalar is NOT an explicit
                // compile-time const, even if later optimizers fold its value.
                StructuredBindingKind kind = typeFirstImmutable
                    ? StructuredBindingKind.Readonly
                    : Current.Text switch
                    {
                        "let" => StructuredBindingKind.Let,
                        "val" => StructuredBindingKind.Val,
                        _ => StructuredBindingKind.Const
                    };
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
                    annotated = Current.Text is "int" or "bool" or "Option" or "Array"; // type-first incl. closed Option<int> and fixed Array<int>
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
                if (scopes.Any(scope => scope.ContainsKey(name)))
                    throw new FormatException($"Duplicate local binding at offset {nameAt}");
                Expect("=");
                var initializer = Expr(depth + 1);
                if (annotated)
                    Require(initializer, type, "Local initializer has wrong type");
                else
                    type = initializer.Type; // No i64 fallback for bool or unsupported types.
                if (type == IrValueType.FixedArrayI64 && kind != StructuredBindingKind.Val)
                    throw Error("E_ARRAY_IMMUTABLE_ONLY: fixed Array<int> requires val; mutable borrow, resize and const folding are unsupported");
                long? constantValue = null;
                // Any const Option<int> fails closed, not only scalar-shaped initializers.
                if (kind == StructuredBindingKind.Const && type == IrValueType.OptionI64)
                    throw new FormatException(
                        $"E_OPTION_CONST_UNSUPPORTED: Option<int> const folding needs tagged-value proof at offset {at}");
                if (kind == StructuredBindingKind.Const &&
                    IsCompileTimeScalar(initializer))
                {
                    try { constantValue = ConstantValue(initializer); }
                    catch (OverflowException)
                    {
                        throw new FormatException(
                            $"Compile-time const overflow at offset {initializer.Offset}");
                    }
                    initializer = type == IrValueType.Bool
                        ? new StructuredBool(initializer.Offset, constantValue.Value != 0)
                        : new StructuredInt(initializer.Offset, constantValue.Value);
                }
                Expect(";");
                // Bind only after checking initializer (no self-reference).
                scopes[^1].Add(name, new LocalBinding(type, kind, constantValue));
                return (new StructuredLet(at, name, type, initializer, kind), false);
            }
            if (Take("if"))
            {
                var condition = Expr(depth + 1);
                Require(condition, IrValueType.Bool, "If condition must be bool");
                bool oresDoForm = Take(";");
                IReadOnlyList<StructuredStmt> yes;
                bool yesReturns;
                if (oresDoForm)
                {
                    Expect("do");
                    (yes, yesReturns) = DelimitedArm(depth + 1);
                }
                else (yes, yesReturns) = Block(depth + 1);
                IReadOnlyList<StructuredStmt>? no = null;
                bool noReturns = false;
                if (Take("else"))
                {
                    if (oresDoForm) (no, noReturns) = DelimitedArm(depth + 1);
                    else (no, noReturns) = Block(depth + 1);
                }
                if (oresDoForm) Expect("fi");
                return (new StructuredIf(at, condition, yes, no),
                    yesReturns && noReturns);
            }
            if (Take("while"))
            {
                var condition = Expr(depth + 1);
                Require(condition, IrValueType.Bool, "While condition must be bool");
                var (body, _) = Block(depth + 1);
                // Even a constant-true loop is not accepted as a return proof.
                return (new StructuredWhile(at, condition, body), false);
            }
            int variableAt = Current.Offset;
            string variable = Name();
            var binding = Lookup(variable, variableAt);
            if (binding.Kind != StructuredBindingKind.Let)
                throw new FormatException(
                    $"Cannot reassign {binding.Kind.ToString().ToLowerInvariant()} binding '{variable}' at offset {variableAt}");
            Expect("=");
            var assigned = Expr(depth + 1);
            Require(assigned, binding.Type, "Assignment has wrong type");
            Expect(";");
            return (new StructuredSet(at, variable, assigned), false);
        }
        private IrValueType Type()
        {
            // Type-position '?' is solely Option<T> sugar, not a default
            // or omittable parameter. This closed NativeAOT slice currently
            // represents only Option<int> (tag + full-width i64 payload).
            if (Take("int"))
            {
                if (!Take("?")) return IrValueType.I64;
                if (Current.Text == "?")
                    throw Error("E_OPTION_NESTED_UNSUPPORTED: int?? requires verified nested Option ABI");
                return IrValueType.OptionI64;
            }
            if (Take("bool"))
            {
                if (Take("?"))
                    throw Error("E_OPTION_I64_ONLY: bool? requires Option<bool> lowering");
                return IrValueType.Bool;
            }
            if (Take("Array"))
            {
                Expect("<");
                if (!Take("int"))
                    throw Error("E_ARRAY_I64_ONLY: fixed arrays support only Array<int>");
                Expect(">");
                return IrValueType.FixedArrayI64;
            }
            if (Take("Option"))
            {
                Expect("<");
                if (!Take("int"))
                    throw Error("E_OPTION_I64_ONLY: this NativeAOT slice admits only Option<int>; no nested or erased generic Option");
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
            if (name.Length is < 1 or > MaxIdentifier ||
                !(Alpha(name[0]) || name[0] == '_') ||
                !name.Skip(1).All(c => Alpha(c) || Digit(c) || c == '_') ||
                name is "if" or "else" or "while" or "return" or "let"
                    or "int" or "bool" or "true" or "false" or "and" or "or" or "xor"
                    or "define" or "pub" or "fnc" or "const" or "val" or "final"
                    or "struct" or "record" or "class" or "actor"
                    or "module" or "import" or "from" or "as" or "end"
                    or "routine" or "async" or "isoactor" or "guard"
                    or "Some" or "None" or "Option" or "Array")
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

        // A source const is a fixed binding, not a demand for compile-time
        // evaluation. Fold pure scalar literals, retaining runtime initializer
        // expression and immutable binding for calls/reads. Do not mask overflow.
        private static bool IsCompileTimeScalar(StructuredExpr expression) =>
            expression switch
            {
                StructuredInt or StructuredBool => true,
                StructuredNegate unary => IsCompileTimeScalar(unary.Value),
                StructuredBinary binary => IsCompileTimeScalar(binary.Left)
                                           && IsCompileTimeScalar(binary.Right),
                _ => false
            };

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
            if (Take("unwrap_or"))
            {
                Expect("(");
                var fallback = Expr(depth + 1);
                Require(fallback, IrValueType.I64, "Option<int>.unwrap_or fallback must be int");
                Expect(")");
                return new StructuredOptionOr(at, option, fallback);
            }
            throw Error("E_OPTION_METHOD_UNSUPPORTED: use is_some(), is_none(), or unwrap_or(int)");
        }
        private StructuredExpr Expr(int depth) => Conditional(depth + 1);
        private StructuredExpr Conditional(int depth)
        {
            Budget(depth);
            var test = LogicalOr(depth + 1);
            if (Current.Text != "?") return test;
            int at = Current.Offset;
            index++;
            Require(test, IrValueType.Bool, "Conditional test requires bool");
            var yes = Expr(depth + 1);
            Expect(":");
            var no = Conditional(depth + 1);
            if (yes.Type != no.Type)
                throw new FormatException($"Conditional result types differ at offset {at}");
            return CheckedConditional(at, test, yes, no);
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
            while (Current.Text is "xor" or "^^")
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
            var left = Equal(depth + 1);
            while (Current.Text is "and" or "&&")
            {
                int at = Current.Offset;
                index++;
                var right = Equal(depth + 1);
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
            if (array.Type != IrValueType.FixedArrayI64 || !Take("."))
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
        private StructuredExpr Equal(int depth)
        {
            var left = Compare(depth + 1);
            while (Current.Text is "==" or "eq" or "!=" or "neq" or "!eq")
            {
                int at = Current.Offset;
                bool notEqual = Current.Text is "!=" or "neq" or "!eq";
                index++;
                var right = Compare(depth + 1);
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
                    if (left.Type != right.Type)
                        throw new FormatException($"Equality requires matching int or bool operands at offset {at}");
                    var op = left.Type == IrValueType.Bool
                        ? (notEqual ? StructuredOperator.NotEqualBool : StructuredOperator.EqualBool)
                        : (notEqual ? StructuredOperator.NotEqualI64 : StructuredOperator.EqualI64);
                    left = CheckedBinary(at, op, left, right, IrValueType.Bool);
                }
            }
            return left;
        }
        private StructuredExpr Compare(int depth)
        {
            var left = Add(depth + 1);
            while (true)
            {
                if (Current.Text == "as")
                {
                    int castAt = Current.Offset;
                    index++;
                    if (Take("?"))
                        throw new FormatException(
                            $"Optional cast as? requires verified Option<T> lowering at offset {castAt}");
                    if (Current.Text is not ("int" or "bool"))
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
                var right = Add(depth + 1);
                Require(left, IrValueType.I64, "Comparison requires int operands");
                Require(right, IrValueType.I64, "Comparison requires int operands");
                left = CheckedBinary(at, comparison, left, right, IrValueType.Bool);
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
            var left = Unary(depth + 1);
            while (Current.Text == "*")
            {
                int at = Current.Offset;
                index++;
                var right = Unary(depth + 1);
                Require(left, IrValueType.I64, "Multiplication requires int operands");
                Require(right, IrValueType.I64, "Multiplication requires int operands");
                left = CheckedBinary(at, StructuredOperator.Multiply,
                    left, right, IrValueType.I64);
            }
            return left;
        }
        private StructuredExpr Unary(int depth)
        {
            Budget(depth);
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
                    throw new FormatException(
                        $"Legacy ownership spelling 'rt borrow mut' is rejected; use 'rt mut borrow' at offset {at}");
                if (ownership is "copy" or "share" or "take" or "borrow" or "mut borrow")
                    throw new FormatException(
                        $"Ownership operation 'rt {ownership}' requires typed actor-arena lowering; unsupported in the .NET structured backend at offset {at}");

                throw new FormatException(
                    $"Runtime ownership operation requires typed lowering; unsupported in the .NET structured backend at offset {at}");
            }
            if (Take("+"))
            {
                var operand = Unary(depth + 1);
                Require(operand, IrValueType.I64, "Unary plus requires int");
                return operand;
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
                return OptionPostfix(inner, depth);
            }
            int offset = Current.Offset;
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
            bool localBinding = scopes.Any(scope => scope.ContainsKey(name));
            // Option methods on local values must not be reinterpreted as
            // linked module qualification or static method dispatch.
            if (localBinding && Lookup(name, offset).Type == IrValueType.OptionI64)
                return OptionPostfix(new StructuredRead(offset, name, IrValueType.OptionI64), depth);
            if (localBinding && Lookup(name, offset).Type == IrValueType.FixedArrayI64)
                return ArrayPostfix(new StructuredRead(offset, name, IrValueType.FixedArrayI64), depth);
            if (linking is not null)
                while (Take("."))
                {
                    if (localBinding) throw Error("Qualified access on local values requires nominal type lowering");
                    name += "." + Name();
                }
            if (Take("("))
            {
                if (localBinding) throw Error("Local value is not a statically bound callable");
                if (!visible.TryGetValue(name, out var target))
                    throw new FormatException(
                        $"Undefined, recursive or forward typed helper call at offset {offset}");
                var args = new List<StructuredExpr>();
                if (Current.Text != ")")
                {
                    do
                    {
                        if (args.Count >= MaxParameters)
                            throw Error("Typed helper call argument budget exceeded");
                        args.Add(Expr(depth + 1));
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
                return node;
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
