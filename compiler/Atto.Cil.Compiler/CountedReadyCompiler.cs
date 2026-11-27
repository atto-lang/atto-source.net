using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Atto.Cil.Compiler;

// Explicit closed-world ready-only profile, emitting ordinary statically
// compiled C# against the existing NativeAOT runtime. No guest eval/reflection.
internal sealed class CountedReadyCompiler
{
    private sealed record Value(string Type, string Code);
    private sealed record Slot(string Type, string Code) { internal bool Moved; internal bool Mutable; }
    private readonly IReadOnlyList<string> tokens;
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private int pos, budget = 4096, depth, countSites;
    private string? outstanding;
    private bool pureCondition;
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
        if (Eat("Future")) { if (!Eat("<")) return "inferFuture"; if (Eat("Array")) { Need("<"); Need("int"); Need(">"); Need(">"); return "futureArray"; } Need("int"); Need(">"); return "future"; }
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
                if (op is "+" or "-" or "*") left = new("int", (op == "+" ? "Add" : op == "-" ? "Sub" : "Mul") + "(" + Check(left,"int") + "," + Check(right,"int") + ")");
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
        if (Eat("-")) return new("int","Neg(" + Check(Expr(7,binding),"int") + ")");
        if (Peek.Length > 0 && char.IsAsciiDigit(Peek[0])) return new("int", Literal().ToString(CultureInfo.InvariantCulture) + "L");
        if (binding is not null)
        {
            string boundName = Name();
            if (binding == "@count") return new("int",Get(boundName,"int").Code);
            if (boundName != binding) throw Bad("projection may reference only its binder");
            return new("int","x");
        }
        if (Eat("await"))
        {
            if (pureCondition) throw Bad("effect in condition");
            bool legacy = Eat("nb");
            if (Eat("select")) return SelectResult(true);
            if (legacy) throw Bad("nb requires select");
            string name = Name();
            if (slots.TryGetValue(name,out var pending) && pending.Type == "futureArray" && !pending.Moved && outstanding == pending.Code)
            {
                pending.Moved = true; outstanding = null;
                return new("array","Finish(" + pending.Code + ",owned)");
            }
            if (outstanding is not null) throw Bad("await outstanding counted batch first");
            var future = Get(name,"future"); future.Moved = true; return new("int","Observe(" + future.Code + ")");
        }
        if (Eat("readch"))
        {
            if (pureCondition) throw Bad("effect in condition");
            if (outstanding is not null) throw Bad("await outstanding counted batch before channel access");
            return new("int","Read(" + Get(Name(),"channel").Code + ")");
        }
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
    private Value Counted(bool direct)
    {
        if (outstanding is not null) throw Bad("await outstanding counted batch first");
        Need("("); string count = Check(Expr(0,"@count"),"int"); Need(")");
        bool literal = count.EndsWith("L",StringComparison.Ordinal) && long.TryParse(count[..^1],out _);
        long constant = literal ? long.Parse(count[..^1],CultureInfo.InvariantCulture) : -1;
        if (literal && constant > 16) throw Bad("SelectCountInvalid");
        if (count.StartsWith("Neg(",StringComparison.Ordinal) && count.EndsWith("L)",StringComparison.Ordinal) && long.TryParse(count[4..^2],out var neg) && neg > 0) throw Bad("SelectCountInvalid");
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
        if (!literal || constant != 0) foreach (var f in futures) f.Moved = true;
        string plan = literal && constant == 0 ? "static () => ([], [])" :
            "() => ([" + string.Join(",",sources) + "], [" + string.Join(",",projections) + "])";
        string start = "Start(" + count + "," + plan + "," + (fair ? "true" : "false") + ",owned,fence)";
        return direct ? new("array","Finish(" + start + ",owned)") : new("futureArray",start);
    }
    private Value SelectResult(bool direct)
    {
        if (outstanding is not null) throw Bad("await outstanding counted batch first");
        if (Eat("<")) { Need("int"); Need(">"); }
        if (Peek == "(") return Counted(direct);
        bool over = Eat("over"); if (!over) Need("{");
        Need("default"); Need(":"); Need("->"); Need("{"); Need("return");
        string code = "Ready(static () => " + Check(Expr(0,"$no_binding"),"int") + ")";
        Need(";"); Need("}"); Need(over ? "end" : "}");
        return direct ? new("int","Observe(" + code + ")") : new("future",code);
    }
    private string Bind(bool mutable = false)
    {
        string type = Type(), n = Name();
        if (slots.ContainsKey(n) || slots.Count >= 128) throw Bad("duplicate or excessive lexical binding");
        if (mutable && type != "int") throw Bad("only scalar mutation admitted");
        Need("="); string code;
        if (type == "channel")
        {
            if (outstanding is not null) throw Bad("await outstanding counted batch first");
            Need("Channel"); Need("."); Need("new"); Need("<"); Need("int"); Need(">"); Need("(");
            int cap = Small(64); if (cap == 0) throw Bad("zero capacity"); Need(")"); code = "new SelectableI64Channel(" + cap + ")";
        }
        else if (type is "future" or "futureArray" or "inferFuture")
        {
            Eat("nb"); Need("select"); Value selected = SelectResult(false);
            if (type == "inferFuture") type = selected.Type;
            code = Check(selected,type);
        }
        else code = Check(Expr(),type);
        Need(";"); string slot = "v" + slots.Count; slots.Add(n,new Slot(type,slot){ Mutable=mutable });
        if (type == "futureArray") outstanding = slot;
        return "var " + slot + "=" + code + ";";
    }
    private string Parse()
    {
        Eat("pub"); Need("suspends"); Need("fnc"); Need("main"); Need("("); Need(")"); Need(":"); Need("int"); Need("{");
        List<string> statements = [];
        while (Peek != "return")
        {
            if (statements.Count >= 256) throw Bad("statement quota");
            if (Eat("val") || Eat("const")) statements.Add(Bind());
            else if (Eat("let")) statements.Add(Bind(true));
            else if (Eat("writech")) { if (outstanding is not null) throw Bad("await outstanding counted batch first"); var s = Get(Name(),"channel"); Need(","); var v = Check(Expr(),"int"); Need(";"); statements.Add("Write(" + s.Code + "," + v + ");"); }
            else if (Eat("if"))
            {
                if (outstanding is not null) throw Bad("await outstanding counted batch before branching");
                string cond;
                pureCondition = true;
                try { cond = Check(Expr(),"bool"); }
                finally { pureCondition = false; }
                Need("then"); Need("return");
                var before = slots.Values.ToDictionary(s => s.Code,s => s.Moved);
                string value = Check(Expr(),"int"); Need(";"); Need("fi");
                foreach (var s in slots.Values) s.Moved = before[s.Code];
                statements.Add("if(" + cond + ")return " + value + ";");
            }
            else if (pos + 1 < tokens.Count && tokens[pos+1] == "=") { var s = Get(Name(),"int"); if (!s.Mutable) throw Bad("immutable binding"); Need("="); string v = Check(Expr(),"int"); Need(";"); statements.Add(s.Code+"="+v+";"); }
            else throw Bad("unsupported statement");
        }
        Need("return"); statements.Add("return " + Check(Expr(),"int") + ";"); Need(";"); Need("}");
        if (outstanding is not null) throw Bad("outstanding batch must be awaited");
        if (Peek != "<eof>" || countSites == 0) throw Bad("one closed-world counted main required");
        return string.Join("\n", statements);
    }
    private const string Helpers = """
    private static void Write(SelectableI64Channel c,long n) { if(!c.TryWrite(n)) throw new InvalidOperationException("E_READY_ONLY: write would suspend"); }
    private static long Read(SelectableI64Channel c) { if(!c.TryRead(out long n)) throw new InvalidOperationException("E_READY_ONLY: read would suspend"); return n; }
    private static long At(IReadOnlyList<long> a,int i) { if(i<0||i>=a.Count) throw new IndexOutOfRangeException("E_ARRAY_BOUNDS"); return a[i]; }
    private static SelectableI64Future Ready(Func<long> p) { var f=new SelectableI64Future(); try{f.Resolve(p());}catch(Exception e){f.Fail(e);} return f; }
    private static long Observe(SelectableI64Future f) { var s=f.SubscribeReady(static _=>{}); try{if(!f.TryCommit(s,out var r))throw new InvalidOperationException("E_READY_ONLY: Future would suspend"); if(r.Status!=SelectableI64Future.FutureStatus.Value)throw r.Error??new InvalidOperationException("Future failed"); return r.Value;}finally{s.Close();} }
    private static long Add(long a,long b)=>checked(a+b);
    private static long Sub(long a,long b)=>checked(a-b);
    private static long Mul(long a,long b)=>checked(a*b);
    private static long Neg(long a)=>checked(-a);
    private sealed record Pending(CountedSelectI64Batch Batch,int Count,int Arms,SelectContinuationFence.Execution Lease);
    private static Pending Start(long count,Func<(object[],Func<long,long>[])> plan,bool fair,HashSet<Pending> owned,SelectContinuationFence fence) {
      if(count<0||count>16)throw new ArgumentOutOfRangeException(nameof(count),"SelectCountInvalid");
      var (s,p)=count==0?(Array.Empty<object>(),Array.Empty<Func<long,long>>()):plan();
      var ticket=fence.Register(fence.Generation);
      var lease=fence.TryClaim(ticket);
      if(lease is null){ticket.Close();throw new InvalidOperationException("counted guest execution admission failed");}
      try{
        var record=new Pending(new CountedSelectI64Batch((int)count,s,fair,p),(int)count,s.Length,lease);
        owned.Add(record); return record;
      }catch{lease.Dispose();throw;}
    }
    private static IReadOnlyList<long> Finish(Pending record,HashSet<Pending> owned) {
      var b=record.Batch;
      try {
        for(int i=0;!b.Completion.IsCompleted&&i<2*(record.Count+record.Arms)+2;i++)
          if(!b.Pump())throw new InvalidOperationException("E_READY_ONLY: selection would suspend");
        if(!b.Completion.IsCompleted)throw new InvalidOperationException("E_READY_ONLY: owner pump budget");
        return b.Completion.GetAwaiter().GetResult();
      } finally { try{Retire(record);}finally{owned.Remove(record);} }
    }
    private static void Retire(Pending record) {
      // Pump and Cancel are synchronous, serialized owner calls. No worker
      // remains physically executing after they return or throw.
      try{var b=record.Batch; if(!b.Completion.IsCompleted)b.Cancel(); if(b.Completion.IsFaulted)_=b.Completion.Exception;}
      finally{record.Lease.Dispose();}
    }
    private static void Drain(HashSet<Pending> owned,SelectContinuationFence fence) {
      fence.Close();
      List<Exception> errors=new();
      foreach(var record in owned){try{Retire(record);}catch(Exception error){errors.Add(error);}}
      owned.Clear();
      if(!fence.RetirementReady)errors.Add(new InvalidOperationException("counted guest physical execution not retired"));
      if(errors.Count!=0)throw new AggregateException("counted guest retirement failure",errors);
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
            string text="using System;\nusing System.Collections.Generic;\nusing Ores.Net.Runtime;\npublic static class "+name+" {\n"+Helpers+"\npublic static long Run(){\nvar owned=new HashSet<Pending>();\nvar fence=new SelectContinuationFence(maxTickets:128);\ntry{\n"+body+"\n}finally{Drain(owned,fence);}\n}}\n";
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
