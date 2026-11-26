using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

// Narrow broker-owned Counter ABI, not the general actor compiler.
// The production lexer admits a complete exact receive shape before emitting
// native-publishable C#; no source interpolation or storage privileges.
public static partial class OresParser
{
    public static string EmitDurableCounterV1(string source)
    {
        if (source.Length > 16384 || source.Any(c => c >= 127 || (c < 32 && c is not ('\t' or '\r' or '\n'))))
            throw new FormatException("E_DURABLE_COUNTER_ACTOR_ABI: bounded ASCII source required");
        var tokens = Tokenize(source);
        int i = 0;
        string Peek() => i < tokens.Count ? tokens[i].Text : "";
        void Expect(params string[] words)
        {
            foreach (string word in words)
            {
                if (i >= tokens.Count || Peek() != word)
                    throw new FormatException("E_DURABLE_COUNTER_ACTOR_ABI: unsupported actor shape");
                i++;
            }
        }
        Expect("define","durable","actor","Counter","as","state");
        string field = Peek();
        if (field is not ("count" or "balance"))
            throw new FormatException("E_DURABLE_COUNTER_ACTOR_ABI: unsupported field");
        Expect(field,":","int","=","0",";","receive","(","ActorMail","<","int",">");
        string mail = Peek();
        if (mail is not ("mail" or "event"))
            throw new FormatException("E_DURABLE_COUNTER_ACTOR_ABI: unsupported mail parameter");
        Expect(mail,")","->","void","{","self",".",field,"=","self",".",field,"+",mail,".","value",";","}","end","");
        if (i != tokens.Count) throw new FormatException("E_DURABLE_COUNTER_ACTOR_ABI: trailing source");

        static ulong Fnv(IEnumerable<byte> bytes)
        {
            ulong h=0xcbf29ce484222325UL;
            foreach (byte b in bytes) h=unchecked((h^b)*0x100000001b3UL);
            return h;
        }
        static void Sized(List<byte> bytes,string text)
        {
            byte[] raw=Encoding.ASCII.GetBytes(text);
            byte[] size=new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size,(uint)raw.Length);
            bytes.AddRange(size);bytes.AddRange(raw);
        }
        var signature = new List<byte>(Encoding.ASCII.GetBytes("atto.actor.state.schema.v1\0"));
        Sized(signature,"Counter");Sized(signature,field);signature.Add(1);
        byte[] snapshot=new byte[36];
        Encoding.ASCII.GetBytes("ATTOST01").CopyTo(snapshot,0);snapshot[8]=1;
        BinaryPrimitives.WriteUInt64LittleEndian(snapshot.AsSpan(9),Fnv(signature));
        BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(17),1);snapshot[19]=1;
        BinaryPrimitives.WriteUInt64LittleEndian(snapshot.AsSpan(28),Fnv(snapshot.Take(28)));
        var descriptor=new List<byte>{1,7};
        descriptor.AddRange(Encoding.ASCII.GetBytes("Counter"));
        descriptor.Add((byte)field.Length);descriptor.AddRange(Encoding.ASCII.GetBytes(field));
        descriptor.Add(1);descriptor.Add(36);descriptor.AddRange(snapshot);
        string hex=Convert.ToHexString(descriptor.ToArray());
        return """
public static class AttoDurableCounterV1
{
    public static byte[] Descriptor() => System.Convert.FromHexString(
""" + "\n        \"" + hex + "\");\n" + """
    public static long Transition(long state,long op,long delta)
    {
        const long maximum=1000000000L;
        if(state<0||state>maximum)return -1;
        if(op==2)return delta==0?state:-1;
        if(op!=1||delta<=0||delta>maximum||state>maximum-delta)return -1;
        return checked(state+delta);
    }
}
""";
    }
}
