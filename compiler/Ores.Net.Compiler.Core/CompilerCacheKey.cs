using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ores.Net.Compiler;

// Canonical atto-spec cache key v1. No artifact authenticity or loading authority.
public static class CompilerCacheKey
{
    public sealed record Dependency(string Identity,string Digest);
    public sealed record Input(string Stage,string Owner,string UnitKind,string Unit,
        string Compiler,string Toolchain,string Target,string Configuration,string Sources,
        string Bindings,string? Generated,IReadOnlyList<Dependency> Dependencies);
    private static FormatException Invalid(string reason)=>new("E_CACHE_KEY: "+reason);
    private static void U32(Stream stream,int value)
    {
        Span<byte> bytes=stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes,(uint)value);
        stream.Write(bytes);
    }
    private static byte[] Identity(string value)
    {
        if(value is null||value.Length is <1 or >512||value.Any(c=>c<33||c>126))throw Invalid("identity");
        return Encoding.ASCII.GetBytes(value);
    }
    private static byte[] Digest(string value)
    {
        if(value is null||value.Length!=64||value.Any(c=>!(c is >= '0' and <= '9' or >= 'a' and <= 'f')))throw Invalid("digest");
        return Convert.FromHexString(value);
    }
    private static void WriteIdentity(Stream stream,byte[] bytes)
    {
        U32(stream,bytes.Length);stream.Write(bytes);
    }
    public static string Encode(Input input)
    {
        if(input is null||input.UnitKind is not ("namespace" or "source-unit"))throw Invalid("unit kind");
        if(input.Dependencies is null||input.Dependencies.Count>128)throw Invalid("dependency budget");
        var ordered=new SortedDictionary<string,(byte[] Id,byte[] Hash)>(StringComparer.Ordinal);
        int count=0;
        foreach(var dependency in input.Dependencies){
            if(++count>128||dependency is null)throw Invalid("dependency budget or null");
            if(!ordered.TryAdd(dependency.Identity??throw Invalid("identity"),(Identity(dependency.Identity),Digest(dependency.Digest))))
                throw Invalid("duplicate dependency");
        }
        using var stream=new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("atto.compiler.cache-key.v1\0"));
        foreach(string value in new[]{input.Stage,input.Owner,input.UnitKind,input.Unit})WriteIdentity(stream,Identity(value));
        foreach(string value in new[]{input.Compiler,input.Toolchain,input.Target,input.Configuration,input.Sources,input.Bindings})stream.Write(Digest(value));
        stream.WriteByte(input.Generated is null?(byte)0:(byte)1);
        if(input.Generated is not null)stream.Write(Digest(input.Generated));
        U32(stream,ordered.Count);
        foreach(var dependency in ordered.Values){WriteIdentity(stream,dependency.Id);stream.Write(dependency.Hash);}
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}
