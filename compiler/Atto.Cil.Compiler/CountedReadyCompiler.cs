using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Atto.Cil.Compiler;

// Explicit closed-world ready-only profile, emitting ordinary statically
// compiled C# against the existing NativeAOT runtime. No guest eval/reflection.
internal sealed class CountedReadyCompiler
{
    private sealed record Value(string Type, string Code);
    private sealed record Slot(string Type, string Code) { internal bool Moved; }
    private readonly IReadOnlyList<string> tokens;
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private int pos, budget = 4096, depth, countSites;
    private static readonly HashSet<string> Reserved = new("pub suspends fnc main int bool val let const Channel Future Array new writech readch select nb await when fair first random default over end return if then fi true false".Split(' '), StringComparer.Ordinal);
    private string Peek => tokens[pos];
    private CountedReadyCompiler(string source) { tokens = new Parser(source, countedReady: true).CountedTokens; }
    private static CompilerError Bad(string message) => new("E_COUNTED_READY_PROFILE", message);
    private bool Eat(string token) { if (Peek != token) return false; pos++; return true; }
    private void Need(string token) { if (!Eat(token)) throw Bad("expected " + token); }
    private string Name()
    {
        var n = Peek;
        if (n.Length == 0 || !(n[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') ||
            n.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) || Reserved.Contains(n))
            throw Bad("lexical name required");
        pos++; return n;
    }
    private Slot Get(string n, string type)
    {
        if (!slots.TryGetValue(n, out var s) || s.Moved || s.Type != type) throw Bad("expected live lexical " + type);
        return s;
    }
    private long Literal()
    {
        if (!long.TryParse(Peek, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) throw Bad("bounded integer literal required");
        pos++; return n;
    }
    private int Small(int max) { long n = Literal(); if (n < 0 || n > max) throw Bad("literal exceeds profile limit"); return (int)n; }
    private static string Check(Value v, string type) => v.Type == type ? v.Code : throw Bad("expected " + type);
    private string Type()
    {
        if (Eat("int")) return "int";
        if (Eat("Channel")) { Need("<"); Need("int"); Need(">"); return "channel"; }
        if (Eat("Future")) { Need("<"); Need("int"); Need(">"); return "future"; }
        if (Eat("Array")) { bool bracket = Eat("["); if (!bracket) Need("<"); Need("int"); Need(bracket ? "]" : ">"); return "array"; }
        throw Bad("unsupported lexical type");
    }
    private static int Prec(string token) => token switch { "||" => 1, "&&" => 2, "==" or "!=" => 3, "<" or ">" or "<=" or ">=" => 4, "+" or "-" => 5, "*" => 6, _ => -1 };
    private Value Expr(int min = 0, string? binding = null)
    {
        if (--budget < 0 || ++depth > 64) throw Bad("expression budget");
        try
        {
            Value left = Primary(binding);
            while (Prec(Peek) >= min)
            {
                string op = Peek; pos++;
                var right = Expr(Prec(op) + 1, binding);
                if (op is "+" or "-" or "*") left = new("int", "checked((" + Check(left,"int") + ")" + op + "(" + Check(right,"int") + "))");
                else if (op is "&&" or "||") left = new("bool", "((" + Check(left,"bool") + ")" + op + "(" + Check(right,"bool") + "))");
                else left = new("bool", "((" + Check(left,"int") + ")" + op + "(" + Check(right,"int") + "))");
            }
            return left;
        }
        finally { depth--; }
    }
    private Value Primary(string? binding)
    {
        if (Eat("(")) { var v = Expr(0,binding); Need(")"); return v; }
        if (Eat("-")) return new("int","checked(-(" + Check(Expr(7,binding),"int") + "))");
        if (Peek.Length > 0 && char.IsAsciiDigit(Peek[0])) return new("int", Literal().ToString(CultureInfo.InvariantCulture) + "L");
        if (binding is not null)
        {
            if (Name() != binding) throw Bad("projection may reference only its binder");
            return new("int","x");
        }
        if (Eat("await"))
        {
            if (Eat("select")) return Counted();
            var future = Get(Name(),"future"); future.Moved = true; return new("int","Observe(" + future.Code + ")");
        }
        if (Eat("readch")) return new("int","Read(" + Get(Name(),"channel").Code + ")");
        var n = Name();
        if (!slots.TryGetValue(n,out var s) || s.Moved) throw Bad("unknown or moved lexical binding");
        if (s.Type == "int") return new("int",s.Code);
        if (s.Type == "array")
        {
            if (Eat("[")) { int i = Small(15); Need("]"); return new("int","At(" + s.Code + "," + i + ")"); }
            Need("."); Need("size"); return new("int","((long)" + s.Code + ".Count)");
        }
        throw Bad("capability cannot escape scalar expression");
    }
    private Value Counted()
    {
        if (Eat("<")) { Need("int"); Need(">"); }
        Need("("); int count = Small(16); Need(")");
        bool fair = Eat("fair"); if (!fair) Eat("first");
        bool over = Eat("over"); if (!over) Need("{");
        List<string> sources = [], projections = []; HashSet<Slot> futures = [];
        while (Eat("when"))
        {
            if (sources.Count == 8) throw Bad("arm quota");
            string kind = Eat("readch") ? "channel" : Eat("await") ? "future" : throw Bad("read/Future arm required");
            var s = Get(Name(),kind); if (kind == "future") futures.Add(s);
            Need(":"); Need("int"); string binder = Name(); Need("->"); Need("{"); Need("return");
            projections.Add("static x => " + Check(Expr(0,binder),"int")); Need(";"); Need("}"); sources.Add(s.Code);
        }
        if (sources.Count == 0) throw Bad("at least one arm required");
        Need(over ? "end" : "}"); countSites++;
        if (count == 0) return new("array","Batch(0, [], [], " + (fair ? "true" : "false") + ")");
        foreach (var f in futures) f.Moved = true;
        return new("array","Batch(" + count + ", [" + string.Join(",",sources) + "], [" + string.Join(",",projections) + "], " + (fair ? "true" : "false") + ")");
    }
    private string Bind()
    {
        string type = Type(), n = Name();
        if (slots.ContainsKey(n) || slots.Count >= 128) throw Bad("duplicate or excessive lexical binding");
        Need("="); string code;
        if (type == "channel")
        {
            Need("Channel"); Need("."); Need("new"); Need("<"); Need("int"); Need(">"); Need("(");
            int cap = Small(64); if (cap == 0) throw Bad("zero capacity"); Need(")"); code = "new SelectableI64Channel(" + cap + ")";
        }
        else if (type == "future")
        {
            Need("select"); Need("<"); Need("int"); Need(">"); bool over = Eat("over"); if (!over) Need("{");
            Need("default"); Need(":"); Need("->"); Need("{"); Need("return");
            code = "Ready(static () => " + Check(Expr(0,"$no_binding"),"int") + ")";
            Need(";"); Need("}"); Need(over ? "end" : "}");
        }
        else code = Check(Expr(),type);
        Need(";"); string slot = "v" + slots.Count; slots.Add(n,new Slot(type,slot));
        return "var " + slot + "=" + code + ";";
    }
    private string Parse()
    {
        Eat("pub"); Need("suspends"); Need("fnc"); Need("main"); Need("("); Need(")"); Need(":"); Need("int"); Need("{");
        List<string> statements = [];
        while (Peek != "return")
        {
            if (statements.Count >= 256) throw Bad("statement quota");
            if (Eat("val")) statements.Add(Bind());
            else if (Eat("writech")) { var s = Get(Name(),"channel"); Need(","); var v = Check(Expr(),"int"); Need(";"); statements.Add("Write(" + s.Code + "," + v + ");"); }
            else if (Eat("if"))
            {
                string cond = Check(Expr(),"bool"); Need("then"); Need("return");
                var before = slots.Values.ToDictionary(s => s.Code,s => s.Moved);
                string value = Check(Expr(),"int"); Need(";"); Need("fi");
                foreach (var s in slots.Values) s.Moved = before[s.Code];
                statements.Add("if(" + cond + ")return " + value + ";");
            }
            else throw Bad("unsupported statement");
        }
        Need("return"); statements.Add("return " + Check(Expr(),"int") + ";"); Need(";"); Need("}");
        if (Peek != "<eof>" || countSites == 0) throw Bad("one closed-world counted main required");
        return string.Join("\n", statements);
    }
    private const string Helpers = """
    private static void Write(SelectableI64Channel c,long n) { if(!c.TryWrite(n)) throw new InvalidOperationException("E_READY_ONLY: write would suspend"); }
    private static long Read(SelectableI64Channel c) { if(!c.TryRead(out long n)) throw new InvalidOperationException("E_READY_ONLY: read would suspend"); return n; }
    private static long At(IReadOnlyList<long> a,int i) { if(i<0||i>=a.Count) throw new IndexOutOfRangeException("E_ARRAY_BOUNDS"); return a[i]; }
    private static SelectableI64Future Ready(Func<long> p) { var f=new SelectableI64Future(); try{f.Resolve(p());}catch(Exception e){f.Fail(e);} return f; }
    private static long Observe(SelectableI64Future f) { var s=f.SubscribeReady(static _=>{}); try{if(!f.TryCommit(s,out var r))throw new InvalidOperationException("E_READY_ONLY: Future would suspend"); if(r.Status!=SelectableI64Future.FutureStatus.Value)throw r.Error??new InvalidOperationException("Future failed"); return r.Value;}finally{s.Close();} }
    private static IReadOnlyList<long> Batch(int n,object[] s,Func<long,long>[] p,bool fair) {
      var b=new CountedSelectI64Batch(n,s,fair,p);
      try {
        for(int i=0;!b.Completion.IsCompleted&&i<2*(n+s.Length)+2;i++)
          if(!b.Pump())throw new InvalidOperationException("E_READY_ONLY: selection would suspend");
        if(!b.Completion.IsCompleted)throw new InvalidOperationException("E_READY_ONLY: owner pump budget");
        return b.Completion.GetAwaiter().GetResult();
      } finally { if(!b.Completion.IsCompleted)b.Cancel(); if(b.Completion.IsFaulted)_=b.Completion.Exception; }
    }
    """;
    internal static int Run(string input,string output)
    {
        string? temp=null;
        try
        {
            if (!input.EndsWith(".atto",StringComparison.Ordinal)||!output.EndsWith(".cs",StringComparison.Ordinal)) throw Bad("expected .atto input and .cs output");
            if (new FileInfo(input).Length > 65536) throw Bad("source limit");
            string source=File.ReadAllText(input,new UTF8Encoding(false,true));
            string body=new CountedReadyCompiler(source).Parse();
            string name="Guest_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
            string text="using System;\nusing System.Collections.Generic;\nusing Ores.Net.Runtime;\npublic static class "+name+" {\n"+Helpers+"\npublic static long Run(){\n"+body+"\n}}\n";
            string final=Path.GetFullPath(output);temp=Path.Combine(Path.GetDirectoryName(final)!,"."+Guid.NewGuid().ToString("N")+".tmp");
            using (var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using (var writer=new StreamWriter(stream,new UTF8Encoding(false))) writer.Write(text);
            File.Move(temp,final,false);temp=null;Console.WriteLine(name);return 0;
        }
        catch(CompilerError e){Console.Error.WriteLine(e.Code+": "+e.Message);return 2;}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or DecoderFallbackException){Console.Error.WriteLine("E_IO: "+e.Message);return 3;}
        finally { if(temp is not null && File.Exists(temp)) File.Delete(temp); }
    }
}
