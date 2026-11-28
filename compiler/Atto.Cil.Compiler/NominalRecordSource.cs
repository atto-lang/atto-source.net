namespace Atto.Cil.Compiler;

// Closed, immutable i64 Record profile. Resolve nominal identity and fields
// before passing scalar slots and direct calls into the ordinary CIL parser.
// No aggregate ABI, allocation guarantee, generic or mutable view admission.
internal static class NominalRecordSource
{
    private const string Prefix = "_atto_rec_v1_";
    private sealed record Method(string Target, string[] Fields, int Arity);
    private sealed record Schema(string[] Fields, Dictionary<string, Method> Methods);
    private sealed record Local(Schema Type, Dictionary<string, string> Slots);
    private static CompilerError Deny(string code, string reason) => new("E_RECORD_SUBTYPE_" + code, reason);
    private static readonly HashSet<string> Reserved = new(
        ("define record class extends as end pub private static fnc main int bool string float const val let mut return self super " +
         "if else while for match switch select spawn actor await async suspends trap throw raise panic copy take borrow share " +
         "new make use import namespace module type denote declare infer true false null void rt unsafe seq stdio process").Split(' '),
        StringComparer.Ordinal);
    internal static List<string>? Lower(List<string> source)
    {
        if (source.Count < 2 || source[0] != "define" || source[1] != "record") return null;
        if (source.Any(t => t.StartsWith(Prefix, StringComparison.Ordinal))) throw Deny("NAME", "reserved generated name");
        int pos = 0, methodCount = 0, slotCount = 0;
        var types = new Dictionary<string, Schema>(StringComparer.Ordinal);
        var locals = new Dictionary<string, Local>(StringComparer.Ordinal);
        List<string> output = [];
        string Peek() => pos < source.Count ? source[pos] : "<eof>";
        bool Eat(string s) { if (Peek() != s) return false; pos++; return true; }
        void Need(string s) { if (!Eat(s)) throw Deny("SYNTAX", "expected " + s + ", got " + Peek()); }
        bool Identifier(string t) => t.Length is > 0 and <= 64 && (char.IsAsciiLetter(t[0]) || t[0] == '_') &&
            t.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !Reserved.Contains(t);
        string Name(bool type = false)
        {
            string n = Peek();
            if (!Identifier(n) || (type ? !char.IsAsciiLetterUpper(n[0]) : n.Any(char.IsAsciiLetterUpper)))
                throw Deny("NAME", "invalid identifier");
            pos++; return n;
        }
        bool Integer(string t) => t.Length > 0 && t.All(char.IsAsciiDigit);
        bool Scalar(string t) => Integer(t) || t is "+" or "-" or "*" or "(" or ")";
        // Each source integer expression is validated again by the direct CIL
        // parser and checker. No host evaluation or arbitrary source rewrite.
        List<string> Expression(Dictionary<string, string> parameters, Dictionary<string, string>? fields, bool main)
        {
            List<string> result = []; int depth = 0;
            while (true)
            {
                string t = Peek();
                if (depth == 0 && t is ";" or "," or ")") break;
                if (t == "(") depth++;
                if (t == ")" && --depth < 0) throw Deny("BODY", "unbalanced expression");
                if (depth > 64 || result.Count > 1024) throw Deny("LIMIT", "expression budget");
                if (t == "self" && fields is not null)
                {
                    pos++; Need("."); string key = Name();
                    if (!fields.TryGetValue(key, out var slot)) throw Deny("FIELD", "unknown self field");
                    result.Add(slot);
                    continue;
                }
                if (main && locals.TryGetValue(t, out var local))
                {
                    pos++; Need("."); string member = Name();
                    if (Eat("("))
                    {
                        if (!local.Type.Methods.TryGetValue(member, out var method)) throw Deny("METHOD", "unknown nominal method");
                        result.AddRange([method.Target, "("]);
                        int count = 0;
                        foreach (string field in method.Fields) { if (count++ > 0) result.Add(","); result.Add(local.Slots[field]); }
                        int actual = 0;
                        if (!Eat(")"))
                        {
                            do
                            {
                                if (++actual > method.Arity) throw Deny("ARITY", "too many arguments");
                                if (count++ > 0) result.Add(",");
                                // Scalar arguments only; no nested method or aggregate escape.
                                result.AddRange(Expression(parameters, null, false));
                            } while (Eat(","));
                            Need(")");
                        }
                        if (actual != method.Arity) throw Deny("ARITY", "missing arguments");
                        result.Add(")");
                    }
                    else
                    {
                        if (!local.Slots.TryGetValue(member, out var fieldSlot)) throw Deny("FIELD", "unknown nominal field");
                        result.Add(fieldSlot);
                    }
                    continue;
                }
                if (parameters.TryGetValue(t, out var parameter)) result.Add(parameter);
                else if (Scalar(t)) result.Add(t);
                else throw Deny("BODY", "unsupported integer operand " + t);
                pos++;
            }
            if (depth != 0 || result.Count == 0) throw Deny("BODY", "empty or unbalanced expression");
            return result;
        }
        while (Eat("define"))
        {
            Need("record"); string name = Name(true); Need("extends"); string parent = Name(true); Need("as");
            if (types.Count >= 16 || name == "Record" || types.ContainsKey(name) ||
                name is "Map" or "Tuple" or "Pair" or "Array" or "String" or "Integer" or "Option" or "Result" or "Pointer" or "Channel" or "Future")
                throw Deny("NAME", "duplicate or reserved nominal Record name");
            Schema? inherited = null;
            if (parent != "Record" && !types.TryGetValue(parent, out inherited)) throw Deny("PARENT", "expected prior Record parent");
            List<string> fields = inherited is null ? [] : [.. inherited.Fields];
            var methods = inherited is null ? new Dictionary<string, Method>(StringComparer.Ordinal) : new Dictionary<string, Method>(inherited.Methods, StringComparer.Ordinal);
            while (Eat("int"))
            {
                string field = Name(); Need(";");
                if (fields.Contains(field) || methods.ContainsKey(field) || fields.Count >= 4) throw Deny("FIELD", "collision or field budget");
                fields.Add(field);
            }
            while (Eat("pub"))
            {
                string method = Name();
                if (fields.Contains(method) || methods.ContainsKey(method) || methodCount >= 32) throw Deny("METHOD", "collision or method budget");
                Need("("); List<string> ordinary = [];
                if (!Eat(")"))
                {
                    do { Need("int"); string parameter = Name(); if (ordinary.Contains(parameter) || fields.Count + ordinary.Count >= 8) throw Deny("ARITY", "duplicate or excessive parameter"); ordinary.Add(parameter); } while (Eat(","));
                    Need(")");
                }
                Need(":"); Need("int"); Need("{"); Need("return");
                string target = Prefix + "method_" + methodCount++;
                var fieldSlots = fields.Select((key, i) => (key, slot: Prefix + "field_" + i)).ToDictionary(x => x.key, x => x.slot, StringComparer.Ordinal);
                var parameters = ordinary.ToDictionary(x => x, x => x, StringComparer.Ordinal);
                var body = Expression(parameters, fieldSlots, false);
                Need(";"); Need("}");
                methods.Add(method, new Method(target, [.. fields], ordinary.Count));
                output.AddRange(["fnc", target, "("]);
                int count = 0;
                foreach (string parameter in fieldSlots.Values.Concat(ordinary))
                {
                    if (count++ > 0) output.Add(",");
                    output.AddRange(["int", parameter]);
                }
                output.AddRange([")", ":", "int", "{", "return", .. body, ";", "}"]);
            }
            Need("end");
            types.Add(name, new Schema([.. fields], methods));
        }
        Eat("pub"); Need("fnc"); Need("main"); Need("("); Need(")"); Need(":"); Need("int"); Need("{");
        output.AddRange(["fnc", "main", "(", ")", ":", "int", "{"]);
        while (Eat("const"))
        {
            string type = Name(true);
            if (!types.TryGetValue(type, out var schema)) throw Deny("TYPE", "unknown nominal Record");
            string name = Name(); Need("=");
            if (locals.ContainsKey(name) || locals.Count >= 16) throw Deny("BINDING", "duplicate or excessive binding");
            Need("(");
            var slots = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Eat(")"))
            {
                do
                {
                    string key = Name(); Need("=");
                    if (!schema.Fields.Contains(key) || slots.ContainsKey(key) || slotCount >= 4) throw Deny("SHAPE", "unknown/duplicate field or slot budget");
                    // Source-order initialization; each expression evaluated once.
                    var value = Expression(new(StringComparer.Ordinal), null, false);
                    string slot = Prefix + "slot_" + slotCount++;
                    slots.Add(key, slot);
                    output.AddRange(["val", slot, "=", .. value, ";"]);
                } while (Eat(","));
                Need(")");
            }
            Need(";");
            if (slots.Count != schema.Fields.Length) throw Deny("SHAPE", "missing nominal field");
            locals.Add(name, new Local(schema, slots));
        }
        Need("return");
        output.AddRange(["return", .. Expression(new(StringComparer.Ordinal), null, true)]);
        Need(";"); Need("}"); Need("<eof>");
        if (pos != source.Count || output.Count > 8192) throw Deny("LIMIT", "trailing tokens or generated budget");
        output.AddRange([";", "}", "<eof>"]);
        return output;
    }
}
