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
internal sealed record Function(string Name, List<string> Args, Expression Return);

internal sealed partial class Parser
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
            name is "fnc" or "pub" or "return" or "int" or "bool")
            throw new CompilerError("E_NAME", $"expected snake_case identifier, got {name}");
        _position++;
        return name;
    }
    internal Parser(string source)
    {
        if (Encoding.UTF8.GetByteCount(source) > 65536)
            throw new CompilerError("E_LIMIT", "64 KiB source limit");
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
            else if ((c is '>' or '<' or '=' or '!') &&
                i < source.Length && source[i] == '=') i++;
            else if (!"(){}:;,+-*/^<>=".Contains(c))
                throw new CompilerError("E_LEX", $"unsupported source character at {start}");
            _tokens.Add(source[start..i]);
            if (_tokens.Count > 8192) throw new CompilerError("E_LIMIT", "8192 token limit");
        }
        _tokens.Add("<eof>");
    }
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
            if (!Eat(":")) Require("->");
            Require("int"); Require("{");
            if (!Eat("return") && !Eat("^"))
                throw new CompilerError("E_PARSE", "expected return or caret at function body");
            var expression = ParseExpr(0, 0);
            if (!Eat(";") && Peek != "}")
                throw new CompilerError("E_PARSE", "expected semicolon after return");
            Require("}");
            result.Add(new Function(name, args, expression));
            if (result.Count > 64) throw new CompilerError("E_LIMIT", "64 function limit");
        }
        return result;
    }
    private Expression ParseExpr(int min, int depth)
    {
        if (depth > 32) throw new CompilerError("E_LIMIT", "expression nesting limit");
        Expression left;
        if (Eat("match")) left = ParseMatch(depth + 1);
        else if (Eat("(")) { left = ParseExpr(0, depth + 1); Require(")"); }
        else if (Eat("-")) left = new Binary("-", new Literal(0), ParseExpr(30, depth + 1));
        else if (Peek.Length > 0 && Peek[0] is >= '0' and <= '9')
        {
            var raw = Peek; _position++;
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw new CompilerError("E_LITERAL", "64-bit signed literal overflow");
            left = new Literal(value);
        }
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
            int precedence = Peek is "*" or "/" ? 20 :
                Peek is "+" or "-" ? 10 :
                Peek is ">" or "<" or ">=" or "<=" ? 5 :
                Peek is "==" or "!=" ? 4 : -1;
            if (precedence < min) break;
            var op = Peek; _position++;
            left = new Binary(op, left, ParseExpr(precedence + 1, depth + 1));
        }
        return left;
    }
}

internal sealed partial class CIL(List<Function> functions)
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
            WalkExpr(_functions[id].Return, _functions[id]);
            state[id] = 2;
        }
        bool WalkExpr(Expression e, Function owner, HashSet<string>? bound = null)
        {
            bound ??= new HashSet<string>(StringComparer.Ordinal);
            switch (e)
            {
                case Literal: return false;
                case Arg a:
                    if (!owner.Args.Contains(a.Name) && !bound.Contains(a.Name))
                        throw new CompilerError("E_SYMBOL", a.Name);
                    return false;
                case Binary b:
                    if (WalkExpr(b.Left, owner, bound) || WalkExpr(b.Right, owner, bound))
                        throw new CompilerError("E_TYPE", "arithmetic/comparison operands must be int");
                    return b.Op is ">" or "<" or ">=" or "<=" or "==" or "!=";
                case Call c:
                    if (!_symbols.TryGetValue(c.Name, out var target))
                        throw new CompilerError("E_SYMBOL", c.Name);
                    if (_functions[target].Args.Count != c.Args.Count)
                        throw new CompilerError("E_ARITY", c.Name);
                    foreach (var arg in c.Args)
                        if (WalkExpr(arg, owner, bound))
                            throw new CompilerError("E_TYPE", "call arguments must be int");
                    WalkFunction(target);
                    return false;
                case ValueMatch m:
                    return CheckValueMatch(m, bound, (item, scope) => WalkExpr(item, owner, scope));
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
            var il = new InstructionEncoder(code, new ControlFlowBuilder());
            _nextMatchLocal = 0;
            int locals = CountMatchLocals(f.Return);
            if (locals > 256) throw new CompilerError("E_LIMIT", "256 match subject locals limit");
            StandaloneSignatureHandle localSignature = default;
            if (locals > 0)
            {
                var localBlob = new BlobBuilder();
                var localTypes = new BlobEncoder(localBlob).LocalVariableSignature(locals);
                for (int k = 0; k < locals; k++) localTypes.AddVariable().Type().Int64();
                localSignature = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(localBlob));
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
            new MetadataRootBuilder(metadata), ilStream, flags: CorFlags.ILOnly);
        var output = new BlobBuilder();
        builder.Serialize(output);
        var finalPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var temporary = finalPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
                output.WriteContentTo(stream);
            File.Move(temporary, finalPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Generate(Expression e, Function f, InstructionEncoder il,
        Dictionary<string, int>? bound = null)
    {
        switch (e)
        {
            case Literal c:
                il.LoadConstantI8(c.Number);
                return;
            case Arg a:
                if (bound is not null && bound.TryGetValue(a.Name, out int slot))
                    il.LoadLocal(slot);
                else
                    il.LoadArgument(f.Args.IndexOf(a.Name));
                return;
            case Binary b:
                Generate(b.Left, f, il, bound);
                Generate(b.Right, f, il, bound);
                switch (b.Op)
                {
                    case "+": il.OpCode(ILOpCode.Add_ovf); break;
                    case "-": il.OpCode(ILOpCode.Sub_ovf); break;
                    case "*": il.OpCode(ILOpCode.Mul_ovf); break;
                    case "/": il.OpCode(ILOpCode.Div); break;
                    case ">": il.OpCode(ILOpCode.Cgt); il.OpCode(ILOpCode.Conv_i8); break;
                    case "<": il.OpCode(ILOpCode.Clt); il.OpCode(ILOpCode.Conv_i8); break;
                    case "==": il.OpCode(ILOpCode.Ceq); il.OpCode(ILOpCode.Conv_i8); break;
                    case "!=":
                        il.OpCode(ILOpCode.Ceq);
                        il.OpCode(ILOpCode.Ldc_i4_0);
                        il.OpCode(ILOpCode.Ceq);
                        il.OpCode(ILOpCode.Conv_i8);
                        break;
                    case ">=":
                        il.OpCode(ILOpCode.Clt);
                        il.OpCode(ILOpCode.Ldc_i4_0);
                        il.OpCode(ILOpCode.Ceq);
                        il.OpCode(ILOpCode.Conv_i8);
                        break;
                    case "<=":
                        il.OpCode(ILOpCode.Cgt);
                        il.OpCode(ILOpCode.Ldc_i4_0);
                        il.OpCode(ILOpCode.Ceq);
                        il.OpCode(ILOpCode.Conv_i8);
                        break;
                    default: throw new CompilerError("E_PARSE", b.Op);
                }
                return;
            case Call c:
                foreach (var arg in c.Args) Generate(arg, f, il, bound);
                il.Call(MetadataTokens.MethodDefinitionHandle(_symbols[c.Name] + 1));
                return;
            case ValueMatch match:
                EmitValueMatch(match, f, il, bound);
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
            if (File.Exists(args[1])) File.Delete(args[1]);
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
