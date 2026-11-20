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
internal sealed record BoolLiteral(bool Value) : Expression;
internal sealed record Arg(string Name) : Expression;
internal sealed record Binary(string Op, Expression Left, Expression Right) : Expression;
internal sealed record Call(string Name, List<Expression> Args) : Expression;
internal sealed record Guard(Expression Condition, Expression Return);
internal sealed record Function(string Name, List<string> Args, string ReturnType,
    List<Guard> Guards, Expression Return);

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
            name is "fnc" or "pub" or "return" or "int" or "bool"
                or "if" or "then" or "fi" or "true" or "false")
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
            else if ((c is '<' or '>' or '=' or '!') && i < source.Length && source[i] == '=') i++;
            else if (!"(){}:;,+-*/<>".Contains(c))
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
            string returnType = Peek;
            if (returnType is not ("int" or "bool"))
                throw new CompilerError("E_PARSE", "return type must be int or bool");
            _position++;
            Require("{");
            List<Guard> guards = [];
            while (Eat("if"))
            {
                if (guards.Count >= 32) throw new CompilerError("E_LIMIT", "32 guard limit");
                var condition = ParseExpr(0, 0);
                Require("then");
                Require("return");
                var guardedValue = ParseExpr(0, 0);
                Require(";");
                Require("fi");
                guards.Add(new Guard(condition, guardedValue));
            }
            Require("return");
            var expression = ParseExpr(0, 0);
            Require(";"); Require("}");
            result.Add(new Function(name, args, returnType, guards, expression));
            if (result.Count > 64) throw new CompilerError("E_LIMIT", "64 function limit");
        }
        return result;
    }
    private Expression ParseExpr(int min, int depth)
    {
        if (depth > 32) throw new CompilerError("E_LIMIT", "expression nesting limit");
        Expression left;
        if (Eat("(")) { left = ParseExpr(0, depth + 1); Require(")"); }
        else if (Eat("-")) left = new Binary("-", new Literal(0), ParseExpr(30, depth + 1));
        else if (Eat("true")) left = new BoolLiteral(true);
        else if (Eat("false")) left = new BoolLiteral(false);
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
            int precedence = Peek is "*" or "/" ? 20
                : Peek is "+" or "-" ? 10
                : Peek is "<" or "<=" or ">" or ">=" or "==" or "!=" ? 5 : -1;
            if (precedence < min) break;
            var op = Peek; _position++;
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
        if (!_symbols.TryGetValue("main", out var index)
            || _functions[index].Args.Count != 0 || _functions[index].ReturnType != "int")
            throw new CompilerError("E_ENTRY", "requires main(): int with no args");
        int[] state = new int[_functions.Count];
        for (int i = 0; i < state.Length; i++) WalkFunction(i);

        void WalkFunction(int id)
        {
            if (state[id] == 2) return;
            if (state[id] == 1) throw new CompilerError("E_CYCLE", _functions[id].Name);
            state[id] = 1;
            var function = _functions[id];
            foreach (var guard in function.Guards)
            {
                Expect(TypeOf(guard.Condition, function), "bool");
                Expect(TypeOf(guard.Return, function), function.ReturnType);
            }
            Expect(TypeOf(function.Return, function), function.ReturnType);
            state[id] = 2;
        }

        static void Expect(string actual, string wanted)
        {
            if (actual != wanted)
                throw new CompilerError("E_TYPE", $"expected {wanted}, got {actual}");
        }

        string TypeOf(Expression expression, Function owner)
        {
            switch (expression)
            {
                case Literal: return "int";
                case BoolLiteral: return "bool";
                case Arg a:
                    if (!owner.Args.Contains(a.Name))
                        throw new CompilerError("E_SYMBOL", a.Name);
                    return "int";
                case Binary b:
                    var left = TypeOf(b.Left, owner);
                    var right = TypeOf(b.Right, owner);
                    if (b.Op is "+" or "-" or "*" or "/" or "<" or "<=" or ">" or ">=")
                    {
                        Expect(left, "int");
                        Expect(right, "int");
                        return b.Op is "+" or "-" or "*" or "/" ? "int" : "bool";
                    }
                    if (b.Op is "==" or "!=")
                    {
                        if (left != right) throw new CompilerError("E_TYPE",
                            "equality operands must have identical scalar types");
                        return "bool";
                    }
                    throw new CompilerError("E_TYPE", "unsupported operator");
                case Call c:
                    if (!_symbols.TryGetValue(c.Name, out var target))
                        throw new CompilerError("E_SYMBOL", c.Name);
                    var called = _functions[target];
                    if (called.Args.Count != c.Args.Count)
                        throw new CompilerError("E_ARITY", c.Name);
                    foreach (var arg in c.Args) Expect(TypeOf(arg, owner), "int");
                    WalkFunction(target);
                    return called.ReturnType;
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
            // ControlFlowBuilder fixes forward branch offsets and validates
            // labels; guards must short-circuit before checked subtraction.
            var il = new InstructionEncoder(code, new ControlFlowBuilder());
            foreach (var guard in f.Guards)
            {
                Generate(guard.Condition, f, il);
                var skip = il.DefineLabel();
                il.Branch(ILOpCode.Brfalse, skip);
                Generate(guard.Return, f, il);
                il.OpCode(ILOpCode.Ret);
                il.MarkLabel(skip);
            }
            Generate(f.Return, f, il);
            il.OpCode(ILOpCode.Ret);
            bodyOffsets.Add(bodyStream.AddMethodBody(il, maxStack: 64));
        }
        int nextParameter = 1;
        for (int i = 0; i < _functions.Count; i++)
        {
            var f = _functions[i];
            var signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature().Parameters(
                f.Args.Count, r =>
                {
                    if (f.ReturnType == "bool") r.Type().Boolean();
                    else r.Type().Int64();
                },
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

    private void Generate(Expression e, Function f, InstructionEncoder il)
    {
        switch (e)
        {
            case Literal c: il.LoadConstantI8(c.Number); return;
            case BoolLiteral b: il.LoadConstantI4(b.Value ? 1 : 0); return;
            case Arg a: il.LoadArgument(f.Args.IndexOf(a.Name)); return;
            case Binary b:
                Generate(b.Left, f, il);
                Generate(b.Right, f, il);
                switch (b.Op)
                {
                    case "+": il.OpCode(ILOpCode.Add_ovf); return;
                    case "-": il.OpCode(ILOpCode.Sub_ovf); return;
                    case "*": il.OpCode(ILOpCode.Mul_ovf); return;
                    case "/": il.OpCode(ILOpCode.Div); return;
                    case "<": il.OpCode(ILOpCode.Clt); return;
                    case ">": il.OpCode(ILOpCode.Cgt); return;
                    case "==": il.OpCode(ILOpCode.Ceq); return;
                    case "!=":
                        il.OpCode(ILOpCode.Ceq);
                        il.LoadConstantI4(0);
                        il.OpCode(ILOpCode.Ceq);
                        return;
                    case "<=":
                        il.OpCode(ILOpCode.Cgt);
                        il.LoadConstantI4(0);
                        il.OpCode(ILOpCode.Ceq);
                        return;
                    case ">=":
                        il.OpCode(ILOpCode.Clt);
                        il.LoadConstantI4(0);
                        il.OpCode(ILOpCode.Ceq);
                        return;
                    default: throw new CompilerError("E_TYPE", b.Op);
                }
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
