using System.Globalization;

namespace Ores.Net.Compiler;

// Non-executable source AST. The production arithmetic parser and emitter stay
// unchanged until structured CFG lowering has an independent verification gate.
public abstract record StructuredExpr(int Offset, IrValueType Type);
public sealed record StructuredInt(int At, long Value) : StructuredExpr(At, IrValueType.I64);
public sealed record StructuredBool(int At, bool Value) : StructuredExpr(At, IrValueType.Bool);
public sealed record StructuredRead(int At, string Name, IrValueType ValueType)
    : StructuredExpr(At, ValueType);
public sealed record StructuredNegate(int At, StructuredExpr Value)
    : StructuredExpr(At, IrValueType.I64);
// First typed call milestone: statically resolved, expression-only helpers.
// Calls are expanded into verified SSA before emitting any CIL.
public sealed record StructuredCall(int At, string Name,
    IReadOnlyList<StructuredExpr> Arguments, IrValueType ResultType)
    : StructuredExpr(At, ResultType);
public sealed record StructuredParameter(string Name, IrValueType Type);
public sealed record StructuredFunction(string Name,
    IReadOnlyList<StructuredParameter> Parameters, IrValueType ResultType,
    StructuredExpr? ReturnValue, IReadOnlyList<StructuredStmt> Body);
public enum StructuredOperator { Add, Subtract, Multiply, EqualI64, LessThanI64, NotEqualI64 }
public sealed record StructuredBinary(int At, StructuredOperator Operator,
    StructuredExpr Left, StructuredExpr Right, IrValueType ResultType)
    : StructuredExpr(At, ResultType);
public abstract record StructuredStmt(int Offset);
// Compile-time const, single-assignment val, and mutable let are distinct
// source bindings; the experimental structured AST preserves that distinction.
public enum StructuredBindingKind { Let, Val, Const }
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
public static class OresStructuredParser
{
    private const int MaxChars = 1_048_576;
    private const int MaxTokens = 100_000;
    private const int MaxStatements = 16_384;
    private const int MaxDepth = 128;
    private const int MaxIdentifier = 128;
    private const string SingletonUnsupported =
        "ORES-SINGLETON-UNSUPPORTED: singleton declarations require exactly-once complete publication, cycle/failure semantics, runtime-derived actor authority for shared state, and five-backend guest execution parity";
    private const int MaxHelpers = 64;
    private const int MaxParameters = 16;
    private const int MaxLocalTypeFields = 64;

    public static StructuredProgram Parse(string source)
        => ParseCore(source, null).Program;

    internal static StructuredLinkedUnit ParseLinked(string source, LinkedModuleContext linking)
        => ParseCore(source, linking);

    internal static IReadOnlyList<SourceImport> ReadLinkedImports(string source)
    {
        var reader = new Reader(LexLinked(source));
        return reader.ReadImports();
    }

    private static List<Token> LexLinked(string source)
    {
        if (source.Length > MaxChars || System.Text.Encoding.UTF8.GetByteCount(source) > MaxChars)
            throw new FormatException("Source exceeds 1 MiB UTF-8 parser limit");
        var lexer = new Lexer(source, true);
        var tokens = new List<Token>();
        do
        {
            tokens.Add(lexer.Read());
            if (tokens.Count > MaxTokens) throw new FormatException("Structured token budget exceeded");
        } while (tokens[^1].Text.Length != 0);
        return tokens;
    }

    private static StructuredLinkedUnit ParseCore(string source, LinkedModuleContext? linking)
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
        // No structural match lowering or readonly shape metadata in scalar CIL yet.
        StructuralShapeAdmission.RejectUnsupportedPatterns(source);
        var lexer = new Lexer(source, linking is not null);
        var tokens = new List<Token>();
        // Distinguish a forbidden local nominal declaration from an
        // unsupported top-level class/actor before either reaches lowering.
        int braceDepth = 0;
        while (true)
        {
            var token = lexer.Read();
            // This frontend also lexes eagerly: deny contextual singleton
            // declarations before future trait/generic or @Inject tokens.
            // A local declaration gets the same compiler-owned diagnostic.
            if (token.Text == "singleton" &&
                (tokens.Count > 0 && tokens[^1].Text == "define" ||
                 tokens.Count > 1 && tokens[^1].Text == "pub" &&
                 tokens[^2].Text == "define"))
                throw new FormatException(SingletonUnsupported + " at offset " + token.Offset);
            // The scalar bootstrap does not have class/actor dispatch or
            // ownership-aware lexical closures. Refuse these at source admission
            // with a stable capability diagnostic, not an accidental parse error.
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
                throw new FormatException(
                    $"Inverted class requires checked bracket-row and ownership lowering in the .NET backend at offset {token.Offset}");
            if ((token.Text == "class" || token.Text == "actor") &&
                tokens.Count > 0 &&
                (tokens[^1].Text == "define" ||
                 (tokens[^1].Text == "pub" && tokens.Count > 1 &&
                  tokens[^2].Text == "define")))
            {
                if (braceDepth > 0)
                    throw new FormatException(
                        $"Local class/actor declarations are not permitted at offset {token.Offset}");
                throw new FormatException(
                    $"Nominal class/actor methods require receiver-erased method slots, type/borrow checking and Native AOT CIL lowering; unsupported in .NET structured frontend at offset {token.Offset}");
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
            if (token.Text == "{") braceDepth++;
            else if (token.Text == "}" && braceDepth > 0) braceDepth--;
            if (tokens.Count > MaxTokens)
                throw new FormatException("Structured token budget exceeded");
            if (token.Text.Length == 0) break;
        }
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Text == "select" &&
                StaticSelectAdmission.IsStaticSelectFollower(tokens[i + 1].Text))
                throw StaticSelectAdmission.Unsupported(tokens[i].Offset);
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

