namespace Atto.Cil.Compiler;

// Closed integer-only source profile. Lower to existing checked direct CIL
// function calls and immutable local slots, never a host evaluator.
// This is not a general first-class factory or allocator ABI.
internal static class ContextualIntSource
{
    private const string Prefix = "_atto_ctx_v1_";
    private static CompilerError Denied(string message) => new("E_CONTEXT_PROFILE", message);
    internal static List<string>? Lower(List<string> source)
    {
        bool present = Enumerable.Range(0, Math.Max(0, source.Count - 4)).Any(i =>
            source[i] == "fnc" && source[i + 2] == ":" && source[i + 3] == ":" && source[i + 4] == "(");
        if (!present) return null;
        if (source.Any(t => t.StartsWith(Prefix, StringComparison.Ordinal))) throw Denied("reserved generated name");
        int pos = 0;
        string Peek() => pos < source.Count ? source[pos] : "<eof>";
        bool Eat(string t) { if (Peek() != t) return false; pos++; return true; }
        void Need(string t) { if (!Eat(t)) throw Denied("expected " + t + ", got " + Peek()); }
        bool IsName(string t) => t.Length > 0 && (char.IsAsciiLetter(t[0]) || t[0] == '_') &&
            t.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
            t is not ("define" or "class" or "as" or "pub" or "static" or "fnc" or "main" or "int" or "bool" or "const" or "val" or "let" or "return" or "new" or "make" or "end" or "true" or "false" or "Pointer" or "stdio" or "process" or "actor" or "await" or "spawn" or "self" or "import" or "use" or "namespace");
        string Name() { string t = Peek(); if (!IsName(t)) throw Denied("invalid contextual name"); pos++; return t; }
        List<string> Parameters()
        {
            Need("("); List<string> result = [];
            if (!Eat(")"))
            {
                do { Need("int"); result.Add(Name()); if (result.Count > 8) throw Denied("parameter limit"); } while (Eat(","));
                Need(")");
            }
            return result;
        }
        Need("define"); Need("class"); string owner = Name(); Need("as"); Need("pub"); Need("static"); Need("fnc");
        string member = Name(); Need(":"); Need(":");
        var captures = Parameters(); var ordinary = Parameters();
        var parameters = captures.Concat(ordinary).ToList();
        if (captures.Count is < 1 or > 4 || parameters.Count > 8 ||
            parameters.Distinct(StringComparer.Ordinal).Count() != parameters.Count || parameters.Contains(owner))
            throw Denied("invalid integer context schema");
        if (!Eat("->")) Need(":"); Need("int"); Need("{"); Need("return");
        var output = new List<string> { "fnc", Prefix + "member", "(" };
        for (int i = 0; i < parameters.Count; i++) {
            if (i != 0) output.Add(",");
            output.AddRange(["int", parameters[i]]);
        }
        output.AddRange([")", "->", "int", "{", "return"]);
        var aliases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var locals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string parameter in parameters) locals.Add(parameter, parameter);
        bool IsScalar(string token) => token.All(char.IsAsciiDigit) && token.Length > 0 ||
            token is "+" or "-" or "*" or "/" or "(" or ")";
        // Read a scalar expression without consuming its surrounding delimiter.
        List<string> Plain()
        {
            var result = new List<string>(); int depth = 0;
            while (true) {
                string t = Peek();
                if (depth == 0 && t is "," or ")" or ";") break;
                if (t == "(") depth++;
                if (t == ")" && --depth < 0) throw Denied("unbalanced delimiter");
                if (depth > 64) throw Denied("expression depth");
                if (locals.TryGetValue(t, out string? local)) result.Add(local);
                else if (IsScalar(t)) result.Add(t);
                else throw Denied("unsupported scalar operand " + t);
                pos++;
            }
            if (depth != 0 || result.Count == 0) throw Denied("empty or unbalanced scalar expression");
            return result;
        }
        List<List<string>> Args(int count)
        {
            Need("("); List<List<string>> args = [];
            if (!Eat(")")) { do { args.Add(Plain()); if (args.Count > count) throw Denied("argument arity"); } while (Eat(",")); Need(")"); }
            if (args.Count != count) throw Denied("argument arity");
            return args;
        }
        var memberBody=Plain(); output.AddRange(memberBody); Need(";"); Need("}"); Need("end");
        int mainHeaderAt=output.Count+2;
        var variants=new Dictionary<string,string>(StringComparer.Ordinal);
        var variantTokens=new List<string>();
        string Variant(List<int> order) {
            string key=string.Join(",",order);
            if(variants.TryGetValue(key,out string? found))return found;
            if(variants.Count>=16)throw Denied("named specialization budget");
            string target=Prefix+"order_"+variants.Count;
            variantTokens.AddRange(["fnc",target,"("]);
            int n=0;
            foreach(var p in order.Select(j=>captures[j]).Concat(ordinary)){
                if(n++>0)variantTokens.Add(",");variantTokens.AddRange(["int",p]);
            }
            variantTokens.AddRange([")","->","int","{","return",..memberBody,";","}"]);
            variants.Add(key,target);return target;
        }
        output.AddRange([";", "}", "fnc", "main", "(", ")", "->", "int", "{"]);
        locals.Clear();
        Need("fnc"); Need("main"); Need("("); Need(")"); Need("->"); Need("int"); Need("{");
        int slots = 0;
        List<string> MainExpr()
        {
            List<string> result = []; int depth = 0;
            while (Peek() != ";" || depth != 0) {
                string t = Peek();
                if (t == owner || aliases.ContainsKey(t)) {
                    pos++;
                    List<List<string>> context;
                    string target=Prefix+"member";
                    if (t == owner) {
                        Need(":"); Need(":");
                        bool named=pos+2<source.Count&&Peek()=="("&&IsName(source[pos+1])&&source[pos+2]=="=";
                        if(named){
                            Need("(");context=[];List<int> order=[];
                            for(int k=0;k<captures.Count;k++){
                                if(k>0)Need(",");
                                string key=Name();Need("=");int j=captures.IndexOf(key);
                                if(j<0||order.Contains(j))throw Denied("unknown or duplicate named context");
                                order.Add(j);context.Add(Plain());
                            }
                            Need(")");target=Variant(order);
                        }else context=Args(captures.Count);
                    }else context = aliases[t].Select(x => new List<string> { x }).ToList();
                    Need("."); Need(member); var actual = Args(ordinary.Count);
                    result.AddRange([target, "("]);
                    int i = 0;
                    foreach (var arg in context.Concat(actual)) { if (i++ > 0) result.Add(","); result.AddRange(arg); }
                    result.Add(")");
                } else {
                    if (t == "(") depth++;
                    if (t == ")" && --depth < 0) throw Denied("unbalanced main expression");
                    if (depth > 64) throw Denied("expression depth");
                    if (locals.TryGetValue(t, out string? local)) result.Add(local);
                    else if (IsScalar(t)) result.Add(t);
                    else throw Denied("unsupported main operand " + t);
                    pos++;
                }
            }
            if (result.Count == 0) throw Denied("empty main expression");
            return result;
        }
        List<string> CaptureSlots()
        {
            Need(owner); Need(":"); Need(":"); Need("(");
            bool named = pos + 1 < source.Count && IsName(Peek()) && source[pos + 1] == "=";
            if (slots + captures.Count > 4) throw Denied("direct CIL local slot limit");
            var saved = Enumerable.Repeat("", captures.Count).ToList();
            for (int k = 0; k < captures.Count; k++) {
                if (k > 0) Need(",");
                int index = k;
                if (named) {
                    string key = Name(); Need("="); index = captures.IndexOf(key);
                    if (index < 0 || saved[index] != "") throw Denied("unknown or duplicate named context");
                }
                var arg = Plain();
                string slot = Prefix + "slot_" + slots++;
                saved[index] = slot; output.AddRange(["val", slot, "=", .. arg, ";"]);
            }
            Need(")");
            return saved;
        }
        while (Peek() is "const" or "let" or "set")
        {
            if (Peek() != "const") {
                bool declaration = Eat("let");
                if (!declaration) Need("set");
                string target = Name();
                if (target == owner || aliases.ContainsKey(target) || (!declaration && !locals.ContainsKey(target)))
                    throw Denied("set requires existing scalar let; contextual aliases immutable");
                Need("="); var rhs = Plain(); Need(";");
                if (slots >= 4) throw Denied("direct CIL local slot limit");
                string local = Prefix + "slot_" + slots++;
                output.AddRange(["val", local, "=", .. rhs, ";"]);
                locals[target] = local;
                continue;
            }
            Need("const");
            string name = Name();
            if (name == owner || aliases.ContainsKey(name) || locals.ContainsKey(name) || aliases.Count >= 64)
                throw Denied("duplicate or excessive context binding");
            Need("=");
            if (aliases.TryGetValue(Peek(), out var existing) && pos + 1 < source.Count && source[pos + 1] == ";") {
                pos++; aliases.Add(name, existing); Need(";"); continue;
            }
            var saved=CaptureSlots(); Need(";");
            aliases.Add(name, saved);
        }
        Need("return");
        output.Add("return"); output.AddRange(MainExpr());
        Need(";"); Need("}"); Need("<eof>");
        output.AddRange([";", "}", "<eof>"]);
        if (output.Count+variantTokens.Count > 8192) throw Denied("generated token limit");
        output.InsertRange(mainHeaderAt,variantTokens);
        return output;
    }
}
