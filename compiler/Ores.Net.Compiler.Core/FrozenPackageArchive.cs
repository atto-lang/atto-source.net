using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Ores.Net.Compiler;

// Explicit host transport; no extraction, package discovery or trust derived
// from the archive. The expected lock SHA256 is a separately supplied pin.
public static class FrozenPackageArchive
{
    private const int MaxArchive=8*1024*1024, MaxTotal=8*1024*1024;
    public static byte[] ReadBounded(string path,int limit)
    {
        if(limit is <1 or >MaxArchive) throw new ArgumentOutOfRangeException(nameof(limit));
        if((File.GetAttributes(path)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)
            throw new FormatException("E_IMPORT_PATH: expected explicit regular file");
        using var stream=File.OpenRead(path);
        return ReadBounded(stream,limit);
    }
    private static byte[] ReadBounded(Stream stream,int limit)
    {
        using var result=new MemoryStream();
        byte[] buffer=new byte[8192];
        while(true) {
            int remaining=limit-(int)result.Length;
            int count=stream.Read(buffer,0,Math.Min(buffer.Length,remaining+1));
            if(count==0) return result.ToArray();
            if(count>remaining) throw new FormatException("E_SOURCE_LIMIT: expanded input exceeds budget");
            result.Write(buffer,0,count);
        }
    }
    private static bool Component(string s)
    {
        static bool AlphaNum(char c)=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
        return s.Length is >=1 and <=64 && AlphaNum(s[0]) && s.All(c=>AlphaNum(c)||c is '-' or '_');
    }
    public static FrozenImportSnapshot Read(string archive,string name,string version,string trustedLock)
    {
        using var bytes=new MemoryStream(ReadBounded(archive,MaxArchive),writable:false);
        using var zip=new ZipArchive(bytes,ZipArchiveMode.Read);
        if(zip.Entries.Count>66) throw new FormatException("E_SOURCE_LIMIT: archive entry budget exceeded");
        var sources=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        var names=new HashSet<string>(StringComparer.Ordinal);
        byte[]? manifest=null,lockBytes=null;
        int total=0;
        foreach(var entry in zip.Entries) {
            string key=entry.FullName;
            if(!names.Add(key)||key.EndsWith('/'))
                throw new FormatException("E_IMPORT_PATH: duplicate or directory archive entry");
            int limit=key switch {
                ".atto.pkg.toml"=>8192,
                ".atto.pkg.lock"=>32768,
                _ when key.Length<=256&&key.EndsWith(".atto",StringComparison.Ordinal)&&
                    key[..^5].Split('/').All(Component)=>1048576,
                _=>throw new FormatException("E_IMPORT_PATH: invalid archive entry name")
            };
            if(entry.Length>limit||entry.Length>MaxTotal-total)
                throw new FormatException("E_SOURCE_LIMIT: expanded archive budget exceeded");
            using var input=entry.Open();
            byte[] data=ReadBounded(input,Math.Min(limit,MaxTotal-total));
            total+=data.Length;
            if(key==".atto.pkg.toml") manifest=data;
            else if(key==".atto.pkg.lock") lockBytes=data;
            else sources.Add(key,data);
        }
        if(manifest is null||lockBytes is null) throw new FormatException("E_LOCK: missing manifest or lock entry");
        return FrozenImportSnapshot.Verify(name,version,trustedLock,manifest,lockBytes,sources);
    }
}
