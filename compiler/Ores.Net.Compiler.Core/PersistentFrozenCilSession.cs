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
/// digests. Managed path APIs are NOT a hostile-filesystem sandbox. All
/// cooperating builds use the same exclusive lock. No native binary caching,
/// durable external rollback provider, or arbitrary artifact signing API.
public sealed class PersistentFrozenCilSession
{
    private readonly string root,scope,compiler,toolchain;
    private static readonly UTF8Encoding Utf8=new(false,true);
    private const int Bound=CompilerArtifactReceipt.Header+CompilerArtifactReceipt.MaxArtifact;
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hash(string value)=>Hash(Utf8.GetBytes(value));
    private static void OwnerOnly(string path){
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0 ||
           (File.GetUnixFileMode(path)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|
             UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)
            throw new IOException("E_CACHE_PATH: host-private path required");
    }
    public PersistentFrozenCilSession(string directory,string scope,string compilerSha256,string toolchainSha256){
        if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("Linux trusted-host profile only");
        root=Path.GetFullPath(directory);OwnerOnly(root);
        if(!Directory.Exists(root))throw new IOException("E_CACHE_PATH: directory required");
        this.scope=scope;compiler=compilerSha256;toolchain=toolchainSha256;
    }
    private string Slot(string key)=>Path.Combine(root,"slot-"+key[0]);
    private static byte[]? Read(string path){
        try{
            OwnerOnly(path);
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(input.Length<CompilerArtifactReceipt.Header||input.Length>Bound)throw new IOException("E_CACHE_READ: artifact size");
            byte[] result=new byte[(int)input.Length];input.ReadExactly(result);
            if(input.ReadByte()!=-1)throw new IOException("E_CACHE_READ: artifact grew");
            return result;
        }catch(FileNotFoundException){return null;}
    }
    private void Publish(string path,byte[] bytes){
        string pending=Path.Combine(root,".pending");
        File.Delete(pending);
        try{
            using(var output=new FileStream(pending,new FileStreamOptions{
                Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,
                UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite})){
                output.Write(bytes);output.Flush(true);
            }
            File.Move(pending,path,true);
            // Atomic rename on the trusted local filesystem; no directory fsync
            // available here. Crash durability is not claimed.
        }finally{File.Delete(pending);}
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
            OwnerOnly(root);
            using var guard=new FileStream(Path.Combine(root,".lock"),new FileStreamOptions{
                Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None,
                UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});
            var path=Slot(key);
            if(!cold&&Read(path) is {} envelope){
                var actual=Convert.ToHexString(envelope.AsSpan(8,32)).ToLowerInvariant();
                if(actual[0]!=key[0])throw new FormatException("E_CACHE_RECEIPT: wrong slot");
                var bytes=CompilerArtifactReceipt.Verify(actual,envelope,secret);
                if(actual==key)return new(bytes,true);
            }
            var fresh=new FrozenCilSession().Compile(consumer,current,true);
            Publish(path,CompilerArtifactReceipt.Seal(key,fresh.Cil,secret));
            return fresh;
        }finally{CryptographicOperations.ZeroMemory(secret);}
    }
}
