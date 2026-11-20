using System.Globalization;
using System.Text;

namespace Atto.Cil.ResultPreview;

// This opt-in, trusted-only compiler emits C# that is compiled to CIL by
// Roslyn at build time. It is NOT the shipping direct-CIL compiler and is
// NOT a TenantVM or general guest-language implementation.
internal sealed class PreviewError(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
internal enum GuestType { Int, Result }
internal abstract record GuestExpression;
internal sealed record Scalar(long Value) : GuestExpression;
internal sealed record OkValue(long Value) : GuestExpression;
internal sealed record ErrValue(string Message) : GuestExpression;
internal sealed record TrapCall(string Name) : GuestExpression;
internal sealed record GuestFunction(
    string Name, GuestType ReturnType, bool IsPublic, bool ThrowsError,
    bool IsThrow, GuestExpression? Expression, string? ThrowMessage);
internal sealed record GuestProgram(GuestFunction? Helper, GuestFunction Main);

internal sealed class Parser
{
    private const int MaxBytes = 8192;
    private const int MaxTokens = 512;
    private readonly List<string> _tokens = [];
    private int _position;
    private string Peek => _position < _tokens.Count ? _tokens[_position] : "<eof>";
    private string Next()
    {
        if (_position >= _tokens.Count) throw new PreviewError("E_PARSE");
        return _tokens[_position++];
    }
    private bool Eat(string value) { if (Peek != value) return false; _position++; return true; }
    private void Require(string value)
    {
        if (!Eat(value)) throw new PreviewError("E_PARSE");
    }
    private static bool IsAsciiName(string value) =>
        value.Length is > 0 and <= 80 &&
        (value[0] is >= 'a' and <= 'z' or '_') &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private string Name()
    {
        string value = Next();
        if (!IsAsciiName(value) || value is "fnc" or "pub" or "return" or "throw" or
            "trap" or "int" or "Result" or "Error" or "Ok" or "Err" or "throws" or
            "do" or "sync" or "task" or "await" or "panic" or "raise")
            throw new PreviewError("E_NAME");
        return value;
    }
    internal Parser(string source)
    {
        if (Encoding.UTF8.GetByteCount(source) > MaxBytes)
            throw new PreviewError("E_LIMIT");
        for (int i = 0; i < source.Length;)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            int start = i++;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')
            {
                while (i < source.Length && (source[i] is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) i++;
            }
            else if (c is >= '0' and <= '9')
            {
                while (i < source.Length && source[i] is >= '0' and <= '9') i++;
            }
            else if (c == '"')
            {
                while (i < source.Length && source[i] != '"')
                {
                    char m = source[i];
                    if (!(m is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                        >= '0' and <= '9' or '_' or '-' or ' ') || i - start > 64)
                        throw new PreviewError("E_STRING");
                    i++;
                }
                if (i >= source.Length) throw new PreviewError("E_STRING");
                i++;
            }
            else if (!"(){}:;,<>-".Contains(c))
                throw new PreviewError("E_LEX");
            _tokens.Add(source[start..i]);
            if (_tokens.Count > MaxTokens) throw new PreviewError("E_LIMIT");
        }
        _tokens.Add("<eof>");
    }
    private GuestType Type()
    {
        if (Eat("int")) return GuestType.Int;
        Require("Result"); Require("<"); Require("int"); Require(",");
        Require("Error"); Require(">");
        return GuestType.Result;
    }
    private long Number()
    {
        string raw = Eat("-") ? "-" + Next() : Next();
        if (raw.Length is < 1 or > 20 ||
            !long.TryParse(raw, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out long value) ||
            raw.TrimStart('-').Any(c => c is < '0' or > '9'))
            throw new PreviewError("E_LITERAL");
        return value;
    }
    private string ErrorMessage()
    {
        Require("Error"); Require("(");
        string token = Next();
        if (token.Length < 2 || token[0] != '"' || token[^1] != '"')
            throw new PreviewError("E_STRING");
        Require(")");
        return token[1..^1];
    }
    private GuestExpression Expression()
    {
        if (Eat("trap"))
        {
            string target = Name(); Require("("); Require(")");
            return new TrapCall(target);
        }
        if (Eat("Ok"))
        {
            Require("("); long value = Number(); Require(")");
            return new OkValue(value);
        }
        if (Eat("Err"))
        {
            Require("("); string message = ErrorMessage(); Require(")");
            return new ErrValue(message);
        }
        if (Peek == "-" || (Peek.Length > 0 && Peek[0] is >= '0' and <= '9'))
            return new Scalar(Number());
        throw new PreviewError("E_UNSUPPORTED");
    }
    private GuestFunction Function()
    {
        bool isPublic = Eat("pub");
        Require("fnc");
        string name = Name(); Require("("); Require(")");
        Require(":"); GuestType type = Type();
        bool throwsError = Eat("throws");
        if (throwsError) Require("Error");
        Require("{");
        bool isThrow = Eat("throw");
        GuestExpression? expression = null;
        string? error = null;
        if (isThrow)
            error = ErrorMessage();
        else
        {
            Require("return");
            expression = Expression();
        }
        Require(";"); Require("}");
        if (isThrow && !throwsError) throw new PreviewError("E_EFFECT");
        if (!isThrow &&
            ((type == GuestType.Int && expression is not Scalar) ||
             (type == GuestType.Result && expression is Scalar)))
            throw new PreviewError("E_TYPE");
        return new GuestFunction(name, type, isPublic, throwsError,
            isThrow, expression, error);
    }
    internal GuestProgram Parse()
    {
        var first = Function();
        GuestFunction? helper = null;
        GuestFunction main;
        if (first.Name == "main")
            main = first;
        else
        {
            helper = first;
            if (helper.IsPublic) throw new PreviewError("E_VISIBILITY");
            main = Function();
        }
        if (main.Name != "main" || !main.IsPublic ||
            main.ReturnType != GuestType.Result || main.IsThrow ||
            main.ThrowsError || Peek != "<eof>")
            throw new PreviewError("E_ENTRY");
        if (main.Expression is TrapCall call &&
            (helper is null || helper.Name != call.Name))
            throw new PreviewError("E_SYMBOL");
        if (helper?.Expression is TrapCall)
            throw new PreviewError("E_UNSUPPORTED");
        if (main.Expression is not (TrapCall or OkValue or ErrValue))
            throw new PreviewError("E_TYPE");
        return new GuestProgram(helper, main);
    }
}

internal static class Emitter
{
    private static string LongLiteral(long number) =>
        number == long.MinValue ? "long.MinValue" :
        number.ToString(CultureInfo.InvariantCulture) + "L";
    // The source string lexer permits only a closed ASCII alphabet, so no
    // C# metacharacter or escape can enter this string literal.
    private static string ErrorLiteral(string message) => "\"" + message + "\"";

    internal static string Generate(GuestProgram program)
    {
        var generated = new StringBuilder();
        generated.AppendLine("using System;");
        generated.AppendLine("namespace AttoResultGuest;");
        generated.AppendLine("internal abstract record GuestResult;");
        generated.AppendLine("internal sealed record GuestOk(long Value) : GuestResult;");
        generated.AppendLine("internal sealed record GuestErr(GuestError Error) : GuestResult;");
        generated.AppendLine("internal sealed class GuestError(string message) { internal string Message { get; } = message; }");
        generated.AppendLine("internal sealed class GuestThrown(GuestError original) : Exception { internal GuestError Original { get; } = original; }");
        generated.AppendLine("internal static class Guest {");
        if (program.Helper is { } helper)
        {
            generated.Append("  private static ")
                .Append(helper.ReturnType == GuestType.Int ? "long " : "GuestResult ")
                .Append(helper.Name).AppendLine("() {");
            if (helper.IsThrow)
                generated.Append("    throw new GuestThrown(new GuestError(")
                    .Append(ErrorLiteral(helper.ThrowMessage!)).AppendLine("));");
            else if (helper.Expression is Scalar scalar)
                generated.Append("    return ").Append(LongLiteral(scalar.Value)).AppendLine(";");
            else if (helper.Expression is OkValue ok)
                generated.Append("    return new GuestOk(").Append(LongLiteral(ok.Value)).AppendLine(");");
            else if (helper.Expression is ErrValue err)
                generated.Append("    return new GuestErr(new GuestError(")
                    .Append(ErrorLiteral(err.Message)).AppendLine("));");
            else throw new PreviewError("E_UNSUPPORTED");
            generated.AppendLine("  }");
        }
        generated.AppendLine("  internal static GuestResult Run() {");
        switch (program.Main.Expression)
        {
            case OkValue ok:
                generated.Append("    return new GuestOk(").Append(LongLiteral(ok.Value)).AppendLine(");");
                break;
            case ErrValue err:
                generated.Append("    return new GuestErr(new GuestError(")
                    .Append(ErrorLiteral(err.Message)).AppendLine("));");
                break;
            case TrapCall call:
                // Trap preserves the exact existing Result value (including
                // its Err identity) instead of creating a nested Result.
                generated.AppendLine("    try {");
                generated.Append("      return ");
                if (program.Helper!.ReturnType == GuestType.Int)
                    generated.Append("new GuestOk(").Append(call.Name).AppendLine("());");
                else
                    generated.Append(call.Name).AppendLine("();");
                generated.AppendLine("    } catch (GuestThrown thrown) {");
                // Capture ordinary guest throw only; host faults, OOM, and
                // cancellation bypass this handler.
                generated.AppendLine("      return new GuestErr(thrown.Original);");
                generated.AppendLine("    }");
                break;
            default: throw new PreviewError("E_TYPE");
        }
        generated.AppendLine("  }");
        generated.AppendLine("}");
        generated.AppendLine("internal static class Program {");
        generated.AppendLine("  private static int Main() {");
        generated.AppendLine("    GuestResult result = Guest.Run();");
        generated.AppendLine("    Console.WriteLine(result switch {");
        generated.AppendLine("      GuestOk ok => \"ATTO_RESULT=Ok(\" + ok.Value + \")\",");
        generated.AppendLine("      GuestErr err => \"ATTO_RESULT=Err(\" + err.Error.Message + \")\",");
        generated.AppendLine("      _ => throw new InvalidOperationException(\"invalid result tag\")");
        generated.AppendLine("    });");
        generated.AppendLine("    Console.WriteLine(\"ATTO_RESULT_GUEST=CSHARP_TO_CIL_PREVIEW\");");
        generated.AppendLine("    return 0;");
        generated.AppendLine("  }");
        generated.AppendLine("}");
        return generated.ToString();
    }
}
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2 || !args[0].EndsWith(".atto", StringComparison.Ordinal) ||
            !args[1].EndsWith(".cs", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("usage: atto-result-preview source.atto generated.cs");
            return 64;
        }
        var target = Path.GetFullPath(args[1]);
        try
        {
            if (File.Exists(target)) throw new PreviewError("E_OUTPUT_EXISTS");
            var source = File.ReadAllText(args[0], new UTF8Encoding(false, true));
            var guest = Emitter.Generate(new Parser(source).Parse());
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".tmp";
            try
            {
                File.WriteAllText(tmp, guest, new UTF8Encoding(false));
                File.Move(tmp, target);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
            Console.WriteLine("ATTO_RESULT_PREVIEW_SOURCE_EMITTED=PASS");
            Console.WriteLine("ATTO_RESULT_DIRECT_CIL_GUEST=NOT_EXECUTED");
            return 0;
        }
        catch (PreviewError e)
        {
            Console.Error.WriteLine(e.Code);
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Console.Error.WriteLine("E_IO");
            return 3;
        }
    }
}
