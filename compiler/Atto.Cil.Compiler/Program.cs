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
            else if (c == '"')
            {
                // Tokenize quoted strings atomically: an embedded `=>` is
                // data, not an entry operator; comments are handled above.
                // String *execution* remains unsupported by the scalar CIL
                // backend. Bound literals to prevent unbounded preflight work.
                bool closed = false;
                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        if (i + 1 >= source.Length) break;
                        i += 2;
                        continue;
                    }
                    if (source[i] == '"') { i++; closed = true; break; }
                    if (source[i] == '\n' || source[i] == '\r') break;
                    i++;
                    if (i - start > 4096)
                        throw new CompilerError("E_LIMIT", "string token exceeds 4096 characters");
                }
                if (!closed) throw new CompilerError("E_LEX", "unterminated quoted string");
            }
            else if (c == '=' && i < source.Length && source[i] == '>')
                throw new CompilerError("E_UNSUPPORTED_MAP_ENTRY",
                    "first-class Entry<K,V> and Map<K,V> require checked CIL ownership/lowering");
            // Recognize punctuation needed to reach a stable typed-feature
            // rejection. This does not enable assignments/generic runtime
            // values in the scalar parser; unsupported forms still fail closed.
            else if (!"(){}[]:;,+-*/=<>.?".Contains(c))
                throw new CompilerError("E_LEX", $"unsupported source character at {start}");
            _tokens.Add(source[start..i]);
            if (_tokens.Count > 8192) throw new CompilerError("E_LIMIT", "8192 token limit");
        }
        // The CIL scalar guest has no typed Result/Option values, error
        // identities, or cleanup-aware ?/trap unwinding. Reject these Atto
        // constructs *after lexing* so strings/comments cannot impersonate
        // executable effects, and before any CIL metadata/artifact is created.
        // Do not replace Result with an int sentinel or catch CLR exceptions
        // as though they were source Error values.
        if (_tokens.Any(t => t is "Result" or "Option" or "Ok" or "Err" or
              "Some" or "None" or "Error" or "trap" or "throw" or
              "raise" or "panic" or "try" or "catch" or "finally" or
              "defer" or "recover" or "?"))
            throw new CompilerError("E_UNSUPPORTED_RESULT_EFFECTS",
                "Result/Option, postfix ?, trap and source Error handling require typed CIL lowering");
        for (int tokenIndex = 0; tokenIndex + 2 < _tokens.Count; tokenIndex++)
        {
            if (_tokens[tokenIndex] == "int" &&
                _tokens[tokenIndex + 1] == "." &&
                _tokens[tokenIndex + 2] == "parse")
                throw new CompilerError("E_UNSUPPORTED_RESULT_EFFECTS",
                    "int.parse(string) requires Result<int,Error> and checked guest CIL lowering");
        }
        // Builtin Map/Entry must not be mistaken for scalar user symbols.
        // Token-based inspection avoids triggering on comments/strings.
        if (_tokens.Contains("Map") || _tokens.Contains("Entry"))
            throw new CompilerError("E_UNSUPPORTED_MAP_ENTRY",
                "typed Map/Entry runtime and CIL lowering are not implemented");
        // Closed Record indexing requires a proved fixed field slot and
        // referent-mutation authority; nominal Structs never get a dynamic
        // bracket indexer. Neither feature is lowerable by this scalar CIL.
        if (_tokens.Contains("Record"))
            throw new CompilerError("E_UNSUPPORTED_RECORD",
                "closed Record static slot access and mutable field lowering are not implemented");
        if (_tokens.Contains("[") || _tokens.Contains("]"))
            throw new CompilerError("E_UNSUPPORTED_INDEX_WRITE",
                "bracket indexing needs checked Map/Record type resolution; nominal Struct brackets are prohibited");
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
            Require("int"); Require("{"); Require("return");
            var expression = ParseExpr(0, 0);
            Require(";"); Require("}");
            result.Add(new Function(name, args, expression));
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
            int precedence = Peek is "*" or "/" ? 20 : Peek is "+" or "-" ? 10 : -1;
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
        void WalkExpr(Expression e, Function owner)
        {
            switch (e)
            {
                case Literal: return;
                case Arg a:
                    if (!owner.Args.Contains(a.Name)) throw new CompilerError("E_SYMBOL", a.Name);
                    return;
                case Binary b:
                    WalkExpr(b.Left, owner); WalkExpr(b.Right, owner);
                    return;
                case Call c:
                    if (!_symbols.TryGetValue(c.Name, out var target)) throw new CompilerError("E_SYMBOL", c.Name);
                    if (_functions[target].Args.Count != c.Args.Count) throw new CompilerError("E_ARITY", c.Name);
                    foreach (var arg in c.Args) WalkExpr(arg, owner);
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

    private void Generate(Expression e, Function f, InstructionEncoder il)
    {
        switch (e)
        {
            case Literal c: il.LoadConstantI8(c.Number); return;
            case Arg a: il.LoadArgument(f.Args.IndexOf(a.Name)); return;
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
