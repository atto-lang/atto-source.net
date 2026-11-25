using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ores.Net.Compiler;

// Frozen in-memory registry metadata. Expected lock digest is independently trusted.
// No filesystem/network access or caller-supplied export authority.
public sealed partial class FrozenImportSnapshot
{
    private const int MaxManifest=8192, MaxLock=32768, MaxFiles=64, MaxSource=1048576;
    private readonly Dictionary<string,string> namespaces, exports, sources;
    public sealed record Dependency(string Version,string LockDigest);
    public IReadOnlyDictionary<string,Dependency> Dependencies {get;}
    internal IReadOnlyList<string> NamespaceNames => namespaces.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    public string Name {get;}
    public string Version {get;}
    public string LockDigest {get;}
    private FrozenImportSnapshot(string name,string version,string digest,Dictionary<string,string> ns,
        Dictionary<string,string> ex,Dictionary<string,string> src,Dictionary<string,Dependency> dependencies)
    { Name=name; Version=version; LockDigest=digest; namespaces=ns; exports=ex; sources=src; Dependencies=new System.Collections.ObjectModel.ReadOnlyDictionary<string,Dependency>(dependencies); }
    private static FormatException Fail(string code,string msg)=>new(code+": "+msg);
    private static bool Digest(string? s)=>s is not null && s.Length==64 && s.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Sha(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static string Text(byte[] b,string code) {
        try {return new UTF8Encoding(false,true).GetString(b);}
        catch(DecoderFallbackException) {throw Fail(code,"invalid UTF-8");}
    }
    private static byte[] Bounded(byte[]? b,int max,string code) {
        if(b is null||b.Length>max) throw Fail(code,"input exceeds budget");
        return (byte[])b.Clone();
    }
    private static bool AlphaNum(char c)=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
    private static bool Segment(string s)=>s.Length is >=1 and <=64 && AlphaNum(s[0]) && s.All(c=>AlphaNum(c)||c is '_' or '-');
    private static bool Subpath(string s)=>s.Length<=256 && s.Split('/').All(Segment);
    private static bool PathOk(string s)=>s.Length<=256 && s.EndsWith(".atto",StringComparison.Ordinal) && Subpath(s[..^5]);
    private static bool NamespaceOk(string s)=>s.Length<=519 && s.Split('.').Length<=8 && s.Split('.').All(ContextualImportPath.Valid);
    private static string Quoted(string s,string code) {
        if(s.Length<3||s[0]!='"'||s[^1]!='"') throw Fail(code,"expected quoted ASCII string");
        string v=s[1..^1];
        if(v.Any(c=>c<32||c>126||c=='"'||c=='\\')) throw Fail(code,"unsupported string");
        return v;
    }
    private static Dictionary<string,Dictionary<string,string>> Parse(string text,string[] allowed,string code) {
        if(text.Any(c=>c>126||c<32&&c!='\r'&&c!='\n'&&c!='\t')||text.Replace("\r\n","").Contains('\r'))
            throw Fail(code,"invalid metadata bytes");
        var output=new Dictionary<string,Dictionary<string,string>>(StringComparer.Ordinal);
        Dictionary<string,string>? table=null;
        foreach(string raw in text.Split('\n')) {
            string line=raw.Trim();
            if(line.Length==0||line.StartsWith('#')) continue;
            if(line.StartsWith('[')) {
                if(!allowed.Contains(line,StringComparer.Ordinal)||output.ContainsKey(line)) throw Fail(code,"unknown or duplicate table");
                table=new(StringComparer.Ordinal); output.Add(line,table); continue;
            }
            int at=line.IndexOf('=');
            if(table is null||at<1||table.Count>=MaxFiles) throw Fail(code,"invalid or excessive key");
            string key=line[..at].Trim(), val=Quoted(line[(at+1)..].Trim(),code);
            if(key.StartsWith('"')) key=Quoted(key,code);
            else if(key.Length==0||!key.All(c=>AlphaNum(c)||c is '_' or '-')) throw Fail(code,"dotted/slash keys must be quoted");
            if(!table.TryAdd(key,val)) throw Fail(code,"duplicate key");
        }
        return output;
    }
    private static Dictionary<string,string> Table(Dictionary<string,Dictionary<string,string>> m,string key)
        => m.TryGetValue(key,out var t)?t:new(StringComparer.Ordinal);
    public static FrozenImportSnapshot Verify(string expectedName,string expectedVersion,string expectedLock,
        byte[] manifestInput,byte[] lockInput,IReadOnlyDictionary<string,byte[]> inputs) {
        if(!Digest(expectedLock)) throw Fail("E_LOCK","invalid trusted lock digest");
        byte[] manifest=Bounded(manifestInput,MaxManifest,"E_MANIFEST"), lockBytes=Bounded(lockInput,MaxLock,"E_LOCK");
        if(Sha(lockBytes)!=expectedLock) throw Fail("E_LOCK_HASH","lock digest mismatch");
        var tables=Parse(Text(lockBytes,"E_LOCK"),new[]{"[lock]","[files]","[dependencies]"},"E_LOCK");
        var header=Table(tables,"[lock]"); var pins=Table(tables,"[files]");
        if(!(tables.Keys.SequenceEqual(new[]{"[lock]","[files]"})||tables.Keys.SequenceEqual(new[]{"[lock]","[files]","[dependencies]"}))||header.Count!=1||
            !header.TryGetValue("manifest_sha256",out string? manifestHash)||!Digest(manifestHash)||pins.Count==0)
            throw Fail("E_LOCK","invalid lock inventory");
        if(Sha(manifest)!=manifestHash) throw Fail("E_LOCK_HASH","manifest digest mismatch");
        if(inputs.Count!=pins.Count||inputs.Count>MaxFiles) throw Fail("E_LOCK_HASH","source inventory mismatch");
        var src=new Dictionary<string,string>(StringComparer.Ordinal);
        // Match exact case even if caller supplied a case-insensitive dictionary.
        var exact=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        foreach(var e in inputs) if(!exact.TryAdd(e.Key,e.Value)) throw Fail("E_LOCK_HASH","duplicate source");
        foreach(var e in pins) {
            if(!PathOk(e.Key)||!Digest(e.Value)) throw Fail("E_LOCK","invalid source pin");
            if(!exact.TryGetValue(e.Key,out var raw)) throw Fail("E_LOCK_HASH","missing pinned source");
            byte[] data=Bounded(raw,MaxSource,"E_SOURCE_LIMIT");
            if(Sha(data)!=e.Value) throw Fail("E_LOCK_HASH","source digest mismatch");
            src.Add(e.Key,Text(data,"E_LOCK"));
        }
        var m=Parse(Text(manifest,"E_MANIFEST"),new[]{"[package]","[namespaces]","[exports]","[dependencies]"},"E_MANIFEST");
        var pkg=Table(m,"[package]"); var ns=Table(m,"[namespaces]"); var ex=Table(m,"[exports]");
        if(pkg.Count!=2||!pkg.TryGetValue("name",out var name)||!name.StartsWith('@')||
            name[1..].Split('/').Length!=3||!name[1..].Split('/').All(Segment)||
            !pkg.TryGetValue("version",out var version)||version.Length is <1 or >64||
            !version.All(c=>AlphaNum(c)||c is '.' or '+' or '-')||ns.Count==0) throw Fail("E_MANIFEST","invalid package metadata");
        if(name!=expectedName||version!=expectedVersion) throw Fail("E_LOCK_HASH","package identity mismatch");
        var providers=new Dictionary<string,string>(StringComparer.Ordinal); var used=new HashSet<string>(StringComparer.Ordinal);
        foreach(var e in ns) {
            string provider=e.Value.EndsWith('/')?e.Value+"namespace.atto":e.Value;
            if(!NamespaceOk(e.Key)||!PathOk(provider)||!used.Add(provider)||!src.ContainsKey(provider))
                throw Fail("E_MANIFEST","invalid, duplicate or unpinned provider");
            for(int dot=e.Key.IndexOf('.');dot>=0;dot=e.Key.IndexOf('.',dot+1))
                if(!ns.ContainsKey(e.Key[..dot])) throw Fail("E_MANIFEST","undeclared namespace parent");
            providers.Add(e.Key,provider);
        }
        foreach(var e in ex) if(!Subpath(e.Key)||!providers.ContainsKey(e.Value)) throw Fail("E_MANIFEST","dangling subpath");
        if(!used.SetEquals(src.Keys)) throw Fail("E_UNSUPPORTED","additional dependency sources not admitted");
        var declared=Table(m,"[dependencies]"); var locked=Table(tables,"[dependencies]");
        if(declared.Count>16||!new HashSet<string>(declared.Keys,StringComparer.Ordinal).SetEquals(locked.Keys))
            throw Fail("E_LOCK","dependency inventory mismatch");
        var deps=new Dictionary<string,Dependency>(StringComparer.Ordinal);
        foreach(var e in declared) {
            if(!e.Key.StartsWith('@')||e.Key[1..].Split('/').Length!=3||!e.Key[1..].Split('/').All(Segment)||
                e.Value.Length is <1 or >64||!e.Value.All(c=>AlphaNum(c)||c is '.' or '+' or '-')||!Digest(locked[e.Key]))
                throw Fail("E_LOCK","invalid dependency identity, version or pin");
            deps.Add(e.Key,new Dependency(e.Value,locked[e.Key]));
        }
        return new FrozenImportSnapshot(name,version,expectedLock,providers,ex,src,deps);
    }
    public string NamespaceForSubpath(string path) {
        if(!Subpath(path)||!exports.TryGetValue(path,out var ns)) throw Fail("E_UNBOUND","undeclared subpath");
        return ns;
    }
    public string ProviderSource(string ns) {
        if(!namespaces.TryGetValue(ns,out var provider)) throw Fail("E_UNBOUND","undeclared namespace");
        return sources[provider];
    }
    // Explicit root selector or pinned public subpath; no physical fallback.
    public string SelectNamespace(string source,string selector) {
        if(!NamespaceOk(selector)) throw Fail("E_IMPORT_PATH","invalid namespace selector");
        if(source==Name) {
            if(namespaces.ContainsKey(selector)) return selector;
            int dot=selector.LastIndexOf('.');
            if(dot>=0) {
                string leaf=selector[(dot+1)..];
                var matches=namespaces.Keys.Where(k=>k==leaf||k.EndsWith("."+leaf,StringComparison.Ordinal)).OrderBy(k=>k,StringComparer.Ordinal).ToArray();
                if(matches.Length>1) throw Fail("E_AMBIGUOUS","ambiguous namespace selector");
                if(matches.Length==1) return matches[0];
            }
            throw Fail("E_UNBOUND","namespace not declared");
        }
        if(source is null||!source.StartsWith(Name+"/",StringComparison.Ordinal)) throw Fail("E_IMPORT_PATH","wrong pinned package");
        return NamespaceForSubpath(source[(Name.Length+1)..]);
    }
    // Source-derived graph integration lives in FrozenImportSnapshot.Graph.cs.
}
