namespace Ores.Net.Compiler;

// Header-only target admission. Never returns a nominal AST to the scalar
// CFG/CIL pipeline and never treats attached behavior as a structural row.
public static partial class OresStructuredParser
{
    private static void RejectAttachedStructTarget(string source, int start, string owner, bool startAtBrace = false)
    {
        var lexer = new Lexer(source[start..], attachedStructTarget: true);
        int count = 0;
        Token Read()
        {
            if (++count > MaxTokens) throw new FormatException("Attached struct token budget exceeded");
            return lexer.Read();
        }
        Token current = Read(); // struct
        void Advance() => current = Read();
        FormatException Error(string code, string message) =>
            new($"{code}: {message} at offset {start + current.Offset}");
        void Expect(string text)
        {
            if (current.Text != text) throw Error("NET_STRUCT_WITH_SYNTAX", $"expected '{text}'");
            Advance();
        }
        string Word()
        {
            if (!IsSimpleNominalWord(current.Text)) throw Error("NET_STRUCT_WITH_SYNTAX", "expected identifier");
            string result = current.Text;
            Advance();
            return result;
        }
        string Scalar(bool allowVoid)
        {
            if (current.Text is not ("int" or "bool") && !(allowVoid && current.Text == "void"))
                throw Error("NET_STRUCT_WITH_UNSUPPORTED_TYPE", "bounded attachment target requires scalar types");
            string result = current.Text;
            Advance();
            return result;
        }
        void Body()
        {
            Expect("{");
            int depth = 1;
            while (depth > 0)
            {
                if (current.Text.Length == 0) throw Error("NET_STRUCT_WITH_SYNTAX", "unterminated member body");
                if (current.Text == "{" && ++depth > MaxDepth)
                    throw Error("NET_STRUCT_WITH_SYNTAX", "member body depth exceeded");
                if (current.Text == "}") depth--;
                Advance();
            }
        }

        // First identify an actual attachment. Ordinary struct declarations
        // must continue through their original admission path untouched.
        List<Token> fieldTokens = new();
        try
        {
            if (!startAtBrace) Advance();
            if (current.Text != "{") return;
            Advance();
            int depth = 1;
            while (depth > 0)
            {
                if (current.Text.Length == 0) return;
                if (current.Text == "{" && ++depth > MaxDepth) return;
                if (current.Text == "}") depth--;
                if (depth > 0) fieldTokens.Add(current);
                Advance();
            }
        }
        catch (FormatException) { return; }
        if (current.Text != "with") return;

        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (fieldTokens.Count % 3 != 0)
            throw Error("NET_STRUCT_WITH_SYNTAX", "expected scalar type-first fields");
        for (int i = 0; i < fieldTokens.Count; i += 3)
        {
            if (fieldTokens[i].Text is not ("int" or "bool") ||
                !IsSimpleNominalWord(fieldTokens[i + 1].Text) || fieldTokens[i + 2].Text != ";")
                throw Error("NET_STRUCT_WITH_SYNTAX", "invalid scalar field declaration");
            if (!fields.Add(fieldTokens[i + 1].Text))
                throw Error("NET_STRUCT_WITH_DUPLICATE", "duplicate field");
            if (fields.Count > MaxLocalTypeFields)
                throw Error("NET_STRUCT_WITH_SYNTAX", "too many fields");
        }
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        string Formals()
        {
            Expect("(");
            List<string> types = new();
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (current.Text != ")")
            {
                while (true)
                {
                    types.Add(Scalar(false));
                    if (!names.Add(Word())) throw Error("NET_STRUCT_WITH_DUPLICATE", "duplicate parameter");
                    if (types.Count > MaxParameters) throw Error("NET_STRUCT_WITH_SYNTAX", "too many parameters");
                    if (current.Text != ",") break;
                    Advance();
                }
            }
            Expect(")");
            return string.Join(",", types);
        }
        void Member()
        {
            if (current.Text is "pub" or "private") Advance();
            bool mutable = current.Text == "mut";
            if (mutable) Advance();
            string signature;
            if (current.Text == "constructor")
            {
                if (mutable) throw Error("NET_STRUCT_WITH_SYNTAX", "constructor has no receiver modifier");
                Advance();
                signature = "constructor:" + Formals();
            }
            else
            {
                Expect("mtd");
                string first = Word();
                string name, parameters;
                if (current.Text is "•" or "<-")
                {
                    if (first is not ("int" or "bool" or "void"))
                        throw Error("NET_STRUCT_WITH_UNSUPPORTED_TYPE", "unsupported result type");
                    Advance();
                    name = Word();
                    parameters = Formals();
                }
                else
                {
                    name = first;
                    parameters = Formals();
                    if (current.Text is not (":" or "->"))
                        throw Error("NET_STRUCT_WITH_SYNTAX", "expected return type separator");
                    Advance();
                    _ = Scalar(true);
                }
                signature = "mtd:" + name + ":" + parameters;
            }
            if (!signatures.Add(signature)) throw Error("NET_STRUCT_WITH_DUPLICATE", "duplicate attached member signature");
            if (signatures.Count > MaxHelpers) throw Error("NET_STRUCT_WITH_SYNTAX", "too many attached members");
            Body();
        }
        do
        {
            Expect("with");
            if (current.Text == "{")
            {
                Advance();
                if (current.Text == "}") throw Error("NET_STRUCT_WITH_SYNTAX", "empty attachment group");
                while (current.Text != "}") Member();
                Advance();
            }
            else Member();
        } while (current.Text == "with");

        throw Error("NET_STRUCT_WITH_NATIVEAOT_UNSUPPORTED",
            $"attached struct {owner} headers validated; checked nominal value layout, initialization, ownership and CIL lowering required");
    }
}
