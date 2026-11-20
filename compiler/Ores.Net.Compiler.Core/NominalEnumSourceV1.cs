using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Opt-in, closed-source-shape nominal enum frontend. This is an actual
/// source -> typed CFG -> CIL path, but deliberately NOT general ADT support:
/// one locally constructed nullary enum and an exhaustive scalar match.
/// V2 also permits a single *internal* nominal-typed helper with one
/// nominal argument and nominal result. Those types are proven at the source
/// boundary before lowering to internal static i64 ABI slots; no raw numeric
/// enum argument, public nominal ABI, payload or actor message is admitted.
/// </summary>
public static class OresNominalEnumV1
{
    public static VerifiedCfgModule LowerModule(string source)
        => new Reader(Lex(source)).Read();

    public static void EmitLibrary(string source, string path)
        => CilEmitter.EmitLibrary(LowerModule(source), path);

    private readonly record struct Token(string Value, int Offset, bool Identifier);
    private sealed record Variant(string Name, long Tag);

    private static IReadOnlyList<Token> Lex(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576)
            throw new FormatException("ADT v1 source exceeds character budget");
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\0')
                throw new FormatException($"ADT v1 source contains NUL at offset {i}");
            if (char.IsHighSurrogate(source[i]))
            {
                if (++i == source.Length || !char.IsLowSurrogate(source[i]))
                    throw new FormatException($"ADT v1 invalid UTF-16 at offset {i - 1}");
            }
            else if (char.IsLowSurrogate(source[i]))
                throw new FormatException($"ADT v1 invalid UTF-16 at offset {i}");
        }
        if (Encoding.UTF8.GetByteCount(source) > 1_048_576)
            throw new FormatException("ADT v1 source exceeds UTF-8 byte budget");

        static bool Alpha(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
        static bool Digit(char c) => c is >= '0' and <= '9';
        var output = new List<Token>();
        int at = 0;
        while (at < source.Length)
        {
            if (char.IsWhiteSpace(source[at])) { at++; continue; }
            if (at + 1 < source.Length && source[at] == '/' && source[at + 1] == '/')
            {
                at += 2;
                while (at < source.Length && source[at] is not ('\n' or '\r' or '\u0085' or '\u2028' or '\u2029'))
                    at++;
                continue;
            }
            if (at + 1 < source.Length && source[at] == '/' && source[at + 1] == '*')
            {
                int start = at, depth = 1;
                at += 2;
                while (depth > 0)
                {
                    if (at >= source.Length)
                        throw new FormatException($"ADT v1 unterminated comment at offset {start}");
                    if (at + 1 < source.Length && source[at] == '/' && source[at + 1] == '*')
                    {
                        if (++depth > 128)
                            throw new FormatException($"ADT v1 comment depth exceeded at offset {at}");
                        at += 2;
                    }
                    else if (at + 1 < source.Length && source[at] == '*' && source[at + 1] == '/')
                    {
                        depth--;
                        at += 2;
                    }
                    else at++;
                }
                continue;
            }

            int startToken = at;
            bool identifier = false;
            if (Alpha(source[at]))
            {
                identifier = true;
                at++;
                while (at < source.Length && (Alpha(source[at]) || Digit(source[at])))
                    at++;
                if (at - startToken > 128)
                    throw new FormatException($"ADT v1 identifier length exceeded at offset {startToken}");
            }
            else if (Digit(source[at]))
            {
                at++;
                while (at < source.Length && Digit(source[at])) at++;
                if (at - startToken > 19)
                    throw new FormatException($"ADT v1 numeric literal exceeds i64 budget at offset {startToken}");
            }
            else if (source[at] == '-' && at + 1 < source.Length && source[at + 1] == '>')
                at += 2;
            else if ("(){}.:;=".Contains(source[at])) at++;
            else throw new FormatException($"ADT v1 unsupported character at offset {at}");

            if (output.Count >= 10_000)
                throw new FormatException("ADT v1 token budget exceeded");
            output.Add(new Token(source[startToken..at], startToken, identifier));
        }
        output.Add(new Token("", source.Length, false));
        return output;
    }

    private sealed class Reader(IReadOnlyList<Token> tokens)
    {
        private int index;
        private Token Current => tokens[Math.Min(index, tokens.Count - 1)];
        private bool Take(string value)
        {
            if (Current.Value != value) return false;
            index++;
            return true;
        }
        private void Need(string value)
        {
            if (!Take(value))
                throw new FormatException($"ADT v1 expected '{value}' at offset {Current.Offset}");
        }
        private string Name()
        {
            if (!Current.Identifier || Current.Value is "int" or "bool" or "define"
                or "enum" or "end" or "match" or "over" or "on" or "return"
                or "pub" or "fnc" or "val" or "let" or "main" or "when")
                throw new FormatException($"ADT v1 expected an unreserved identifier at offset {Current.Offset}");
            string result = Current.Value;
            index++;
            return result;
        }
        private long PositiveTag()
        {
            var text = Current.Value;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long result)
                || result < 1 || result > int.MaxValue)
                throw new FormatException($"ADT v1 tag must be positive signed i32 at offset {Current.Offset}");
            index++;
            return result;
        }
        private long ReturnNumber()
        {
            // The first typed match milestone deliberately limits result
            // literals to signed i32; it is NOT an unchecked integer parser.
            var text = Current.Value;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long number)
                || number > int.MaxValue)
                throw new FormatException($"ADT v1 return literal must be i32 at offset {Current.Offset}");
            index++;
            return number;
        }
        private (string Owner, string Case) Qualified()
        {
            string owner = Name();
            Need(".");
            string variant = Name();
            if (Take("(")) Need(")"); // source nullary case construction
            return (owner, variant);
        }

        public VerifiedCfgModule Read()
        {
            Need("define"); Need("enum");
            string owner = Name();
            Need("as");
            var variants = new List<Variant>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var tags = new HashSet<long>();
            long nextTag = 1;
            while (Current.Value != "end")
            {
                if (Current.Value.Length == 0 || variants.Count >= 32)
                    throw new FormatException($"ADT v1 enum case budget exceeded at offset {Current.Offset}");
                string name = Name();
                if (!names.Add(name))
                    throw new FormatException($"ADT v1 duplicate nominal case '{name}'");
                long tag = Take("=") ? PositiveTag() : nextTag;
                if (tag is < 1 or > int.MaxValue || !tags.Add(tag))
                    throw new FormatException($"ADT v1 duplicate/invalid nominal case tag at offset {Current.Offset}");
                variants.Add(new Variant(name, tag));
                nextTag = tag + 1;
                Take(";");
            }
            Need("end"); Take(";");
            if (variants.Count < 2)
                throw new FormatException("ADT v1 requires at least two closed nominal cases");

            // Explicit nominal typed function boundary. Parameters and
            // results must match the declared source enum owner. The
            // lowered i64 MethodDef ABI is private and statically linked.
            StructuredFunction? helper = null;
            if (Take("fnc"))
            {
                string helperName = Name();
                if (helperName == owner || variants.Any(v => v.Name == helperName))
                    throw new FormatException("ADT v2 helper conflicts with nominal symbol");
                Need("("); Need(owner);
                string parameterName = Name();
                if (parameterName == owner || parameterName == helperName)
                    throw new FormatException("ADT v2 helper parameter shadows a nominal symbol");
                Need(")"); Need(":"); Need(owner); Need("{");
                Need("match"); Need(parameterName); Need("over");
                var helperArms = new List<(Variant Case, Variant Returned)>();
                var helperSeen = new HashSet<string>(StringComparer.Ordinal);
                while (Take("on"))
                {
                    var q = Qualified();
                    if (q.Owner != owner)
                        throw new FormatException("ADT v2 helper pattern has foreign nominal owner");
                    Variant matchedCase = variants.Find(v => v.Name == q.Case)
                        ?? throw new FormatException("ADT v2 helper pattern has unknown case");
                    if (!helperSeen.Add(matchedCase.Name))
                        throw new FormatException("ADT v2 helper has duplicate nominal match case");
                    Need("->"); Need("{"); Need("return");
                    var returned = Qualified();
                    if (returned.Owner != owner)
                        throw new FormatException("ADT v2 helper return has foreign nominal owner");
                    Variant returnedCase = variants.Find(v => v.Name == returned.Case)
                        ?? throw new FormatException("ADT v2 helper returns unknown nominal case");
                    Need(";"); Need("}");
                    helperArms.Add((matchedCase, returnedCase));
                }
                Need("end"); Need("}");
                if (helperSeen.Count != variants.Count)
                    throw new FormatException("ADT v2 helper has nonexhaustive nominal match");
                StructuredStmt helperCase = new StructuredReturn(0,
                    new StructuredInt(0, helperArms[^1].Returned.Tag));
                for (int i = helperArms.Count - 2; i >= 0; --i)
                {
                    var cond = new StructuredBinary(0, StructuredOperator.EqualI64,
                        new StructuredRead(0, parameterName, IrValueType.I64),
                        new StructuredInt(0, helperArms[i].Case.Tag), IrValueType.Bool);
                    helperCase = new StructuredIf(0, cond,
                        new StructuredStmt[] {
                            new StructuredReturn(0, new StructuredInt(0, helperArms[i].Returned.Tag))
                        }, new StructuredStmt[] { helperCase });
                }
                helper = new StructuredFunction(helperName,
                    new StructuredParameter[] { new(parameterName, IrValueType.I64) },
                    IrValueType.I64, null, new StructuredStmt[] { helperCase });
            }

            Need("pub"); Need("fnc"); Need("main"); Need("("); Need(")");
            Need(":"); Need("int"); Need("{");
            Need("val"); Need(owner);
            string variable = Name();
            Need("=");
            bool invokeHelper = helper is not null && Current.Value == helper.Name;
            if (invokeHelper)
            {
                index++; // helper name: already resolved as the exact local function
                Need("(");
            }
            var constructed = Qualified();
            if (constructed.Owner != owner)
                throw new FormatException("ADT v1 constructor has foreign nominal owner");
            var selected = variants.Find(v => v.Name == constructed.Case)
                ?? throw new FormatException("ADT v1 unknown nominal constructor variant");
            if (invokeHelper) Need(")");
            Need(";");
            // Nominal source check precedes this private scalar lowering.
            // No guest may call this helper with an integer or foreign enum.
            StructuredExpr initializer = invokeHelper
                ? new StructuredCall(0, helper!.Name,
                    new StructuredExpr[] { new StructuredInt(0, selected.Tag) },
                    IrValueType.I64)
                : new StructuredInt(0, selected.Tag);

            Need("match"); Need(variable); Need("over");
            var arms = new List<(Variant Case, long Result)>();
            var matched = new HashSet<string>(StringComparer.Ordinal);
            while (Take("on"))
            {
                var arm = Qualified();
                if (arm.Owner != owner)
                    throw new FormatException("ADT v1 match uses a foreign nominal owner");
                Variant branch = variants.Find(v => v.Name == arm.Case)
                    ?? throw new FormatException("ADT v1 match uses an unknown case");
                if (!matched.Add(branch.Name))
                    throw new FormatException("ADT v1 match contains duplicate case arm");
                Need("->"); Need("{"); Need("return");
                long result = ReturnNumber();
                Need(";"); Need("}");
                arms.Add((branch, result));
            }
            Need("end"); Need("}"); Need("");
            if (matched.Count != variants.Count)
                throw new FormatException("ADT v1 nonexhaustive nominal match");

            // Only compiler-owned nominal constructors can initialize the
            // sealed local. The private lowering maps nominal symbols to
            // internal tags, but never exposes those tags as source integers.
            var body = new List<StructuredStmt>
            {
                new StructuredLet(0, variable, IrValueType.I64,
                    initializer, StructuredBindingKind.Val)
            };

            // All possible tags are known from the one checked local value.
            // Every arm is type/owner checked and exhaustiveness is proven;
            // the final else arm cannot be reached by a foreign/raw tag.
            StructuredStmt branchBody = new StructuredReturn(0,
                new StructuredInt(0, arms[^1].Result));
            for (int i = arms.Count - 2; i >= 0; i--)
            {
                var condition = new StructuredBinary(0, StructuredOperator.EqualI64,
                    new StructuredRead(0, variable, IrValueType.I64),
                    new StructuredInt(0, arms[i].Case.Tag), IrValueType.Bool);
                branchBody = new StructuredIf(0, condition,
                    new StructuredStmt[] { new StructuredReturn(0,
                        new StructuredInt(0, arms[i].Result)) },
                    new StructuredStmt[] { branchBody });
            }
            body.Add(branchBody);
            var functions = new Dictionary<string, StructuredFunction>(StringComparer.Ordinal);
            if (helper is not null) functions.Add(helper.Name, helper);
            var order = helper is null ? Array.Empty<string>() : new[] { helper.Name };
            return OresStructuredCfgLowering.LowerModule(
                new StructuredProgram("main", body, functions, order));
        }
    }
}
