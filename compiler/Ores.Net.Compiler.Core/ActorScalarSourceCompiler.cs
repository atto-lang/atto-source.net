using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Opt-in M0 nominal actor compiler. Deliberately supports only:
///
/// define actor Counter as
///   int value: 0
///   fnc increment(int amount): int {
///     self.value = self.value + amount;
///     return self.value;
///   }
/// end
///
/// All other actor shapes fail BEFORE emitting any DLL. The generated
/// ActorInitial():int and ActorStep(int,int):int functions are deterministic,
/// statically verifiable CIL; actor identity, ownership and state storage
/// are supplied by ActorRuntime's per-actor heap in the NativeAOT host.
/// This is not generic actor source semantics, suspension, or CLR stack
/// isolation.
/// </summary>
public static class ActorScalarSourceCompiler
{
    public sealed record ActorDefinition(string ActorName, string FieldName,
        string MethodName, long InitialValue);

    public static ActorDefinition Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 16_384 ||
            Encoding.UTF8.GetByteCount(source) > 16_384)
            throw new FormatException("Actor M0 source exceeds 16 KiB");
        var tokens = Tokenize(source);
        var parser = new Reader(tokens);
        return parser.Parse();
    }

    public static ActorDefinition EmitLibrary(string source, string outputFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        if (Path.GetFileName(outputFile) != "OresGenerated.dll")
            throw new ArgumentException("Actor M0 output must be named OresGenerated.dll");
        var actor = Parse(source);

        // This is a closed typechecked translation: the only generated method
        // body is checked int addition, independently verified by existing CFG
        // and static MethodDef linkage. The actor does NOT get to invent CIL.
        string initial = actor.InitialValue.ToString(CultureInfo.InvariantCulture);
        string lowered = "fnc actor_initial():int {return " + initial +
            ";} fnc actor_step(int current,int amount):int {return current + amount;}" +
            " fnc main():int {return 0;}";
        var verified = OresStructuredCfgLowering.LowerModule(lowered);
        CilEmitter.EmitActorScalarLibrary(verified, outputFile);
        return actor;
    }

    private readonly record struct Token(string Text, int Offset);

    private static IReadOnlyList<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        int offset = 0;
        while (offset < source.Length)
        {
            if (char.IsWhiteSpace(source[offset]))
            {
                offset++;
                continue;
            }
            if (tokens.Count >= 256)
                throw new FormatException("Actor M0 token budget exceeded");
            int start = offset;
            char c = source[offset];
            if (c == '\0' || char.IsSurrogate(c))
                throw new FormatException($"Invalid actor source character at offset {offset}");
            if (c is >= '0' and <= '9')
            {
                do { offset++; } while (offset < source.Length &&
                    source[offset] is >= '0' and <= '9');
            }
            else if ((c is >= 'a' and <= 'z') ||
                     (c is >= 'A' and <= 'Z') || c == '_')
            {
                do
                {
                    offset++;
                    if (offset == source.Length) break;
                    c = source[offset];
                } while ((c is >= 'a' and <= 'z') ||
                         (c is >= 'A' and <= 'Z') ||
                         (c is >= '0' and <= '9') || c == '_');
            }
            else if ("().:+-;={}".Contains(c))
                offset++;
            else
                throw new FormatException(
                    $"Unsupported actor M0 source token at offset {offset}");

            tokens.Add(new Token(source[start..offset], start));
        }
        tokens.Add(new Token("", source.Length));
        return tokens;
    }

    private sealed class Reader(IReadOnlyList<Token> tokens)
    {
        private int _position;
        private Token Current => tokens[_position];

        private FormatException Fail(string message) =>
            new("Actor M0 " + message + " at offset " + Current.Offset);

        private bool Take(string expected)
        {
            if (Current.Text != expected) return false;
            _position++;
            return true;
        }

        private void Expect(string expected)
        {
            if (!Take(expected))
                throw Fail("expected '" + expected + "'");
        }

        private string Name()
        {
            var value = Current.Text;
            if (value.Length is < 1 or > 64 ||
                !((value[0] is >= 'a' and <= 'z') ||
                  (value[0] is >= 'A' and <= 'Z') || value[0] == '_') ||
                !value.Skip(1).All(c => (c is >= 'a' and <= 'z') ||
                    (c is >= 'A' and <= 'Z') || (c is >= '0' and <= '9') ||
                    c == '_'))
                throw Fail("expected identifier");
            _position++;
            return value;
        }

        public ActorDefinition Parse()
        {
            Expect("define");
            Expect("actor");
            string actor = Name();
            if (actor is "self" or "int" or "fnc" or "end")
                throw Fail("reserved actor identifier");
            Expect("as");
            Expect("int");
            string field = Name();
            if (field is "self" or "int")
                throw Fail("reserved field identifier");
            Expect(":");
            bool negative = Take("-");
            string digits = Current.Text;
            if (digits.Length is < 1 or > 19 ||
                !digits.All(c => c is >= '0' and <= '9'))
                throw Fail("expected bounded signed integer literal");
            _position++;
            long initial;
            if (!long.TryParse((negative ? "-" : "") + digits,
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                    out initial))
                throw Fail("initial integer overflows checked i64");
            Take(";");
            Expect("fnc");
            string method = Name();
            if (method != "increment")
                throw Fail("unsupported actor method; only increment is admitted");
            Expect("(");
            Expect("int");
            string amount = Name();
            if (amount is "self" or "int" || amount == field)
                throw Fail("actor argument shadows state or is reserved");
            Expect(")");
            Expect(":");
            Expect("int");
            Expect("{");
            Expect("self");
            Expect(".");
            Expect(field);
            Expect("=");
            Expect("self");
            Expect(".");
            Expect(field);
            Expect("+");
            Expect(amount);
            Expect(";");
            Expect("return");
            Expect("self");
            Expect(".");
            Expect(field);
            Expect(";");
            Expect("}");
            Expect("end");
            if (Current.Text.Length != 0)
                throw Fail("trailing actor declarations or unsupported statements");
            return new ActorDefinition(actor, field, method, initial);
        }
    }
}
