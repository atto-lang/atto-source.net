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
internal sealed record Binding(string Name, Expression Initializer);
internal sealed record Function(string Name, List<string> Args, List<Binding> Locals, Expression Return);

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
            name is "fnc" or "pub" or "return" or "int" or "bool" or "val" or "let"
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
        if (source.Any(c => c > 127 || c == '\0'))
            throw new CompilerError("E_SOURCE", "non-ASCII/NUL source is not admitted by the scalar CIL frontend");
        for (int i = 0; i < source.Length;)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            int start = i++;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')
                while (i < source.Length && (source[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) i++;
            else if (c is >= '0' and <= '9')
                while (i < source.Length && source[i] is >= '0' and <= '9') i++;
            else if (c == '-' && i < source.Length && source[i] == '>') i++;
            else if (c == '|' && i < source.Length && source[i] == '>') i++;
            else if (c == '<' && i < source.Length && source[i] == '|') i++;
            else if (!"(){}:;,+-*/=".Contains(c))
                throw new CompilerError("E_LEX", $"unsupported source character at {start}");
            _tokens.Add(source[start..i]);
            if (_tokens.Count > 8192) throw new CompilerError("E_LIMIT", "8192 token limit");
        }
        _tokens.Add("<eof>");
    }
    // Deliberately bounded int64-only Atto guest subset. Each val initializer
    // executes once in source order, and an inline arrow body has exactly one
    // checked implicit return. A brace body still requires explicit return.
    internal List<Function> Parse()
    {
        List<Function> result = [];
        while (Peek != "<eof>")
        {
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
            Require("int");
            List<Binding> locals;
            Expression value;
            if (Eat("{"))
            {
                (locals, value) = BraceBody();
            }
            else if (colonReturn && Eat("->"))
            {
                if (Eat("{"))
                    (locals, value) = BraceBody();
                else
                {
                    locals = [];
                    value = ParseExpr(0, 0);
                    Require(";");
                }
            }
            else throw new CompilerError("E_PARSE", "expected '{' or ': int -> expression;'");
            result.Add(new Function(name, args, locals, value));
            if (result.Count > 64) throw new CompilerError("E_LIMIT", "64 function limit");
        }
        return result;
    }

    private (List<Binding> Locals, Expression Result) BraceBody()
    {
        List<Binding> locals = [];
        while (Eat("val"))
        {
            var name = Name();
            Require("=");
            var initializer = ParseExpr(0, 0);
            Require(";");
            if (locals.Count >= 4) throw new CompilerError("E_LIMIT", "4 local bindings per function");
            locals.Add(new Binding(name, initializer));
        }
        Require("return");
        var result = ParseExpr(0, 0);
        Require(";");
        Require("}");
        return (locals, result);
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

    // Arithmetic is tighter than functional application. Reverse application
    // is right-associative, forward application is left-associative. Both use
    // the same checked Call AST. Mixed directions require parentheses.
    private Expression ParseExpr(int min, int depth, string pipeDirection = "")
    {
        if (depth > 32) throw new CompilerError("E_LIMIT", "expression nesting limit");
        Expression left;
        if (Eat("(")) { left = ParseExpr(0, depth + 1); Require(")"); }
        else if (Eat("-"))
        {
            // The signed minimum is valid even though its positive magnitude
            // cannot be represented by long.TryParse. Do not parse +2^63 and
            // negate it, or use unchecked arithmetic for this case.
            if (Peek == "9223372036854775808")
            {
                _position++;
                left = new Literal(long.MinValue);
            }
            else left = new Binary("-", new Literal(0), ParseExpr(30, depth + 1));
        }
        else if (Peek.Length > 0 && Peek[0] is >= '0' and <= '9')
        {
            var raw = Peek; _position++;
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw new CompilerError("E_LITERAL", "64-bit signed literal overflow");
            left = new Literal(value);
        }
        else if (Peek is "true" or "false")
            throw new CompilerError("E_TYPE", "boolean source is outside the int64-only CIL profile");
        else
        {
            var name = Name();
            if (Peek == "(") left = new Call(name, Arguments(depth));
            else left = new Arg(name);
        }
        while (true)
        {
            int precedence = Peek switch
            {
                "*" or "/" => 20,
                "+" or "-" => 10,
                "|>" or "<|" => 5,
                _ => -1
            };
            if (precedence < min) break;
            var op = Peek;
            _position++;
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
}

internal sealed class CIL(List<Function> functions)
{
    private readonly List<Function> _functions = functions;
    private readonly Dictionary<string, int> _symbols = new(StringComparer.Ordinal);
    internal void Check()
    {
        for (int i = 0; i < _functions.Count; i++)
            if (!_symbols.TryAdd(_functions[i].Name, i))
                throw new CompilerError("E_DUP_FUNCTION", _functions[i].Name);
        if (!_symbols.TryGetValue("main", out var index) || _functions[index].Args.Count != 0)
            throw new CompilerError("E_ENTRY", "requires main(): int with no args");
        int[] state = new int[_functions.Count];
        for (int i = 0; i < state.Length; i++) WalkFunction(i);
        void WalkFunction(int id)
        {
            if (state[id] == 2) return;
            if (state[id] == 1) throw new CompilerError("E_CYCLE", _functions[id].Name);
            state[id] = 1;
            var fn = _functions[id];
            var available = new HashSet<string>(fn.Args, StringComparer.Ordinal);
            foreach (var binding in fn.Locals)
            {
                // Check before publishing the binding; self/future references
                // cannot accidentally become initialized by source-order changes.
                WalkExpr(binding.Initializer, fn, available);
                if (!available.Add(binding.Name))
                    throw new CompilerError("E_DUP_LOCAL", binding.Name);
            }
            WalkExpr(fn.Return, fn, available);
            state[id] = 2;
        }
        void WalkExpr(Expression e, Function owner, HashSet<string> available)
        {
            switch (e)
            {
                case Literal: return;
                case Arg a:
                    if (!available.Contains(a.Name)) throw new CompilerError("E_SYMBOL", a.Name);
                    return;
                case Binary b:
                    WalkExpr(b.Left, owner, available); WalkExpr(b.Right, owner, available);
                    return;
                case Call c:
                    if (!_symbols.TryGetValue(c.Name, out var target)) throw new CompilerError("E_SYMBOL", c.Name);
                    if (_functions[target].Args.Count != c.Args.Count) throw new CompilerError("E_ARITY", c.Name);
                    foreach (var arg in c.Args) WalkExpr(arg, owner, available);
                    WalkFunction(target);
                    return;
                default: throw new CompilerError("E_TYPE", "unsupported expression");
            }
        }
    }

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

        List<int> bodyOffsets = [];
        foreach (var f in _functions)
        {
            var code = new BlobBuilder();
            var il = new InstructionEncoder(code);
            // Local int64 signatures are explicit ECMA-335 StandAloneSig
            // records. The 4-slot bound keeps all indexed load/store opcodes
            // unambiguous and avoids dynamic reflection/runtime codegen.
            StandaloneSignatureHandle localSignature = default;
            if (f.Locals.Count > 0)
            {
                var localTypes = new BlobBuilder();
                var enc = new BlobEncoder(localTypes).LocalVariableSignature(f.Locals.Count);
                foreach (var binding in f.Locals) enc.AddVariable().Type().Int64();
                localSignature = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(localTypes));
            }
            for (int i = 0; i < f.Locals.Count; i++)
            {
                Generate(f.Locals[i].Initializer, f, il);
                il.OpCode(i switch
                {
                    0 => ILOpCode.Stloc_0,
                    1 => ILOpCode.Stloc_1,
                    2 => ILOpCode.Stloc_2,
                    3 => ILOpCode.Stloc_3,
                    _ => throw new CompilerError("E_LIMIT", "4 local bindings per function")
                });
            }
            Generate(f.Return, f, il);
            il.OpCode(ILOpCode.Ret);
            bodyOffsets.Add(bodyStream.AddMethodBody(il, maxStack: 64,
                localVariablesSignature: localSignature));
        }
        int nextParameter = 1;
        for (int i = 0; i < _functions.Count; i++)
        {
            var f = _functions[i];
            var signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature().Parameters(
                f.Args.Count, r => r.Type().Int64(),
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

    private void Generate(Expression e, Function f, InstructionEncoder il)
    {
        switch (e)
        {
            case Literal c: il.LoadConstantI8(c.Number); return;
            case Arg a:
                int argIndex = f.Args.IndexOf(a.Name);
                if (argIndex >= 0) il.LoadArgument(argIndex);
                else
                {
                    int localIndex = f.Locals.FindIndex(b => b.Name == a.Name);
                    if (localIndex < 0) throw new CompilerError("E_SYMBOL", a.Name);
                    il.OpCode(localIndex switch
                    {
                        0 => ILOpCode.Ldloc_0,
                        1 => ILOpCode.Ldloc_1,
                        2 => ILOpCode.Ldloc_2,
                        3 => ILOpCode.Ldloc_3,
                        _ => throw new CompilerError("E_LIMIT", "4 local bindings per function")
                    });
                }
                return;
            case Binary b:
                Generate(b.Left, f, il); Generate(b.Right, f, il);
                il.OpCode(b.Op switch
                {
                    "+" => ILOpCode.Add_ovf,
                    "-" => ILOpCode.Sub_ovf,
                    "*" => ILOpCode.Mul_ovf,
                    "/" => ILOpCode.Div,
                    _ => throw new CompilerError("E_PARSE", b.Op)
                });
                return;
            case Call c:
                foreach (var arg in c.Args) Generate(arg, f, il);
                il.Call(MetadataTokens.MethodDefinitionHandle(_symbols[c.Name] + 1));
                return;
            default: throw new CompilerError("E_TYPE", "unexpected expression");
        }
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2 || !args[0].EndsWith(".atto", StringComparison.Ordinal) ||
            !args[1].EndsWith(".dll", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("usage: atto-cil input.atto output.dll");
            return 64;
        }
        try
        {
            // Never mutate the destination before parsing, type checking,
            // and complete code emission. Failed source must preserve an
            // already-compiled DLL byte-for-byte.
            if (new FileInfo(args[0]).Length > 65536)
                throw new CompilerError("E_LIMIT", "64 KiB source limit");
            string source = File.ReadAllText(args[0], new UTF8Encoding(false, true));
            var fns = new Parser(source).Parse();
            new CIL(fns).Emit(args[1], source);
            Console.WriteLine($"ATTO_DIRECT_CIL_PASS functions={fns.Count}");
            return 0;
        }
        catch (CompilerError e) { Console.Error.WriteLine($"{e.Code}: {e.Message}"); return 2; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { Console.Error.WriteLine($"E_IO: {e.Message}"); return 3; }
    }
}
