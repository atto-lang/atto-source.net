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
        return CompileCore(consumer,packages,ledger,currentPin,master,cold,false);
    }

    /// Opt-in, dependency-free frozen provider graph only. All snapshots
    /// must be newly approved by the trusted host. Full current CFG lowering
    /// and verification precede lookup. Only CIL emission is skipped on hit.
    /// External host coordinates authority rotation as in the default API.
    public FrozenCilSession.Build CompileBoundInput(string consumer,string expectedConsumerSha256,
        IReadOnlyList<FrozenImportSnapshot> packages,byte[] ledger,byte[] currentPin,byte[] master,bool cold=false){
        if(consumer is null||consumer.Length>65536||Hash(consumer)!=expectedConsumerSha256)
            throw new FormatException("E_CACHE_INPUT: independently pinned consumer required");
        return CompileCore(consumer,packages,ledger,currentPin,master,cold,true);
    }
    private FrozenCilSession.Build CompileCore(string consumer,IReadOnlyList<FrozenImportSnapshot> packages,
        byte[] ledger,byte[] currentPin,byte[] master,bool cold,bool boundInput){
        if(consumer is null||consumer.Length>65536||packages is null||packages.Count is <1 or >16)
            throw new FormatException("E_SOURCE_LIMIT: frozen scalar guest budget exceeded");
        var current=packages.ToArray();var graph=FrozenDependencyGraph.Verify(current);
        var secret=CompilerCacheLedger.Admit(scope,ledger,currentPin,master);
        try{
            VerifiedCfgModule? module=null;
            string bindings=Hash("verified-complete-frozen-graph");
            if(boundInput){
                // Cross-package imports require a stronger, complete typed
                // witness. Do not infer independence from a partial ABI.
                if(current.Any(x=>x.Dependencies.Count!=0))
                    throw new FormatException("E_CACHE_PROFILE: bound input requires dependency-free providers");
                module=OresStructuredParser.CompileFrozenScalar(consumer,current);
                // For this closed profile the compiler builds independent
                // provider programs and retains only verified reachable methods.
                // Key the exact full source of every reached namespace, the
                // ordered retained method names, and the complete manifest
                // domain of ALL supplied packages. No arbitrary caller witness.
                var helpers=module.Helpers.Select(h=>h.Name).ToArray();
                var covered=new HashSet<string>(StringComparer.Ordinal);
                using var stream=new MemoryStream();
                using(var writer=new BinaryWriter(stream,Utf8,true)){
                    writer.Write("atto.net.closed-bound-input.v1");
                    writer.Write(helpers.Length);
                    foreach(string helper in helpers)writer.Write(helper);
                    foreach(var package in current.OrderBy(x=>x.Name,StringComparer.Ordinal)){
                        foreach(string ns in package.NamespaceNames){
                            string prefix="frozen_provider_"+graph.ProviderIdentity(package.Name,ns)+"_";
                            var reached=helpers.Where(h=>h.StartsWith(prefix,StringComparison.Ordinal)).ToArray();
                            if(reached.Length==0)continue;
                            foreach(string helper in reached)
                                if(!covered.Add(helper))throw new FormatException("E_CACHE_PROFILE: ambiguous retained method owner");
                            writer.Write(package.Name);writer.Write(ns);writer.Write(package.ProviderSource(ns));
                        }
                    }
                }
                if(covered.Count!=helpers.Length)throw new FormatException("E_CACHE_PROFILE: incomplete retained method witness");
                if(stream.Length>16L*1024*1024)throw new FormatException("E_SOURCE_LIMIT: bound input witness budget");
                bindings=Hash(stream.ToArray());
            }
            var deps=current.Select(p=>new CompilerCacheKey.Dependency(p.Name+"@"+p.Version,boundInput?p.ManifestDigest:p.LockDigest)).ToArray();
            var key=CompilerCacheKey.Encode(new(boundInput?"cil-library-bound-input-v2":"cil-library",scope,"source-unit","main.atto",compiler,toolchain,
                Hash("linux-x64"),Hash(boundInput?"frozen-closed-cil-bound-input-v1":"frozen-scalar-cil-v1"),Hash(consumer),bindings,null,deps));
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
            var fresh=module is null?new FrozenCilSession().Compile(consumer,current,true):new FrozenCilSession.Build(CilEmitter.BuildLibrary(module),false);
            if(fresh.Cil.Length>CompilerArtifactReceipt.MaxArtifact)throw new FormatException("E_CACHE_OUTPUT: CIL artifact exceeds budget");
            dir.Publish(path,CompilerArtifactReceipt.Seal(key,fresh.Cil,secret),Bound);
            return fresh;
        }finally{CryptographicOperations.ZeroMemory(secret);}
    }
}
