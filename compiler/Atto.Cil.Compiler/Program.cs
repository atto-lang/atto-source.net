using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Atto.Cil.Compiler;

internal sealed class CompilerError(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
internal abstract record Expression;
internal sealed record Literal(long Number) : Expression;
internal sealed record Arg(string Name) : Expression;
internal sealed record Binary(string Op, Expression Left, Expression Right) : Expression;
internal sealed record Call(string Name, List<Expression> Args) : Expression;
// A bounded, integer-only return-position match. Binders are scoped to each arm.
// The match subject is evaluated exactly once into a CIL local.
internal sealed record ValueArm(long? Literal, string? Binding, Expression? Guard, Expression Result);
internal sealed record ValueMatch(Expression Subject, List<ValueArm> Arms) : Expression;
// The admitted statement profile is deliberately bounded: output statements
// execute in source order, then one final return. No silent fallthrough.
internal abstract record Statement;
internal sealed record LocalBinding(string Name, Expression Initializer) : Statement;
internal sealed record PrintLine(Expression Value) : Statement;
internal sealed record ReturnStmt(Expression Value) : Statement;
// Signed i64 guest results; bool is internal to guards.
internal enum CheckedType { Int, Bool }
internal sealed record Function(string Name, List<string> Args,
    CheckedType ReturnType, List<Statement> Body);

internal sealed class Parser
{
    private readonly List<string> _tokens = [];
    private int _position;
    private string Peek => _tokens[_position];
    private bool Eat(string s) { if (Peek != s) return false; _position++; return true; }
    private void Require(string s)
    {
        if (!Eat(s)) throw new CompilerError("E_PARSE", $"expected {s}; got {Peek}");
    }
    private string Name()
    {
        var name = Peek;
        if (name.Length == 0 || !(name[0] is >= 'a' and <= 'z' or '_') ||
            name.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
            name is "fnc" or "pub" or "return" or "int" or "float" or "bool" or "val" or "let"
                or "true" or "false" or "actor" or "unsafe" or "await" or "async"
                or "susp" or "suspends" or "critical" or "trap" or "panic"
                or "raise" or "throw" or "borrow" or "take" or "share"
                or "copy" or "drop" or "pin" or "rt" or "io" or "stdio"
                or "process" or "spawn" or "select" or "import" or "module"
                or "define" or "namespace" or "use" or "pipe" or "rpipe"
                or "sync" or "task" or "yield")
            throw new CompilerError("E_NAME", $"expected snake_case identifier, got {name}");
        _position++;
        return name;
    }
    internal Parser(string source)
    {
        if (Encoding.UTF8.GetByteCount(source) > 65536)
            throw new CompilerError("E_LIMIT", "64 KiB source limit");
        foreach (char c in source)
            if ((char.IsControl(c) && c is not ('\t' or '\n' or '\r')) ||
                char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                throw new CompilerError("E_SOURCE", "unsafe source control or format character");
        for (int i = 0; i < source.Length;)
        {
            var c = source[i];
            if (c is ' ' or '\t' or '\r' or '\n') { i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c > 127) throw new CompilerError("E_SOURCE", "non-ASCII program token");
            int start = i++;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')
                while (i < source.Length && (source[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) i++;
            else if (c is >= '0' and <= '9')
                while (i < source.Length && source[i] is >= '0' and <= '9') i++;
            else if (c == '-' && i < source.Length && source[i] == '>') i++;
            else if (c == '=' && i < source.Length && source[i] == '=') i++;
            else if (c == '|' && i < source.Length && source[i] == '>') i++;
            else if (c == '<' && i < source.Length && source[i] == '|') i++;
            // Tokenize < so unsupported nominal ADT patterns reach the
            // typed E_UNSUPPORTED_TYPE gate; it is not a scalar operator.
            else if (!"(){}:;,+-*/^<>=.".Contains(c))
                throw new CompilerError("E_LEX", $"unsupported source character at {start}");
            _tokens.Add(source[start..i]);
            if (_tokens.Count > 8192) throw new CompilerError("E_LIMIT", "8192 token limit");
        }
        _tokens.Add("<eof>");
    }
    internal List<Function> Parse()
    {
        // File-scoped namespace metadata is distinct from a module/import.
        // It may occur exactly once, and only before any declarations.
        bool hasNamespace = false;
        if (Eat("use"))
        {
            Require("namespace");
            _ = Name();
            Require(";");
            hasNamespace = true;
        }
        List<Function> result = [];
        while (Peek != "<eof>")
        {
            if (Peek == "use" || Peek == "namespace")
                throw new CompilerError(
                    hasNamespace ? "E_NAMESPACE_DUPLICATE" : "E_NAMESPACE_HEADER_ORDER",
                    "namespace headers must occur exactly once at the start of the source file");
            Eat("pub"); Require("fnc");
            string name = Name();
            Require("(");
            List<string> args = [];
            if (Peek != ")") do
            {
                Require("int");
                var arg = Name();
                if (args.Contains(arg)) throw new CompilerError("E_DUP_PARAM", arg);
                args.Add(arg);
                if (args.Count > 8) throw new CompilerError("E_LIMIT", "8 parameter limit");
            } while (Eat(","));
            Require(")");
            bool colonReturn = Eat(":");
            if (!colonReturn) Require("->");
            if (!Eat("int"))
                throw new CompilerError("E_RETURN_TYPE", "direct CIL requires an int return type");
            const CheckedType returnType = CheckedType.Int;
            bool arrow = colonReturn && Eat("->");
            List<Statement> statements = [];
            if (arrow && Peek != "{")
            {
                statements.Add(new ReturnStmt(ParseExpr(0, 0)));
                Require(";");
                result.Add(new Function(name, args, returnType, statements));
                if (result.Count > 64) throw new CompilerError("E_LIMIT", "64 function limit");
                continue;
            }
            Require("{");
            int localCount = 0;
            bool outputStarted = false;
            while (Peek != "}" && Peek != "<eof>")
            {
                if (statements.Count >= 64)
                    throw new CompilerError("E_LIMIT", "64 function statement limit");
                if (Eat("return") || Eat("^"))
                {
                    var expression = ParseExpr(0, 0);
                    if (!Eat(";") && expression is not ValueMatch)
                        throw new CompilerError("E_PARSE", "expected ; after return");
                    statements.Add(new ReturnStmt(expression));
                    if (Peek != "}")
                        throw new CompilerError("E_RETURN", "statements after final return are forbidden");
                    break;
                }
                if (Eat("val"))
                {
                    if (outputStarted) throw new CompilerError("E_PARSE", "locals must precede output");
                    if (localCount++ >= 4) throw new CompilerError("E_LIMIT", "4 local bindings per function");
                    var localName = Name();
                    Require("=");
                    var initializer = ParseExpr(0, 0);
                    Require(";");
                    statements.Add(new LocalBinding(localName, initializer));
                    continue;
                }
                if (Eat("stdio"))
                {
                    outputStarted = true;
                    if (!Eat(".") || !Eat("println"))
                        throw new CompilerError("E_STDIO", "only stdio.println(int) is supported");
                    Require("(");
                    if (Eat(")"))
                        throw new CompilerError("E_STDIO", "stdio.println requires exactly one int");
                    var value = ParseExpr(0, 0);
                    if (!Eat(")"))
                        throw new CompilerError("E_STDIO", "stdio.println requires exactly one int");
                    Require(";");
                    statements.Add(new PrintLine(value));
                    continue;
                }
                throw new CompilerError("E_PARSE", "unsupported function statement");
            }
            if (statements.Count == 0 || statements[^1] is not ReturnStmt)
                throw new CompilerError("E_PARSE", "function requires a final return");
            Require("}");
            result.Add(new Function(name, args, returnType, statements));
            if (result.Count > 64) throw new CompilerError("E_LIMIT", "64 function limit");
        }
        return result;
    }
    private List<Expression> Arguments(int depth)
    {
        Require("(");
        List<Expression> args = [];
        if (Peek != ")") do
        {
            args.Add(ParseExpr(0, depth + 1));
            if (args.Count > 8) throw new CompilerError("E_LIMIT", "8 call arguments");
        } while (Eat(","));
        Require(")");
        return args;
    }

    private Expression ParseExpr(int min, int depth, string pipeDirection = "")
    {
        if (depth > 32) throw new CompilerError("E_LIMIT", "expression nesting limit");
        Expression left;
        if (Eat("(")) { left = ParseExpr(0, depth + 1); Require(")"); }
        else if (Eat("-"))
        {
            // Exactly 2^63 is permitted only as a *negative* signed-i64
            // literal. Never parse it as an overflowing positive Int64.
            if (Peek == "9223372036854775808")
            {
                _position++;
                left = new Literal(long.MinValue);
            }
            else left = new Binary("-", new Literal(0), ParseExpr(30, depth + 1));
        }
        else if (Eat("match")) left = ParseValueMatch(depth + 1);
        else if (Peek.Length > 0 && Peek[0] is >= '0' and <= '9')
        {
            var raw = Peek; _position++;
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw new CompilerError("E_LITERAL", "64-bit signed literal overflow");
            left = new Literal(value);
        }
        else if (Peek is "true" or "false")
            throw new CompilerError("E_TYPE", "boolean literal outside direct scalar CIL profile");
        else
        {
            var name = Name();
            if (Eat("("))
            {
                List<Expression> args = [];
                if (Peek != ")") do
                {
                    args.Add(ParseExpr(0, depth + 1));
                    if (args.Count > 8) throw new CompilerError("E_LIMIT", "8 call argument limit");
                } while (Eat(","));
                Require(")");
                left = new Call(name, args);
            }
            else left = new Arg(name);
        }
        while (true)
        {
            int precedence = Peek is "*" or "/" ? 20 : Peek is "+" or "-" ? 10
                : Peek is ">" or "eq" or "==" ? 5 : Peek is "|>" or "<|" ? 1 : -1;
            if (precedence < min) break;
            var op = Peek; _position++;
            if (op is "|>" or "<|")
            {
                if (pipeDirection.Length != 0 && pipeDirection != op)
                    throw new CompilerError("E_PARSE", "mixed pipeline directions require parentheses");
                pipeDirection = op;
                if (op == "|>")
                {
                    var target = Name();
                    List<Expression> args = Peek == "(" ? Arguments(depth + 1) : [];
                    if (args.Count >= 8) throw new CompilerError("E_LIMIT", "8 call arguments");
                    args.Insert(0, left);
                    left = new Call(target, args);
                }
                else
                {
                    string target;
                    List<Expression> args;
                    if (left is Arg a) { target = a.Name; args = []; }
                    else if (left is Call c) { target = c.Name; args = new List<Expression>(c.Args); }
                    else throw new CompilerError("E_PARSE", "reverse pipeline requires a named function");
                    if (args.Count >= 8) throw new CompilerError("E_LIMIT", "8 call arguments");
                    var rhs = ParseExpr(precedence, depth + 1, op);
                    args.Insert(0, rhs);
                    left = new Call(target, args);
                }
            }
            else
                left = new Binary(op, left, ParseExpr(precedence + 1, depth + 1));
        }
        return left;
    }

    private Expression ParseValueMatch(int depth)
    {
        if (depth > 16) throw new CompilerError("E_LIMIT", "match nesting limit");
        var subject = ParseExpr(0, depth + 1);
        Require("over");
        List<ValueArm> arms = [];
        while (Peek != "end")
        {
            if (Peek == "<eof>") throw new CompilerError("E_PARSE", "expected end of match");
            if (arms.Count >= 128) throw new CompilerError("E_LIMIT", "match arm limit");
            Require("on");
            // Scalar-only CIL matches must not silently treat the 'is' in
            // on is Ok<line> as a catch-all binding. Until Result/Option
            // lowering exists, reject the nominal variant profile explicitly.
            if (Peek == "is")
                throw new CompilerError("E_UNSUPPORTED_TYPE",
                    "on is Ok/Err/Some/None requires typed ADT CIL lowering");
            long? literal = null;
            string? binder = null;
            if (Eat("_"))
            {
                // Wildcard: no binding.
            }
            else if (Peek.Length > 0 && Peek[0] is >= '0' and <= '9')
            {
                var raw = Peek;
                _position++;
                if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    throw new CompilerError("E_LITERAL", "match pattern outside signed i64");
                literal = n;
            }
            else if (Eat("-"))
            {
                var raw = Peek;
                _position++;
                if (!long.TryParse("-" + raw, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var n))
                    throw new CompilerError("E_LITERAL", "negative match pattern outside signed i64");
                literal = n;
            }
            else
            {
                binder = Name();
                if (binder is "on" or "when" or "over" or "end" or "match")
                    throw new CompilerError("E_PARSE", "invalid match binder");
            }
            Expression? guard = Eat("when") ? ParseExpr(0, depth + 1) : null;
            Require("->");
            Expression result;
            bool braced = Eat("{");
            if (braced)
            {
                // Scope is deliberately narrow: only an explicit return is
                // accepted; silently falling through is never a value arm.
                if (!Eat("return") && !Eat("^"))
                    throw new CompilerError("E_RETURN", "match block arm must explicitly return");
                result = ParseExpr(0, depth + 1);
                Require(";");
                Require("}");
                Eat(",");
            }
            else
            {
                result = ParseExpr(0, depth + 1);
                if (!Eat(",") && Peek != "end")
                    throw new CompilerError("E_PARSE", "expected comma after value arm");
            }
            arms.Add(new ValueArm(literal, binder, guard, result));
        }
        Require("end");
        if (arms.Count == 0) throw new CompilerError("E_MATCH", "empty match");
        return new ValueMatch(subject, arms);
    }
}

internal sealed class CIL(List<Function> functions, bool allowStdio)
{
    private readonly List<Function> _functions = functions;
    private readonly Dictionary<string, int> _symbols = new(StringComparer.Ordinal);

    internal void Check()
    {
        for (int i = 0; i < _functions.Count; i++)
            if (!_symbols.TryAdd(_functions[i].Name, i))
                throw new CompilerError("E_DUP_FUNCTION", _functions[i].Name);
        if (!_symbols.TryGetValue("main", out var index) ||
            _functions[index].Args.Count != 0 ||
            _functions[index].ReturnType != CheckedType.Int)
            throw new CompilerError("E_ENTRY", "requires main(): int with no args");
        int[] state = new int[_functions.Count];
        for (int i = 0; i < state.Length; i++) WalkFunction(i);

        void WalkFunction(int id)
        {
            if (state[id] == 2) return;
            if (state[id] == 1) throw new CompilerError("E_CYCLE", _functions[id].Name);
            state[id] = 1;
            var scope = new HashSet<string>(StringComparer.Ordinal);
            foreach (var statement in _functions[id].Body)
            {
                switch (statement)
                {
                    case LocalBinding l:
                        if (WalkExpr(l.Initializer, _functions[id], scope, 0) != CheckedType.Int)
                            throw new CompilerError("E_TYPE", "local initializer must be int");
                        if (_functions[id].Args.Contains(l.Name) || !scope.Add(l.Name))
                            throw new CompilerError("E_DUP_LOCAL", l.Name);
                        break;
                    case PrintLine p:
                        if (!allowStdio)
                            throw new CompilerError("E_PERMISSION", "stdio requires host --allow-stdio");
                        if (WalkExpr(p.Value, _functions[id],
                            scope, 0) != CheckedType.Int)
                            throw new CompilerError("E_TYPE", "stdio.println expects int");
                        break;
                    case ReturnStmt r:
                    {
                        CheckedType resultType = WalkExpr(r.Value, _functions[id],
                            scope, 0);
                        CheckedType declared = _functions[id].ReturnType;
                        if (resultType != declared)
                            throw new CompilerError("E_RETURN_TYPE",
                                "return expression does not match declared function type");
                        break;
                    }
                    default:
                        throw new CompilerError("E_PARSE", "unsupported function statement");
                }
            }
            state[id] = 2;
        }

        CheckedType WalkExpr(Expression e, Function owner, HashSet<string> bound, int depth)
        {
            if (depth > 16) throw new CompilerError("E_LIMIT", "match nesting limit");
            switch (e)
            {
                case Literal: return CheckedType.Int;
                case Arg a:
                    if (!owner.Args.Contains(a.Name) && !bound.Contains(a.Name))
                        throw new CompilerError("E_SYMBOL", a.Name);
                    return CheckedType.Int;
                case Binary b:
                {
                    CheckedType lhs = WalkExpr(b.Left, owner, bound, depth);
                    CheckedType rhs = WalkExpr(b.Right, owner, bound, depth);
                    if (lhs != CheckedType.Int || rhs != CheckedType.Int)
                        throw new CompilerError("E_TYPE",
                            "integer operator requires two signed int64 operands");
                    if (b.Op == "/") CheckIntegerDivision(b);
                    return b.Op is ">" or "eq" or "==" ? CheckedType.Bool : CheckedType.Int;
                }
                case Call c:
                    if (!_symbols.TryGetValue(c.Name, out var target))
                        throw new CompilerError("E_SYMBOL", c.Name);
                    if (_functions[target].Args.Count != c.Args.Count)
                        throw new CompilerError("E_ARITY", c.Name);
                    foreach (var arg in c.Args)
                        if (WalkExpr(arg, owner, bound, depth) != CheckedType.Int)
                            throw new CompilerError("E_TYPE", "function expects int arguments");
                    WalkFunction(target);
                    return _functions[target].ReturnType;
                case ValueMatch m:
                    if (WalkExpr(m.Subject, owner, bound, depth + 1) != CheckedType.Int)
                        throw new CompilerError("E_TYPE", "match subject must be int");
                    HashSet<long> unguarded = [];
                    bool covered = false;
                    foreach (var arm in m.Arms)
                    {
                        if (covered || (arm.Literal is long n && unguarded.Contains(n)))
                            throw new CompilerError("E_MATCH", "unreachable match arm");
                        var armScope = new HashSet<string>(bound, StringComparer.Ordinal);
                        if (arm.Binding != null) armScope.Add(arm.Binding);
                        if (arm.Guard != null &&
                            WalkExpr(arm.Guard, owner, armScope, depth + 1) != CheckedType.Bool)
                            throw new CompilerError("E_TYPE", "match guard must be bool");
                        if (WalkExpr(arm.Result, owner, armScope, depth + 1) != CheckedType.Int)
                            throw new CompilerError("E_TYPE", "match result must be int");
                        if (arm.Guard is null)
                        {
                            if (arm.Literal is long literal) unguarded.Add(literal);
                            else covered = true;
                        }
                    }
                    if (!covered)
                        throw new CompilerError("E_MATCH", "non-exhaustive match: unguarded on _ required");
                    return CheckedType.Int;
                default: throw new CompilerError("E_TYPE", "unsupported expression");
            }
        }
    }

    // The direct CIL profile has no typed guest unwind for hardware division
    // faults. Admit signed div only when neither fault can occur.
    private static void CheckIntegerDivision(Binary expression)
    {
        long? divisor = SignedLiteral(expression.Right);
        if (divisor is null)
            throw new CompilerError("E_I64_DIVISION_UNVERIFIED",
                "nonliteral divisor requires typed guest division errors");
        if (divisor == 0)
            throw new CompilerError("E_I64_DIVISION_BY_ZERO", "integer divisor is zero");
        if (divisor != -1) return;

        long? dividend = SignedLiteral(expression.Left);
        if (dividend == long.MinValue)
            throw new CompilerError("E_I64_DIVISION_OVERFLOW", "minimum signed int divided by -1");
        if (dividend is null)
            throw new CompilerError("E_I64_DIVISION_UNVERIFIED",
                "divisor -1 requires proof that the dividend cannot be signed minimum");
    }

    private static long? SignedLiteral(Expression expression) => expression switch
    {
        Literal value => value.Number,
        Binary("-", Literal { Number: 0 }, Literal { Number: >= 0 } magnitude)
            => -magnitude.Number,
        _ => null
    };

    internal void Emit(string destination, string source)
    {
        Check();
        var metadata = new MetadataBuilder();
        var ilStream = new BlobBuilder();
        var bodyStream = new MethodBodyStreamEncoder(ilStream);
        var mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(source)).AsSpan(0, 16));
        metadata.AddModule(0, metadata.GetOrAddString("AttoGuest.dll"),
            metadata.GetOrAddGuid(mvid), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("AttoGuest"),
            new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var runtimeRef = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"), new Version(8, 0, 0, 0), default,
            metadata.GetOrAddBlob(new byte[] {0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a}),
            default, default);
        var objectRef = metadata.AddTypeReference(runtimeRef,
            metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));

        // Only a trusted host-approved CLI profile may bind guest stdout to
        // process-global System.Console. This grant is not a TenantVM
        // capability proof; untrusted tenants must use host-owned channels.
        MemberReferenceHandle consoleWriteLine = default;
        if (_functions.Any(f => f.Body.Any(stmt => stmt is PrintLine)))
        {
            if (!allowStdio)
                throw new CompilerError("E_PERMISSION", "stdio requires host --allow-stdio");
            var consoleRef = metadata.AddAssemblyReference(
                metadata.GetOrAddString("System.Console"), new Version(8, 0, 0, 0),
                default, metadata.GetOrAddBlob(new byte[] {
                    0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a
                }), default, default);
            var consoleType = metadata.AddTypeReference(consoleRef,
                metadata.GetOrAddString("System"), metadata.GetOrAddString("Console"));
            var signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature().Parameters(1,
                ret => ret.Void(),
                pars => pars.AddParameter().Type().Int64());
            consoleWriteLine = metadata.AddMemberReference(consoleType,
                metadata.GetOrAddString("WriteLine"), metadata.GetOrAddBlob(signature));
        }

        // Four immutable local slots plus the disjoint nested match slots.
        // One initialized i64 slot per maximum nested match level.
        // Storing the subject once prevents repeated calls or side effects.
        var localSig = new BlobBuilder();
        var locals = new BlobEncoder(localSig).LocalVariableSignature(21);
        for (int i = 0; i < 21; i++) locals.AddVariable().Type().Int64();
        var localSigHandle = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(localSig));

        List<int> bodyOffsets = [];
        foreach (var f in _functions)
        {
            var code = new BlobBuilder();
            var il = new InstructionEncoder(code, new ControlFlowBuilder());
            var bindings = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var statement in f.Body)
            {
                switch (statement)
                {
                    case LocalBinding l:
                        Generate(l.Initializer, f, il, bindings, 0);
                        int slot = bindings.Count;
                        il.StoreLocal(slot);
                        bindings.Add(l.Name, slot);
                        break;
                    case PrintLine p:
                        Generate(p.Value, f, il, bindings, 0);
                        il.Call(consoleWriteLine);
                        break;
                    case ReturnStmt r:
                        Generate(r.Value, f, il, bindings, 0);
                        il.OpCode(ILOpCode.Ret);
                        break;
                    default:
                        throw new CompilerError("E_INTERNAL", "unchecked statement");
                }
            }
            bodyOffsets.Add(bodyStream.AddMethodBody(il, maxStack: 64,
                localVariablesSignature: localSigHandle));
        }
        int nextParameter = 1;
        for (int i = 0; i < _functions.Count; i++)
        {
            var f = _functions[i];
            var signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature().Parameters(
                f.Args.Count,
                r => r.Type().Int64(),
                p => { foreach (var arg in f.Args) p.AddParameter().Type().Int64(); });
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodImplAttributes.IL, metadata.GetOrAddString(f.Name),
                metadata.GetOrAddBlob(signature), bodyOffsets[i],
                MetadataTokens.ParameterHandle(nextParameter));
            for (int j = 0; j < f.Args.Count; j++)
            {
                metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString(f.Args[j]), j + 1);
                nextParameter++;
            }
        }
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class |
            TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString("AttoGenerated"), metadata.GetOrAddString("Functions"),
            objectRef, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var builder = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), ilStream, flags: CorFlags.ILOnly,
            deterministicIdProvider: static blobs =>
            {
                // ManagedPEBuilder's default content ID is time-based. Hash
                // the actual ordered PE blob bytes instead of a clock or path.
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var blob in blobs)
                    hash.AppendData(blob.GetBytes().AsSpan());
                return BlobContentId.FromHash(hash.GetHashAndReset());
            });
        var output = new BlobBuilder();
        builder.Serialize(output);
        var finalPath = Path.GetFullPath(destination);
        // A successfully compiled guest must never clobber a previously
        // published DLL, nor may failed admission delete an older artifact.
        // Parents are pre-existing trusted build directories; a hostile parent
        // requires an additional directory-descriptor/ACL security boundary.
        var parent = Path.GetDirectoryName(finalPath)!;
        if (!Directory.Exists(parent))
            throw new CompilerError("E_OUTPUT", "output directory must already exist");
        if (File.Exists(finalPath) || Directory.Exists(finalPath))
            throw new CompilerError("E_OUTPUT", "refusing to overwrite existing guest");
        var temporary = finalPath + ".tmp";
        bool ownTemporary = false;
        try
        {
            // CreateNew is exclusive: a competing regular file or symlink
            // at the temporary pathname cannot be truncated by our writer.
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                ownTemporary = true;
                output.WriteContentTo(stream);
                stream.Flush(flushToDisk: true);
            }
            // No-overwrite publication, including the race between the
            // earlier output existence check and the final move.
            File.Move(temporary, finalPath, overwrite: false);
            ownTemporary = false;
        }
        finally
        {
            if (ownTemporary && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void Generate(Expression e, Function f, InstructionEncoder il,
        Dictionary<string, int> bindings, int matchDepth)
    {
        switch (e)
        {
            case Literal c: il.LoadConstantI8(c.Number); return;
            case Arg a:
                if (bindings.TryGetValue(a.Name, out var slot)) il.LoadLocal(slot);
                else il.LoadArgument(f.Args.IndexOf(a.Name));
                return;
            case Binary b:
                if (b.Op == "/") CheckIntegerDivision(b);
                Generate(b.Left, f, il, bindings, matchDepth);
                Generate(b.Right, f, il, bindings, matchDepth);
                il.OpCode(b.Op switch
                {
                    "+" => ILOpCode.Add_ovf,
                    "-" => ILOpCode.Sub_ovf,
                    "*" => ILOpCode.Mul_ovf,
                    "/" => ILOpCode.Div,
                    ">" => ILOpCode.Cgt,
                    "eq" or "==" => ILOpCode.Ceq,
                    _ => throw new CompilerError("E_PARSE", b.Op)
                });
                return;
            case Call c:
                foreach (var arg in c.Args) Generate(arg, f, il, bindings, matchDepth);
                il.Call(MetadataTokens.MethodDefinitionHandle(_symbols[c.Name] + 1));
                return;
            case ValueMatch m:
                if (matchDepth >= 16) throw new CompilerError("E_LIMIT", "match nesting limit");
                Generate(m.Subject, f, il, bindings, matchDepth + 1);
                il.StoreLocal(4 + matchDepth);
                var done = il.DefineLabel();
                foreach (var arm in m.Arms)
                {
                    var next = il.DefineLabel();
                    if (arm.Literal is long literal)
                    {
                        il.LoadLocal(4 + matchDepth);
                        il.LoadConstantI8(literal);
                        il.Branch(ILOpCode.Bne_un, next);
                    }
                    var armBindings = new Dictionary<string, int>(bindings, StringComparer.Ordinal);
                    if (arm.Binding != null) armBindings[arm.Binding] = 4 + matchDepth;
                    if (arm.Guard != null)
                    {
                        Generate(arm.Guard, f, il, armBindings, matchDepth + 1);
                        il.Branch(ILOpCode.Brfalse, next);
                    }
                    Generate(arm.Result, f, il, armBindings, matchDepth + 1);
                    il.Branch(ILOpCode.Br, done);
                    il.MarkLabel(next);
                }
                // The checker requires an unguarded catchall, making this
                // unreachable. Retain an explicit trap if invariants drift.
                il.OpCode(ILOpCode.Ldnull);
                il.OpCode(ILOpCode.Throw);
                il.MarkLabel(done);
                return;
            default: throw new CompilerError("E_TYPE", "unexpected expression");
        }
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if ((args.Length != 2 && args.Length != 3) ||
            !args[0].EndsWith(".atto", StringComparison.Ordinal) ||
            !args[1].EndsWith(".dll", StringComparison.Ordinal) ||
            (args.Length == 3 && args[2] != "--allow-stdio"))
        {
            Console.Error.WriteLine("usage: atto-cil input.atto output.dll [--allow-stdio]");
            return 64;
        }
        try
        {
            if (new FileInfo(args[0]).Length > 65536)
                throw new CompilerError("E_LIMIT", "64 KiB source limit");
            string source = File.ReadAllText(args[0], new UTF8Encoding(false, true));
            // Actor state attachments have a checked target grammar but no
            // verified CIL state layout, startup guard or actor mailbox ABI.
            // Refuse before any generated guest bytes are written.
            if (ActorStateAdmission.Inspect(source) is not null)
                throw new CompilerError("E_UNSUPPORTED_ACTOR_STATE",
                    "attached actor state parsed, but guarded CIL lowering and actor lifecycle are unavailable");
            var fns = new Parser(source).Parse();
            new CIL(fns, allowStdio: args.Length == 3).Emit(args[1], source);
            Console.WriteLine($"ATTO_DIRECT_CIL_PASS functions={fns.Count}");
            return 0;
        }
        catch (CompilerError e) { Console.Error.WriteLine($"{e.Code}: {e.Message}"); return 2; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { Console.Error.WriteLine($"E_IO: {e.Message}"); return 3; }
    }
}
