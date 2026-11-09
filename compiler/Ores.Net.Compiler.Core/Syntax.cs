using System.Globalization;

namespace Ores.Net.Compiler;

public abstract record Expression;
public sealed record Literal(long Value) : Expression;
public sealed record Unary(char Operator, Expression Value) : Expression;
public sealed record Binary(char Operator, Expression Left, Expression Right) : Expression;
// Named intrinsic only. The '^' operator remains forbidden in Oreslang.
public sealed record NamedBitXor(Expression Left, Expression Right) : Expression;

public static class OresParser
{
    private const int MaxDepth = 128;
    private const int MaxTokens = 100_000;

    public static Expression Parse(string source)
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
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
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
                if (word is "any" or "unknown" or "Any" or "Unknown")
                    throw new FormatException(
                        "any/unknown requires tagged opaque-value ABI and Native AOT runtime descriptors");
                // This bootstrap compiler is single-file. Do not accept Java
                // project-root, zed package or bare import declarations until
                // a verified source graph / native AOT linker exists.
                if (word == "import")
                    throw new FormatException("source imports require verified module graph lowering");
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
            if ("()+-*/{}:;,.=?[]<>~".IndexOf(ch) >= 0)
            {
                _i++;
                return new Token(text[start.._i], start);
            }
            if (ch == '%')
                throw new FormatException($"noncanonical '%' source operator; use named Integer.rem/mod at offset {start}");
            if (ch == '^')
            {
                // Reserved AttoLang syntax is rejected after lexical trivia and
                // before any scalar instruction reaches typed IR or CIL.
                bool doubleCaret = _i + 1 < text.Length && text[_i + 1] == '^';
                string spelling = doubleCaret ? "^^" : "^";
                string named = doubleCaret ? "Xor(a, b)" : "Integer.bitXor(a, b)";
                throw new FormatException(
                    $"Reserved AttoLang operator '{spelling}'; use {named} at offset {start}");
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

        public Expression ParseProgram()
        {
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
            // The scalar subset admits only this one typed Integer intrinsic.
            // Parser-owned name resolution rejects arbitrary Integer.* calls.
            // A first native, escaping-free product construction slice:
            // Tuple.of(<i64 literals...>)[constant_slot] / ~tuple(...)[slot].
            // All operands are pure checked constants, so projecting the
            // selected SSA scalar preserves the exact source evaluation
            // behavior without allocating a host tuple or losing an effect.
            if (Current.Text is "Tuple" or "Record" or "Entry" or "Pair" or "~")
            {
                int at = Current.Offset;
                if (Take("~"))
                {
                    if (Take("tuple")) return ParseScalarizedTupleFactory(depth + 1, at);
                    if (Take("record")) return ParseScalarizedRecordFactory(depth + 1, at);
                    if (Take("entry")) return ParseScalarizedBinaryProductFactory(depth + 1, at, isEntry: true);
                    if (Take("pair")) return ParseScalarizedBinaryProductFactory(depth + 1, at, isEntry: false);
                    throw new FormatException(
                        $"E_COLLECTION_FACTORY_UNSUPPORTED: ~kind requires verified owned collection lowering at offset {at}");
                }
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
