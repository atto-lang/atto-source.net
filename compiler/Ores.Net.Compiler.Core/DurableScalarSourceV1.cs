using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ores.Net.Compiler;

// Schema-only source profile. Fixed NativeAOT-compatible code, no reflection,
// guest authority, receive lowering or implicit admission of durable actors.
public static partial class OresParser
{
    public static string EmitDurableScalarV1(string source)
    {
        static FormatException Deny() => new("E_DURABLE_SCALAR_SCHEMA_V1");
        if (source is null || source.Length > 16384) throw Deny();
        var tokens = new List<string>();
        var lexer = new Regex(@"\G(?:[A-Za-z_][A-Za-z_0-9]*|[0-9]+|[:;=\-])", RegexOptions.CultureInvariant);
        for (int p = 0; p < source.Length;)
        {
            if (source[p] is ' ' or '\t' or '\r' or '\n') { p++; continue; }
            Match token = lexer.Match(source,p);
            if (!token.Success || token.Index != p || tokens.Count >= 4096) throw Deny();
            tokens.Add(token.Value);p += token.Length;
        }
        int i = 0;
        string Peek() => i < tokens.Count ? tokens[i] : "";
        string Next() { string t=Peek();if(t.Length==0)throw Deny();i++;return t; }
        void Take(params string[] words) { foreach(string t in words)if(Next()!=t)throw Deny(); }
        const string reserved = " define pub private durable persistent actor as state end int bool true false self receive init fnc mtd let val mut const return await suspends susp async critical using with import namespace module class struct contract enum new spawn stop if else match on default void ";
        string Name()
        {
            string n=Next();
            if(n.Length>128||!Regex.IsMatch(n,@"\A[A-Za-z_][A-Za-z_0-9]*\z",RegexOptions.CultureInvariant)||reserved.Contains(" "+n+" ",StringComparison.Ordinal))throw Deny();
            return n;
        }
        static ulong Hash(ReadOnlySpan<byte> bytes)
        {
            ulong h=0xcbf29ce484222325UL;foreach(byte b in bytes)h=unchecked((h^b)*0x100000001b3UL);return h;
        }
        static void Le(List<byte> bytes,ulong n,int width) { for(int j=0;j<width;j++)bytes.Add(unchecked((byte)(n>>(8*j)))); }
        var signature=new List<byte>(Encoding.ASCII.GetBytes("atto.actor.state.schema.v1\0"));
        void Sized(string n) {byte[] bytes=Encoding.ASCII.GetBytes(n);Le(signature,(ulong)bytes.Length,4);signature.AddRange(bytes);}
        Take("define","durable","actor");Sized(Name());Take("as");
        var names=new HashSet<string>(StringComparer.Ordinal);
        var tags=new List<byte>();var payload=new List<byte>();
        while(Peek()=="state")
        {
            if(tags.Count>=256)throw Deny();
            Take("state");string name=Name();if(!names.Add(name))throw Deny();Sized(name);Take(":");string type=Next();Take("=");
            if(type=="int")
            {
                bool negative=Peek()=="-";if(negative)Next();string literal=Next();
                if(!Regex.IsMatch(literal,@"\A(?:0|[1-9][0-9]{0,18})\z",RegexOptions.CultureInvariant)||
                   !long.TryParse((negative?"-":"")+literal,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out long value))throw Deny();
                tags.Add(1);signature.Add(1);payload.Add(1);Le(payload,unchecked((ulong)value),8);
            }
            else if(type=="bool")
            {
                string b=Next();if(b is not ("true" or "false"))throw Deny();
                tags.Add(2);signature.Add(2);payload.Add(2);payload.Add((byte)(b=="true"?1:0));
            }
            else throw Deny();
            Take(";");
        }
        Take("end");if(i!=tokens.Count)throw Deny();
        var initial=new List<byte>(Encoding.ASCII.GetBytes("ATTOST01"));initial.Add(1);Le(initial,Hash(signature.ToArray()),8);Le(initial,(ulong)tags.Count,2);initial.AddRange(payload);Le(initial,Hash(initial.ToArray()),8);
        string hex=Convert.ToHexString(initial.ToArray());
        string tagText=string.Join(",",tags.ConvertAll(t=>t.ToString(CultureInfo.InvariantCulture)));
        return """
using System;
using System.Buffers.Binary;
public static class AttoDurableScalarV1
{
    private static readonly byte[] Template=Convert.FromHexString("__INITIAL__");
    private static readonly byte[] Tags=new byte[]{__TAGS__};
    public static byte[] Initial() => (byte[])Template.Clone();
    private static FormatException Deny()=>new("E_DURABLE_SCALAR_CHECKPOINT_V1");
    private static ulong Hash(ReadOnlySpan<byte> bytes){
        ulong h=0xcbf29ce484222325UL;foreach(byte b in bytes)h=unchecked((h^b)*0x100000001b3UL);return h;
    }
    // Flattened ABI: the compiled schema determines the type of each slot;
    // booleans must be exactly 0 or 1. Caller supplies quiescent owned values.
    public static byte[] Encode(params long[] values){
        if(values is null||values.Length!=Tags.Length)throw Deny();
        long[] owned=(long[])values.Clone();byte[] b=Initial();int p=19;
        for(int i=0;i<Tags.Length;i++){
            p++;long v=owned[i];
            if(Tags[i]==1){BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p,8),v);p+=8;}
            else {if(v!=0&&v!=1)throw Deny();b[p++]=(byte)v;}
        }
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p,8),Hash(b.AsSpan(0,p)));return b;
    }
    public static long[] Decode(byte[] input){
        if(input is null||input.Length!=Template.Length)throw Deny();
        byte[] b=(byte[])input.Clone();int end=b.Length-8;
        if(!b.AsSpan(0,19).SequenceEqual(Template.AsSpan(0,19))||
           BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(end,8))!=Hash(b.AsSpan(0,end)))throw Deny();
        long[] staged=new long[Tags.Length];int p=19;
        for(int i=0;i<Tags.Length;i++){
            byte tag=Tags[i];if(b[p++]!=tag)throw Deny();
            if(tag==1){staged[i]=BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p,8));p+=8;}
            else {byte v=b[p++];if(v>1)throw Deny();staged[i]=v;}
        }
        if(p!=end)throw Deny();return staged;
    }
}
""".Replace("__INITIAL__",hex,StringComparison.Ordinal).Replace("__TAGS__",tagText,StringComparison.Ordinal);
    }
}
