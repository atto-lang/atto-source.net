using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ores.Net.Compiler;

// Local trusted-ledger receipt primitive. The 32-byte secret is external to
// the cache and must be held only by a trusted compiler publisher/reader.
// Expected keys must come from independently authenticated current inputs.
// Never interpret a verified envelope as an executable loading grant.
public static class CompilerArtifactReceipt
{
    public const int MaxArtifact=4*1024*1024,Header=112;
    private static readonly byte[] Magic=Encoding.ASCII.GetBytes("ATTOCAC1");
    private static readonly byte[] Domain=Encoding.ASCII.GetBytes("atto.compiler.artifact-receipt.v1\0");
    private static FormatException Invalid()=>new("E_CACHE_RECEIPT: invalid envelope or key");
    private static byte[] Key(string key)
    {
        if(key is null||key.Length!=64||key.Any(c=>!(c is >= '0' and <= '9' or >= 'a' and <= 'f')))throw Invalid();
        return Convert.FromHexString(key);
    }
    private static void Secret(byte[] secret){if(secret is null||secret.Length!=32)throw Invalid();}
    private static byte[] Tag(byte[] secret,byte[] envelope)
    {
        var message=new byte[Domain.Length+80];
        Domain.CopyTo(message,0);envelope.AsSpan(0,80).CopyTo(message.AsSpan(Domain.Length));
        return HMACSHA256.HashData(secret,message);
    }
    public static byte[] Seal(string expectedKey,byte[] artifact,byte[] trustedSecret)
    {
        Secret(trustedSecret);
        if(artifact is null||artifact.Length>MaxArtifact)throw Invalid();
        var bytes=(byte[])artifact.Clone();
        var result=new byte[Header+bytes.Length];
        Magic.CopyTo(result,0);Key(expectedKey).CopyTo(result,8);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(40,8),(ulong)bytes.Length);
        SHA256.HashData(bytes).CopyTo(result,48);Tag(trustedSecret,result).CopyTo(result,80);
        bytes.CopyTo(result,Header);return result;
    }
    public static byte[] Verify(string expectedKey,byte[] envelope,byte[] trustedSecret)
    {
        Secret(trustedSecret);var expected=Key(expectedKey);
        if(envelope is null||envelope.Length<Header||envelope.Length>Header+MaxArtifact)throw Invalid();
        var data=(byte[])envelope.Clone();
        if(!data.AsSpan(0,8).SequenceEqual(Magic)||
           !CryptographicOperations.FixedTimeEquals(expected,data.AsSpan(8,32))||
           BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(40,8))!=(ulong)(data.Length-Header)||
           !CryptographicOperations.FixedTimeEquals(Tag(trustedSecret,data),data.AsSpan(80,32)))throw Invalid();
        var bytes=data.AsSpan(Header).ToArray();
        if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),data.AsSpan(48,32)))throw Invalid();
        return bytes;
    }
}