    private readonly record struct Token(string Text, int Offset);
    private sealed class Lexer(string source, bool moduleMode = false)
    {
        private int index;
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
            if (moduleMode && ch == '"')
            {
                index++;
                while (index < source.Length && source[index] != '"')
                {
                    if (source[index] is '\r' or '\n' or '\0')
                        throw new FormatException($"Invalid string literal at offset {offset}");
                    if (source[index++] == '\\')
                    {
                        if (index == source.Length) break;
                        index++;
                    }
                }
                if (index == source.Length) throw new FormatException($"Unterminated string at offset {offset}");
                index++;
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
                if (word is "cpu" or "gpu" ||
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
            else if (ch == '=' && index + 1 < source.Length &&
                     source[index + 1] == '=') index += 2;
            else if (ch == '!' && index + 1 < source.Length &&
                     source[index + 1] == '=') index += 2;
            else if (moduleMode && index + 1 < source.Length &&
                (source.Substring(index, 2) is "->" or "&&" or "||" or ">=" or "<=")) index += 2;
            else if (ch == '|' && !moduleMode)
                throw new FormatException(
                    $"Lexical captures require checked closure lowering at offset {offset}");
            else if ("()+-*{}:;=<.,?".IndexOf(ch) >= 0 ||
                (moduleMode && "[]/>!|&".IndexOf(ch) >= 0)) index++;
            else throw new FormatException($"Invalid character at offset {offset}");
            var tokenText = source[offset..index];
            if (tokenText == "import" && !moduleMode)
                throw new FormatException("source imports require verified module graph lowering");
            if (tokenText == "enum")
                throw new FormatException(
                    $"nominal enum overloads require compiler-bound dispatch (unsupported .NET bootstrap) at offset {offset}");
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
                if (kind is not ("fnc" or "module")) throw Error("Linked source supports fnc and module imports");
                index++;
                string name = Name();
                Expect("from");
                string quoted = Current.Text;
                if (quoted.Length < 3 || quoted[0] != '"' || quoted[^1] != '"' || quoted.Contains('\\'))
                    throw Error("Import requires an unescaped local source string");
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
                if (Current.Text == "singleton")
                    throw Error(SingletonUnsupported);
                if (Current.Text is "class" or "actor")
                    throw Error("Nominal class/actor methods require receiver-erased method slots, type/borrow checking and Native AOT CIL lowering; unsupported in .NET structured frontend");
                Expect("fnc");
                int at = Current.Offset;
                string name = Name();
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
                        string parameter = Name();
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
                IrValueType resultType = Type();
                if (name == "main" && entry)
                {
                    if (visible.ContainsKey(name)) throw Error("Entry main conflicts with an imported callable");
                    activeReturnType = IrValueType.I64;
                    if (parameters.Count != 0 || resultType != IrValueType.I64)
                        throw new FormatException($"main must have signature ():int at offset {at}");
                    var (body, returns) = Block(0);
                    if (!returns) throw Error("Not every path returns int");
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
                if (!helperReturns)
                    throw Error("Not every typed helper path returns");
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
        private (StructuredStmt Statement, bool Returns) Statement(int depth)
        {
            Budget(depth);
            int at = Current.Offset;
            // A local singleton must not degrade into an unrelated assignment
            // error or accidentally reach a future statement lowering path.
            // Match only declaration modifier sequences, not an identifier
            // named singleton in an ordinary expression or binding.
            if ((Current.Text == "define" && index + 1 < tokens.Count &&
                 (tokens[index + 1].Text == "singleton" ||
                  (tokens[index + 1].Text == "pub" && index + 2 < tokens.Count &&
                   tokens[index + 2].Text == "singleton"))) ||
                (Current.Text == "pub" && index + 2 < tokens.Count &&
                 tokens[index + 1].Text == "define" && tokens[index + 2].Text == "singleton"))
                throw Error(SingletonUnsupported);
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
            if (Take("return"))
            {
                var value = Expr(depth + 1);
                Require(value, activeReturnType,
                    "Return value does not match function result type");
                Expect(";");
                return (new StructuredReturn(at, value), true);
            }
            if (Current.Text is "let" or "val" or "const")
            {
                StructuredBindingKind kind = Current.Text switch
                {
                    "let" => StructuredBindingKind.Let,
                    "val" => StructuredBindingKind.Val,
                    _ => StructuredBindingKind.Const
                };
                index++;
                var type = Type();
                int nameAt = Current.Offset;
                string name = Name();
                if (scopes.Any(scope => scope.ContainsKey(name)))
                    throw new FormatException($"Duplicate local binding at offset {nameAt}");
                Expect("=");
                var initializer = Expr(depth + 1);
                Require(initializer, type, "Local initializer has wrong type");
                long? constantValue = null;
                if (kind == StructuredBindingKind.Const)
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
                var (yes, yesReturns) = Block(depth + 1);
                IReadOnlyList<StructuredStmt>? no = null;
                bool noReturns = false;
                if (Take("else")) (no, noReturns) = Block(depth + 1);
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
            if (Take("int")) return IrValueType.I64;
            if (Take("bool")) return IrValueType.Bool;
            throw Error("Expected int or bool type");
        }
        private string Name()
        {
            string name = Current.Text;
            int at = Current.Offset;
            if (name.Length is < 1 or > MaxIdentifier ||
                !(Alpha(name[0]) || name[0] == '_') ||
                !name.Skip(1).All(c => Alpha(c) || Digit(c) || c == '_') ||
                name is "if" or "else" or "while" or "return" or "let"
                    or "int" or "bool" or "true" or "false"
                    or "define" or "pub" or "fnc" or "const" or "val" or "final"
                    or "struct" or "record" or "class" or "actor"
                    or "module" or "import" or "from" or "as" or "end"
                    or "routine" or "async" or "isoactor")
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

        // This closed scalar subset evaluates const initializers at admission
        // time with checked i64 semantics. Calls and runtime locals fail closed.
        private static long ConstantValue(StructuredExpr expression)
        {
            switch (expression)
            {
                case StructuredInt intLiteral: return intLiteral.Value;
                case StructuredBool boolLiteral: return boolLiteral.Value ? 1L : 0L;
                case StructuredNegate negate:
                    return checked(-ConstantValue(negate.Value));
                case StructuredBinary binary:
                    long left = ConstantValue(binary.Left);
                    long right = ConstantValue(binary.Right);
                    return binary.Operator switch
                    {
                        StructuredOperator.Add => checked(left + right),
                        StructuredOperator.Subtract => checked(left - right),
                        StructuredOperator.Multiply => checked(left * right),
                        StructuredOperator.EqualI64 => left == right ? 1L : 0L,
                        StructuredOperator.NotEqualI64 => left != right ? 1L : 0L,
                        StructuredOperator.LessThanI64 => left < right ? 1L : 0L,
                        _ => throw new FormatException(
                            $"Unsupported const operator at offset {binary.Offset}")
                    };
                default:
                    throw new FormatException(
                        $"const initializer must be a compile-time scalar at offset {expression.Offset}");
            }
        }
        private StructuredExpr Expr(int depth) => Equal(depth + 1);
        private StructuredExpr Equal(int depth)
        {
            var left = Compare(depth + 1);
            while (Current.Text is "==" or "eq" or "!=" or "neq")
            {
                int at = Current.Offset;
                bool notEqual = Current.Text is "!=" or "neq";
                index++;
                var right = Compare(depth + 1);
                Require(left, IrValueType.I64, "Equality requires int operands");
                Require(right, IrValueType.I64, "Equality requires int operands");
                left = CheckedBinary(at,
                    notEqual ? StructuredOperator.NotEqualI64 : StructuredOperator.EqualI64,
                    left, right, IrValueType.Bool);
            }
            return left;
        }
        private StructuredExpr Compare(int depth)
        {
            var left = Add(depth + 1);
            while (Current.Text == "<")
            {
                int at = Current.Offset;
                index++;
                var right = Add(depth + 1);
                Require(left, IrValueType.I64, "Less-than requires int operands");
                Require(right, IrValueType.I64, "Less-than requires int operands");
                left = CheckedBinary(at, StructuredOperator.LessThanI64,
                    left, right, IrValueType.Bool);
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
                return inner;
            }
            int offset = Current.Offset;
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
            if (binding.Kind == StructuredBindingKind.Const)
            {
                long constant = binding.ConstantValue!.Value;
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
