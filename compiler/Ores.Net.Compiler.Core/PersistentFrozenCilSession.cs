using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ores.Net.Compiler;

/// Experimental Linux trusted-host CIL persistence. The host protects the
/// directory and all ancestors, excludes untrusted filesystem writers and
/// supplies current external ledger pins and immutable compiler/toolchain
/// digests. Descriptor-relative Linux x64 storage uses trusted procfs. All
/// cooperating builds use the same exclusive lock. No native binary caching,
/// durable external rollback provider, hostile same-UID protection, or arbitrary artifact signing API.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PersistentFrozenCilSession
{
    private readonly string root,scope,compiler,toolchain;
    private static readonly UTF8Encoding Utf8=new(false,true);
    private const int Bound=CompilerArtifactReceipt.Header+CompilerArtifactReceipt.MaxArtifact;
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hash(string value)=>Hash(Utf8.GetBytes(value));
    private readonly LinuxCacheDirectory.Identity identity;
    public PersistentFrozenCilSession(string directory,string scope,string compilerSha256,string toolchainSha256){
        if(!OperatingSystem.IsLinux()||System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture!=System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Linux x64 trusted-host profile only");
        if(directory.Split('/').Contains(".."))throw new IOException("E_CACHE_PATH: traversal");
        root=Path.GetFullPath(directory);
        using var dir=LinuxCacheDirectory.Open(root);identity=dir.RootIdentity;
        this.scope=scope;compiler=compilerSha256;toolchain=toolchainSha256;
    }
    public FrozenCilSession.Build Compile(string consumer,IReadOnlyList<FrozenImportSnapshot> packages,
        byte[] ledger,byte[] currentPin,byte[] master,bool cold=false){
        if(consumer is null||consumer.Length>65536||packages is null||packages.Count is <1 or >16)
            throw new FormatException("E_SOURCE_LIMIT: frozen scalar guest budget exceeded");
        var current=packages.ToArray();FrozenDependencyGraph.Verify(current);
        var deps=current.Select(p=>new CompilerCacheKey.Dependency(p.Name+"@"+p.Version,p.LockDigest)).ToArray();
        var key=CompilerCacheKey.Encode(new("cil-library",scope,"source-unit","main.atto",compiler,toolchain,
            Hash("linux-x64"),Hash("frozen-scalar-cil-v1"),Hash(consumer),Hash("verified-complete-frozen-graph"),null,deps));
        var secret=CompilerCacheLedger.Admit(scope,ledger,currentPin,master);
        try{
            using var dir=LinuxCacheDirectory.Open(root);
            if(dir.RootIdentity!=identity)throw new IOException("E_CACHE_PATH: root changed");
            dir.Lock();
            var path="slot-"+key[0];
            if(!cold&&dir.Read(path,Bound) is {} envelope){
                var actual=Convert.ToHexString(envelope.AsSpan(8,32)).ToLowerInvariant();
                if(actual[0]!=key[0])throw new FormatException("E_CACHE_RECEIPT: wrong slot");
                var bytes=CompilerArtifactReceipt.Verify(actual,envelope,secret);
                if(actual==key)return new(bytes,true);
            }
            var fresh=new FrozenCilSession().Compile(consumer,current,true);
            dir.Publish(path,CompilerArtifactReceipt.Seal(key,fresh.Cil,secret),Bound);
            return fresh;
        }finally{CryptographicOperations.ZeroMemory(secret);}
    }
}
