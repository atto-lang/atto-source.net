using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

// Opt-in closed typed formal-slot profile; no general CLR async lowering.
// Reuse the production lexer, but allow take! and recovery binders only here.
public static partial class OresStructuredParser
{
    private enum UnwindKind { Body, Cleanup, Recover }
    private readonly record struct UnwindStage(UnwindKind Kind, int Slot);
    private sealed class OwnedUnwindReader(List<Token> tokens)
    {
        private int at;
        private readonly Dictionary<string,int> formals = new(StringComparer.Ordinal);
        private readonly HashSet<int> used = [];
        private string Peek => tokens[at].Text;
        private FormatException Error(string why) => new($"E_OWNED_UNWIND_PROFILE: {why} at offset {tokens[at].Offset}");
        private bool Eat(string word) { if(Peek!=word)return false;at++;return true; }
        private void Need(string word) { if(!Eat(word))throw Error("expected "+word); }
        private string Name()
        {
            string n=Peek;
            if(n.Length==0 || n.Length>128 || !(char.IsAsciiLetter(n[0])||n[0]=='_') ||
                n.Any(ch=>!(char.IsAsciiLetterOrDigit(ch)||ch=='_')) ||
                n is "pub" or "fnc" or "run" or "suspends" or "Future" or "int" or "defer" or "recover" or "return" or "await" or "take" or "move" or "borrow" or "raise" or "throw" or "panic" or "let" or "val" or "const" or "true" or "false" or "if" or "self" or "new" or "select" or "mut")
                throw Error("expected nonreserved ASCII binder");
            at++;return n;
        }
        private int Consume()
        {
            string n=Name();
            if(!formals.TryGetValue(n,out int slot))throw Error("await requires owned Future formal");
            if(!used.Add(slot))throw Error("Future formal used twice");
            return slot;
        }
        private long Literal()
        {
            bool neg=Eat("-");
            string n=Peek.Replace("_","",StringComparison.Ordinal);
            if(!ulong.TryParse(n,NumberStyles.None,CultureInfo.InvariantCulture,out ulong v)||
                v>(ulong)long.MaxValue+(neg?1UL:0UL))throw Error("signed i64 literal required");
            at++;return neg?(v==1UL<<63?long.MinValue:-(long)v):(long)v;
        }
        internal (List<UnwindStage> Stages,long Literal) Parse()
        {
            Need("pub");Need("suspends");Need("fnc");Need("run");Need("(");
            do {
                if(formals.Count==8)throw Error("formal quota");
                Need("take!");Need("Future");Need("<");Need("int");Need(">");
                if(!formals.TryAdd(Name(),formals.Count+1))throw Error("duplicate formal");
            } while(Eat(","));
            Need(")");Need(":");Need("int");Need("{");
            var cleanup=new List<UnwindStage>();UnwindStage? recovery=null;
            while(true) {
                if(Eat("defer")){Need("await");cleanup.Add(new(UnwindKind.Cleanup,Consume()));Need(";");}
                else if(Eat("recover")){
                    if(recovery.HasValue)throw Error("only one recovery");
                    Need("|");string err=Name();Need(",");string cursor=Name();Need("|");
                    if(err==cursor||formals.ContainsKey(err)||formals.ContainsKey(cursor))throw Error("recovery binder collision");
                    Need("->");Need("{");Need("return");Need("await");recovery=new(UnwindKind.Recover,Consume());Need(";");Need("}");Need(";");
                }else break;
            }
            Need("return");UnwindStage? body=Eat("await")?new(UnwindKind.Body,Consume()):null;
            long value=body.HasValue?0:Literal();Need(";");Need("}");
            if(Peek!=""||used.Count!=formals.Count||(recovery.HasValue&&!body.HasValue))throw Error("closed profile requires each formal once and recovery requires body await");
            var stages=new List<UnwindStage>();
            if(body.HasValue)stages.Add(body.Value);
            cleanup.Reverse();stages.AddRange(cleanup);
            if(recovery.HasValue)stages.Add(recovery.Value);
            return(stages,value);
        }
    }
    public static string EmitOwnedUnwindV1(string source)
    {
        ValidateSourceInput(source);
        if(source.Length>65536)throw new FormatException("E_OWNED_UNWIND_PROFILE: source cap");
        foreach(char ch in source)if(ch is '\u061c' or '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')
            throw new FormatException("E_SOURCE_BIDI");
        var lexer=new Lexer(source,ownedUnwind:true);
        var tokens=new List<Token>();
        while(true){var token=lexer.Read();tokens.Add(token);if(token.Text=="")break;if(tokens.Count>32768)throw new FormatException("E_OWNED_UNWIND_PROFILE: token cap");}
        var (stages,literal)=new OwnedUnwindReader(tokens).Parse();
        var b=new StringBuilder();
        b.AppendLine("""
            // Compiler-emitted bounded static continuation quanta; trusted host owns frame.
            using System;
            using System.Runtime.CompilerServices;
            namespace Atto.Generated;
            public static class OwnedUnwindGuest
            {
                [InlineArray(26)]
                public struct Frame { private long element; }
                private static bool Auth(ref Frame f,long owner,long epoch) => owner>0 && epoch>0 && f[0]==owner && f[1]==epoch;
                private static bool Terminal(ref Frame f,long owner,long epoch)=>Auth(ref f,owner,epoch)&&f[2]==32;
                private static void Merge(ref Frame f,long rank,long error)
                {
                    long sr=rank,se=error;
                    if(rank>f[3]) {sr=f[3];se=f[6];f[3]=rank;f[6]=error;}
                    if(sr==0)return;
                    int n=checked((int)f[9]);
                    if((uint)n>=8)throw new InvalidOperationException("E_UNWIND_SUPPRESSION");
                    f[10+n]=se;f[18+n]=sr;f[9]=n+1;
                }
                public static int Words=>26;
            """);
        b.AppendLine($"    public static int Arity=>{stages.Count};");
        string number=literal==long.MinValue?"long.MinValue":literal.ToString(CultureInfo.InvariantCulture)+"L";
        b.AppendLine($$"""
                public static int Start(ref Frame f,long owner,long epoch)
                {
                    if(owner<=0||epoch<=0||f[2]!=0)return -1;
                    f=default;f[0]=owner;f[1]=epoch;f[2]=1;f[5]={{number}};return 1;
                }
                public static int Pending(ref Frame f,long owner,long epoch)
                {
                    if(!Auth(ref f,owner,epoch))return -1;
                    return f[2] switch {
            """);
        for(int i=0;i<stages.Count;i++)b.AppendLine($"            {i+1}=>{stages[i].Slot},");
        b.AppendLine("""
                        _=>-1 };
                }
                public static int Complete(ref Frame f,long owner,long epoch,int slot,int kind,long value)
                {
                    if(!Auth(ref f,owner,epoch)||(uint)kind>2||(kind!=0&&value<=0))return -1;
                    switch(f[2]) {
            """);
        for(int i=0;i<stages.Count;i++)b.AppendLine($"            case {i+1}: if(slot!={stages[i].Slot})return -1; f[7]=kind;f[8]=value;f[2]={17+i};return 0;");
        b.AppendLine("""
                        default:return -1;}
                }
                public static int Resume(ref Frame f,long owner,long epoch)
                {
                    if(!Auth(ref f,owner,epoch))return -1;
                    switch(f[2]){
            """);
        for(int i=0;i<stages.Count;i++){
            var stage=stages[i];b.AppendLine($"            case {17+i}:");
            b.AppendLine("                if(f[7]==0){");
            if(stage.Kind is UnwindKind.Body or UnwindKind.Recover)b.AppendLine("                    f[5]=f[8];");
            if(stage.Kind==UnwindKind.Recover)b.AppendLine("                    f[4]=1;");
            b.AppendLine($"                }}else Merge(ref f,f[7]==2?3:{(stage.Kind==UnwindKind.Body?1:2)},f[8]);");
            b.AppendLine("                f[7]=0;f[8]=0;");
            if(i+1==stages.Count)b.AppendLine("                f[2]=32;return 2;");
            else {
                if(stages[i+1].Kind==UnwindKind.Recover)b.AppendLine("                if(f[3]!=1){f[2]=32;return 2;}");
                b.AppendLine($"                f[2]={i+2};return 1;");
            }
        }
        b.AppendLine("""
                        default:return -1;}
                }
                public static int Outcome(ref Frame f,long owner,long epoch)=>!Terminal(ref f,owner,epoch)?-1:f[4]==1?2:f[3]==0?1:(int)f[3]+2;
                public static int Result(ref Frame f,long owner,long epoch,ref long result)
                {
                    if(!Terminal(ref f,owner,epoch)||(f[3]!=0&&f[4]!=1))return -1;
                    result=f[5];return 0;
                }
                public static long Error(ref Frame f,long owner,long epoch)=>Terminal(ref f,owner,epoch)?f[6]:-1;
                public static long SuppressedCount(ref Frame f,long owner,long epoch)=>Terminal(ref f,owner,epoch)?f[9]:-1;
                public static long SuppressedError(ref Frame f,long owner,long epoch,int i)=>Terminal(ref f,owner,epoch)&&(uint)i<8&&i<f[9]?f[10+i]:-1;
                public static long SuppressedRank(ref Frame f,long owner,long epoch,int i)=>Terminal(ref f,owner,epoch)&&(uint)i<8&&i<f[9]?f[18+i]:-1;
                public static int Retire(ref Frame f,long owner,long epoch)
                {
                    if(!Terminal(ref f,owner,epoch))return -1;
                    f=default;f[2]=33;return 0;
                }
            }
            """);
        return b.ToString();
    }
}
