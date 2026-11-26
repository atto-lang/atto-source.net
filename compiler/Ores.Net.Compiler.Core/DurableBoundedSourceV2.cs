using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ores.Net.Compiler;

// Draft schema manifest only. No ordinary actor lowering or storage authority.
public static class DurableBoundedSourceV2
{
    public enum Kind : byte { Int=1, Bool=2, String=3, Bytes=4 }
    public sealed record Field(string Name, Kind Type);
    private static FormatException Deny()=>new("E_DURABLE_BOUNDED_SCHEMA_V2");
    private static bool NameOk(string name)=>name is not null && name.Length<=128 &&
        Regex.IsMatch(name,@"\A[A-Za-z_][A-Za-z_0-9]*\z",RegexOptions.CultureInvariant);
    private static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong h=0xcbf29ce484222325UL;
        foreach(byte b in bytes)h=unchecked((h^b)*0x100000001b3UL);
        return h;
    }
    public static string Emit(string actor,IReadOnlyList<Field> fields)
    {
        // Trusted compiler-owned manifest, never a guest-supplied object.
        if(!NameOk(actor)||fields is null||fields.Count>256)throw Deny();
        var sig=new List<byte>(Encoding.ASCII.GetBytes("atto.actor.state.schema.v2\0"));
        void Sized(string n){byte[] raw=Encoding.ASCII.GetBytes(n);sig.Add((byte)raw.Length);sig.AddRange(new byte[3]);sig.AddRange(raw);}
        Sized(actor);
        var names=new HashSet<string>(StringComparer.Ordinal);
        var tags=new List<string>();
        foreach(Field f in fields)
        {
            if(f is null||!NameOk(f.Name)||!names.Add(f.Name)||(byte)f.Type<1||(byte)f.Type>4)throw Deny();
            Sized(f.Name);sig.Add((byte)f.Type);tags.Add(((byte)f.Type).ToString(CultureInfo.InvariantCulture));
        }
        return """
using System;
using System.Buffers.Binary;
using System.Text;
public static class AttoDurableBoundedV2
{
    private const ulong Schema=0x__SCHEMA__UL;
    private static readonly byte[] Tags={__TAGS__};
    private static readonly UTF8Encoding Utf8=new(false,true);
    private const int Max=65536,FieldMax=16384;
    private static FormatException Deny()=>new("E_DURABLE_BOUNDED_CHECKPOINT_V2");
    private static ulong Hash(ReadOnlySpan<byte> bytes){ulong h=0xcbf29ce484222325UL;foreach(byte b in bytes)h=unchecked((h^b)*0x100000001b3UL);return h;}
    private static void Room(int p,int n,int end){if(p<0||p>end||n<0||n>end-p)throw Deny();}
    private static byte[] StringBytes(string s){
        if(s.Length>FieldMax)throw Deny();
        try{int n=Utf8.GetByteCount(s);if(n>FieldMax)throw Deny();return Utf8.GetBytes(s);}
        catch(EncoderFallbackException){throw Deny();}
    }
    public static byte[] Encode(params object[] values){
        if(values is null||values.Length!=Tags.Length)throw Deny();
        object[] owned=(object[])values.Clone();byte[] b=new byte[Max];
        "ATTOST02"u8.CopyTo(b);b[8]=2;BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(9),Schema);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(17),(ushort)Tags.Length);int p=19;
        for(int i=0;i<Tags.Length;i++){
            byte tag=Tags[i];object x=owned[i];Room(p,1,Max-8);b[p++]=tag;
            if(tag==1){if(x is not long v)throw Deny();Room(p,8,Max-8);BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p),v);p+=8;}
            else if(tag==2){if(x is not bool v)throw Deny();Room(p,1,Max-8);b[p++]=(byte)(v?1:0);}
            else{
                byte[] raw;
                if(tag==3){if(x is not string s)throw Deny();raw=StringBytes(s);}
                else{if(x is not byte[] v||v.Length>FieldMax)throw Deny();raw=(byte[])v.Clone();}
                Room(p,4+raw.Length,Max-8);BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p),(uint)raw.Length);p+=4;raw.CopyTo(b,p);p+=raw.Length;
            }
        }
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p),Hash(b.AsSpan(0,p)));return b.AsSpan(0,p+8).ToArray();
    }
    public static object[] Decode(byte[] input){
        if(input is null||input.Length<27||input.Length>Max)throw Deny();
        byte[] b=(byte[])input.Clone();int end=b.Length-8;
        if(!b.AsSpan(0,8).SequenceEqual("ATTOST02"u8)||b[8]!=2||BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(9))!=Schema||
           BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(17))!=Tags.Length||BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(end))!=Hash(b.AsSpan(0,end)))throw Deny();
        object[] output=new object[Tags.Length];int p=19;
        for(int i=0;i<Tags.Length;i++){
            byte tag=Tags[i];Room(p,1,end);if(b[p++]!=tag)throw Deny();
            if(tag==1){Room(p,8,end);output[i]=BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p));p+=8;}
            else if(tag==2){Room(p,1,end);byte v=b[p++];if(v>1)throw Deny();output[i]=v==1;}
            else{
                Room(p,4,end);uint n=BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));p+=4;if(n>FieldMax)throw Deny();Room(p,(int)n,end);
                if(tag==3){try{output[i]=Utf8.GetString(b,p,(int)n);}catch(DecoderFallbackException){throw Deny();}}
                else output[i]=b.AsSpan(p,(int)n).ToArray();
                p+=(int)n;
            }
        }
        if(p!=end)throw Deny();return output;
    }
}
""".Replace("__SCHEMA__",Hash(sig.ToArray()).ToString("x16",CultureInfo.InvariantCulture),StringComparison.Ordinal)
   .Replace("__TAGS__",string.Join(",",tags),StringComparison.Ordinal);
    }
}
