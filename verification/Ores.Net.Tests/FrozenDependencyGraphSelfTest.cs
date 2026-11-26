using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

internal static class FrozenDependencyGraphSelfTest
{
    private const string A="@org/project/a",B="@org/project/b",C="@org/project/c",D="@org/project/d";
    private const string Path="src/network.atto",Source="use namespace network; pub fnc answer():int{return 42;}";
    private static byte[] Bytes(string s)=>Encoding.UTF8.GetBytes(s);
    private static string Sha(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static FrozenImportSnapshot Snapshot(string name,string version,Dictionary<string,string> declared,Dictionary<string,string> pins,string source=Source)
    {
        var m=new StringBuilder("[package]\nname = \""+name+"\"\nversion = \""+version+"\"\n[namespaces]\nnetwork = \""+Path+"\"\n[exports]\n\"api\" = \"network\"\n");
        if(declared.Count!=0){
            m.Append("[dependencies]\n");
            foreach(var e in declared.OrderBy(x=>x.Key,StringComparer.Ordinal)) m.Append("\"").Append(e.Key).Append("\" = \"").Append(e.Value).Append("\"\n");
        }
        var l=new StringBuilder("[lock]\nmanifest_sha256 = \""+Sha(Bytes(m.ToString()))+"\"\n[files]\n\""+Path+"\" = \""+Sha(Bytes(source))+"\"\n");
        if(pins.Count!=0){
            l.Append("[dependencies]\n");
            foreach(var e in pins.OrderBy(x=>x.Key,StringComparer.Ordinal)) l.Append("\"").Append(e.Key).Append("\" = \"").Append(e.Value).Append("\"\n");
        }
        byte[] locked=Bytes(l.ToString());
        return FrozenImportSnapshot.Verify(name,version,Sha(locked),Bytes(m.ToString()),locked,new Dictionary<string,byte[]>{{Path,Bytes(source)}});
    }
    private static FrozenImportSnapshot WithDeps(string name,params FrozenImportSnapshot[] dependencies)
        => Snapshot(name,"1.0.0",dependencies.ToDictionary(p=>p.Name,p=>p.Version,StringComparer.Ordinal),
            dependencies.ToDictionary(p=>p.Name,p=>p.LockDigest,StringComparer.Ordinal));
    private static void Deny(string code,Action operation)
    {
        try{operation();}catch(FormatException e){
            if(!e.Message.StartsWith(code+":",StringComparison.Ordinal))throw;
            return;
        }
        throw new InvalidOperationException("expected "+code);
    }
    internal static void EmitTransitiveGuest(string output)
    {
        var c=Snapshot(C,"1.0.0",new(),new(),"use namespace network; fnc secret():int{return 99;} pub fnc answer(int x):int{return x*2;}");
        var b=Snapshot(B,"1.0.0",new(){{C,c.Version}},new(){{C,c.LockDigest}},
            "use namespace network; import fnc answer as twice from pkg '"+C+"/api'; pub fnc answer(int x):int{return twice(x)+1;}");
        var a=Snapshot(A,"1.0.0",new(){{B,b.Version}},new(){{B,b.LockDigest}},
            "use namespace network; import fnc answer as next from pkg '"+B+"/api'; pub fnc answer(int x):int{return next(x)+1;}");
        string main="import pkg '"+A+"' as p; pub fnc main()->int{return p.network.answer(20);}";
        var module=OresStructuredParser.CompileFrozenScalar(main,new[]{a,b,c});
        CilEmitter.EmitLibrary(module,output);
        byte[] baseline=System.IO.File.ReadAllBytes(output);
        CilEmitter.EmitLibrary(OresStructuredParser.CompileFrozenScalar(main,new[]{c,b,a}),output);
        if(!System.IO.File.ReadAllBytes(output).AsSpan().SequenceEqual(baseline))
            throw new InvalidOperationException("dependency inventory order changed CIL");
        Deny("E_UNBOUND",()=>OresStructuredParser.CompileFrozenScalar(main,new[]{a,b}));
        var jump=Snapshot(A,"1.0.0",new(){{B,b.Version}},new(){{B,b.LockDigest}},
            "use namespace network; import fnc answer as next from pkg '"+C+"/api'; pub fnc answer(int x):int{return next(x);}");
        Deny("E_UNBOUND",()=>OresStructuredParser.CompileFrozenScalar(main,new[]{jump,b,c}));
        var hidden=Snapshot(B,"1.0.0",new(){{C,c.Version}},new(){{C,c.LockDigest}},
            "use namespace network; import fnc secret from pkg '"+C+"/api'; pub fnc answer(int x):int{return secret();}");
        Deny("E_PRIVATE",()=>OresStructuredParser.CompileFrozenScalar("import pkg '"+B+"' as p; pub fnc main()->int{return p.network.answer(20);}",new[]{hidden,c}));
        Deny("E_UNBOUND",()=>OresStructuredParser.CompileFrozenScalar("import pkg '"+B+"' as p; pub fnc main()->int{return p.network.twice(20);}",new[]{b,c}));
        Console.WriteLine("FROZEN_TRANSITIVE_CIL|three packages|permutation-identical|four denials");
    }
    internal static void EmitNamespaceGuest(string output)
    {
        var c=Snapshot(C,"1.0.0",new(),new(),"use namespace network; fnc secret():int{return 99;} pub fnc answer(int x):int{return x*2;}");
        FrozenImportSnapshot FromC(string source)=>Snapshot(B,"1.0.0",new(){{C,c.Version}},new(){{C,c.LockDigest}},source);
        var b=FromC("use namespace network; import namespace network as math from pkg '"+C+"/api'; pub fnc answer(int x):int{return math.answer(x)+1;}");
        var a=Snapshot(A,"1.0.0",new(){{B,b.Version}},new(){{B,b.LockDigest}},
            "use namespace network; import namespace network from pkg '"+B+"'; pub fnc answer(int x):int{return network.answer(x)+1;}");
        string main="import pkg '"+A+"' as p; pub fnc main()->int{return p.network.answer(20);}";
        var session=new OresStructuredParser.FrozenScalarSession();
        var cold=session.Compile(main,new[]{a,b,c});
        var warm=session.Compile(main,new[]{c,b,a});
        if(cold.ReparsedProviders.Count!=3||warm.ReusedProviders.Count!=3||warm.ReparsedProviders.Count!=0)
            throw new InvalidOperationException("namespace session counters");
        CilEmitter.EmitLibrary(cold.Module,output);
        byte[] baseline=System.IO.File.ReadAllBytes(output);
        CilEmitter.EmitLibrary(warm.Module,output);
        if(!System.IO.File.ReadAllBytes(output).AsSpan().SequenceEqual(baseline))
            throw new InvalidOperationException("namespace warm CIL drift");
        string fromB="import pkg '"+B+"' as p; pub fnc main()->int{return p.network.answer(20);}";
        string prefix="use namespace network; import namespace network as math from pkg '"+C+"/api'; ";
        Deny("E_PRIVATE",()=>OresStructuredParser.CompileFrozenScalar(fromB,new[]{FromC(prefix+"pub fnc answer(int x):int{return math.secret();}"),c}));
        Deny("E_UNBOUND",()=>OresStructuredParser.CompileFrozenScalar(fromB,new[]{FromC(prefix+"pub fnc answer(int x):int{return math.missing(x);}"),c}));
        Deny("E_UNBOUND",()=>OresStructuredParser.CompileFrozenScalar(fromB,new[]{FromC(prefix.Replace("/api","/src/network")+"pub fnc answer(int x):int{return x;}"),c}));
        Deny("E_DUPLICATE",()=>OresStructuredParser.CompileFrozenScalar(fromB,new[]{FromC(prefix+"pub fnc math():int{return 42;} pub fnc answer(int x):int{return x;}"),c}));
        Deny("E_IMPORT_KIND",()=>OresStructuredParser.CompileFrozenScalar(fromB,new[]{FromC(prefix+"pub fnc answer(int x):int{return math;}"),c}));
        Deny("E_UNBOUND",()=>OresStructuredParser.CompileFrozenScalar(fromB.Replace("network.answer(20)","network.math.answer(20)"),new[]{b,c}));
        var noAuthority=Snapshot(B,"1.0.0",new(),new(),prefix+"pub fnc answer(int x):int{return x;}");
        Deny("E_UNBOUND",()=>OresStructuredParser.CompileFrozenScalar(fromB,new[]{noAuthority,c}));
        Console.WriteLine("FROZEN_PROVIDER_NAMESPACE|three-packages|warm-identical|seven-denials");
    }
    internal static void EmitPersistentCilGuests(string output,string lane)
    {
        if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("Linux fixture only");
        var c=Snapshot(C,"1.0.0",new(),new(),"use namespace network; pub fnc answer(int x):int{return x*2"+(lane=="edit"?"+1":"")+";}");
        var d=WithDeps(D);string main="import pkg '"+C+"' as p; pub fnc main()->int{return p.network.answer(21);}";
        string root=System.IO.Path.Combine(output,"cache");
        System.IO.Directory.CreateDirectory(output);
        System.IO.Directory.CreateDirectory(root,System.IO.UnixFileMode.UserRead|System.IO.UnixFileMode.UserWrite|System.IO.UnixFileMode.UserExecute);
        var master=Enumerable.Repeat((byte)7,32).ToArray();
        var record=CompilerCacheLedger.Record("tenant/a",2,master);var pin=SHA256.HashData(record);
        // Fixture build identity from the actual loaded compiler assembly;
        // a production host must attest its full toolchain closure separately.
        var compiler=Sha(System.IO.File.ReadAllBytes(typeof(FrozenCilSession).Assembly.Location));
        var session=new PersistentFrozenCilSession(root,"tenant/a",compiler,Sha(Bytes("fixture-net10-compiler")));
        var build=session.Compile(main,new[]{d,c},record,pin,master,lane=="cold");
        if(build.Reused!=(lane=="warm"))throw new InvalidOperationException("wrong persistent cache outcome");
        if(lane!="warm"&&!build.Cil.AsSpan().SequenceEqual(CilEmitter.BuildLibrary(OresStructuredParser.CompileFrozenScalar(main,new[]{c,d}))))
            throw new InvalidOperationException("persistent CIL differs from clean");
        if(lane=="warm"&&!build.Cil.AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(System.IO.Path.Combine(output,"cold.dll"))))
            throw new InvalidOperationException("restart output differs");
        Deny("E_CACHE_LEDGER",()=>session.Compile(main,new[]{c,d},record,new byte[32],master));
        if(lane=="edit"){
            Deny("E_UNBOUND",()=>session.Compile(main.Replace("answer(21)","missing(21)"),new[]{c,d},record,pin,master,true));
            if(!session.Compile(main,new[]{c,d},record,pin,master).Reused)throw new InvalidOperationException("rollback lost cache");
            var slots=System.IO.Directory.GetFiles(root,"slot-*");
            foreach(var file in slots){System.IO.File.Move(file,file+".saved");System.IO.File.CreateSymbolicLink(file,file+".saved");}
            try{Deny("E_CACHE_PATH",()=>session.Compile(main,new[]{c,d},record,pin,master));}
            finally{foreach(var file in slots){System.IO.File.Delete(file);System.IO.File.Move(file+".saved",file);}}
            string alias=root+"-alias";System.IO.Directory.CreateSymbolicLink(alias,root);
            try{Deny("E_CACHE_PATH",()=>new PersistentFrozenCilSession(alias,"tenant/a",compiler,Sha(Bytes("fixture-net10-compiler"))));}
            finally{System.IO.Directory.Delete(alias);}
            string moved=root+"-moved";System.IO.Directory.Move(root,moved);
            System.IO.Directory.CreateDirectory(root,System.IO.UnixFileMode.UserRead|System.IO.UnixFileMode.UserWrite|System.IO.UnixFileMode.UserExecute);
            try{Deny("E_CACHE_PATH",()=>session.Compile(main,new[]{c,d},record,pin,master));}
            finally{System.IO.Directory.Delete(root);System.IO.Directory.Move(moved,root);}
            Console.WriteLine("NET_DESCRIPTOR|symlink|root-swap|ancestor-denied");
            foreach(var file in System.IO.Directory.GetFiles(root,"slot-*")){
                var bytes=System.IO.File.ReadAllBytes(file);bytes[80]^=1;System.IO.File.WriteAllBytes(file,bytes);
            }
            Deny("E_CACHE_RECEIPT",()=>session.Compile(main,new[]{c,d},record,pin,master));
        }
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(output,lane+".dll"),build.Cil);
        Console.WriteLine("PERSISTENT_NET_CIL|"+lane+"|reused="+build.Reused+"|stale-pin-denied");
    }

    internal static void EmitCilCacheGuests(string output)
    {
        var c=Snapshot(C,"1.0.0",new(),new(),"use namespace network; pub fnc answer(int x):int{return x*2;}");
        var d=WithDeps(D);
        string main="import pkg '"+C+"' as p; pub fnc main()->int{return p.network.answer(21);}";
        var session=new FrozenCilSession();
        var cold=session.Compile(main,new[]{c,d},true);
        var clean=CilEmitter.BuildLibrary(OresStructuredParser.CompileFrozenScalar(main,new[]{c,d}));
        if(cold.Reused||!cold.Cil.AsSpan().SequenceEqual(clean))throw new InvalidOperationException("cold CIL differs");
        var warm=session.Compile(main,new[]{d,c});
        if(!warm.Reused||!warm.Cil.AsSpan().SequenceEqual(clean))throw new InvalidOperationException("warm CIL differs");
        System.IO.Directory.CreateDirectory(output);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(output,"cold.dll"),cold.Cil);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(output,"warm.dll"),warm.Cil);
        warm.Cil[0]^=1;
        if(!session.Compile(main,new[]{c,d}).Cil.AsSpan().SequenceEqual(clean))
            throw new InvalidOperationException("caller mutated cached bytes");
        Deny("E_AMBIGUOUS",()=>session.Compile(main,new[]{c,c}));
        Deny("E_UNBOUND",()=>session.Compile(main.Replace("answer(21)","missing(21)"),new[]{c,d}));
        if(!session.Compile(main,new[]{d,c}).Reused)throw new InvalidOperationException("failed build invalidated memo");
        var c2=Snapshot(C,"1.0.0",new(),new(),"use namespace network; pub fnc answer(int x):int{return x*2+1;}");
        var edit=session.Compile(main,new[]{c2,d});
        if(edit.Reused||!edit.Cil.AsSpan().SequenceEqual(CilEmitter.BuildLibrary(OresStructuredParser.CompileFrozenScalar(main,new[]{c2,d})))||
            edit.Cil.AsSpan().SequenceEqual(clean))throw new InvalidOperationException("edited CIL differs");
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(output,"edit.dll"),edit.Cil);
        Console.WriteLine("INCREMENTAL_NET_CIL|cold-warm-byte-identical|repinned-edit|defensive-copy|rollback");
    }

    internal static void EmitSessionGuests(string output)
    {
        var c=Snapshot(C,"1.0.0",new(),new(),"use namespace network; pub fnc answer(int x):int{return x*2;}");
        var b=Snapshot(B,"1.0.0",new(){{C,c.Version}},new(){{C,c.LockDigest}},
            "use namespace network; import fnc answer as twice from pkg '"+C+"/api'; pub fnc answer(int x):int{return twice(x)+1;}");
        var a=Snapshot(A,"1.0.0",new(){{B,b.Version}},new(){{B,b.LockDigest}},
            "use namespace network; import fnc answer as next from pkg '"+B+"/api'; pub fnc answer(int x):int{return next(x)+1;}");
        var d=WithDeps(D);
        var inventory=new[]{a,b,c,d};
        string main="import pkg '"+A+"' as p; pub fnc main()->int{return p.network.answer(20);}";
        var session=new OresStructuredParser.FrozenScalarSession();
        static void Counts(OresStructuredParser.FrozenScalarBuild build,int parsed,int reused)
        {
            if(build.ReparsedProviders.Count!=parsed||build.ReusedProviders.Count!=reused||
               build.ReparsedProviders.Intersect(build.ReusedProviders,StringComparer.Ordinal).Any())
                throw new InvalidOperationException("unexpected provider parser reuse counters");
        }
        var cold=session.Compile(main,inventory);Counts(cold,4,0);
        var warm=session.Compile(main,new[]{d,c,b,a});Counts(warm,0,4);
        CilEmitter.EmitLibrary(cold.Module,output+".cold.dll");
        CilEmitter.EmitLibrary(warm.Module,output+".warm.dll");
        if(!System.IO.File.ReadAllBytes(output+".cold.dll").AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(output+".warm.dll")))
            throw new InvalidOperationException("warm CIL differs from cold CIL");
        var c2=Snapshot(C,"1.0.0",new(),new(),"use namespace network; pub fnc answer(int x):int{return x*2+1;}");
        var b2=Snapshot(B,"1.0.0",new(){{C,c2.Version}},new(){{C,c2.LockDigest}},b.ProviderSource("network"));
        var a2=Snapshot(A,"1.0.0",new(){{B,b2.Version}},new(){{B,b2.LockDigest}},a.ProviderSource("network"));
        var updated=new[]{a2,b2,c2,d};
        var changed=session.Compile(main,updated);Counts(changed,3,1);
        string dPrefix="frozen_provider_"+FrozenDependencyGraph.Verify(updated).ProviderIdentity(D,"network")+"_";
        if(!changed.ReusedProviders.SequenceEqual(new[]{dPrefix}))throw new InvalidOperationException("wrong provider reused");
        CilEmitter.EmitLibrary(changed.Module,output+".changed.dll");
        CilEmitter.EmitLibrary(OresStructuredParser.CompileFrozenScalar(main,updated),output+".fresh.dll");
        if(!System.IO.File.ReadAllBytes(output+".changed.dll").AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(output+".fresh.dll")))
            throw new InvalidOperationException("incremental changed CIL differs from fresh build");
        var extra=WithDeps("@org/project/0extra");
        var expanded=new[]{extra,d,c2,b2,a2};
        Deny("E_UNBOUND",()=>session.Compile(main.Replace("answer(20)","missing(20)"),expanded));
        Counts(session.Compile(main,expanded),1,4); // failed build did not publish extra
        Counts(session.Compile(main,updated),0,4);
        Counts(session.Compile(main,expanded),1,4); // removed entry was evicted
        Deny("E_LOCK_HASH",()=>session.Compile(main,new[]{a,b,c2,d}));
        Counts(session.Compile(main,updated),0,4);
        session.Clear();Counts(session.Compile(main,updated),4,0);
        Console.WriteLine("FROZEN_INCREMENTAL|cold=4/0|warm=0/4|changed=3/1|atomic-rejection|eviction|byte-identical");
    }
    internal static void Run()
    {
        var c=WithDeps(C);var b=WithDeps(B,c);var d=WithDeps(D,c);var a=WithDeps(A,b,d);
        foreach(var list in new[]{new[]{a,b,c,d},new[]{d,c,b,a}}){
            var graph=FrozenDependencyGraph.Verify(list);
            if(!graph.DependencyOrder.SequenceEqual(new[]{C,B,D,A})||!ReferenceEquals(b,graph.RequireDependency(A,B))||
                !ReferenceEquals(c,graph.RequireDependency(B,C)))throw new InvalidOperationException("dependency order or identity changed");
            Deny("E_UNBOUND",()=>graph.RequireDependency(A,C));
        }
        Deny("E_UNBOUND",()=>FrozenDependencyGraph.Verify(new[]{a,b,d}));
        Deny("E_AMBIGUOUS",()=>FrozenDependencyGraph.Verify(new[]{a,b,c,d,c}));
        var changed=Snapshot(C,"2.0.0",new(),new());
        Deny("E_LOCK_HASH",()=>FrozenDependencyGraph.Verify(new[]{a,b,changed,d}));
        var wrong=Snapshot(B,"1.0.0",new(){{C,"1.0.0"}},new(){{C,new string('0',64)}});
        Deny("E_LOCK_HASH",()=>FrozenDependencyGraph.Verify(new[]{wrong,c}));
        Deny("E_LOCK",()=>Snapshot(B,"1.0.0",new(){{C,"1.0.0"}},new()));
        Deny("E_LOCK",()=>Snapshot(B,"1.0.0",new(),new(){{C,c.LockDigest}}));
        Deny("E_LOCK",()=>Snapshot(B,"1.0.0",new(){{"../escape","1.0.0"}},new(){{"../escape",c.LockDigest}}));
        // Structurally cyclic malformed pins: no valid cryptographic cycle claimed.
        var ca=Snapshot(A,"1.0.0",new(){{B,"1.0.0"}},new(){{B,new string('0',64)}});
        var cb=Snapshot(B,"1.0.0",new(){{A,"1.0.0"}},new(){{A,new string('0',64)}});
        foreach(var list in new[]{new[]{ca,cb},new[]{cb,ca}}){
            try{_ = FrozenDependencyGraph.Verify(list);throw new InvalidOperationException("cycle admitted");}
            catch(FormatException e){if(e.Message!="E_IMPORT_CYCLE: "+A+" -> "+B+" -> "+A)throw;}
        }
        var self=Snapshot(A,"1.0.0",new(){{A,"1.0.0"}},new(){{A,new string('0',64)}});
        Deny("E_IMPORT_CYCLE",()=>FrozenDependencyGraph.Verify(new[]{self}));
        var root=WithDeps(A,b);
        string guest="import pkg '"+A+"' as p; pub fnc main()->int{return p.network.answer();}";
        Deny("E_UNBOUND",()=>OresStructuredParser.CompileFrozenScalar(guest,new[]{root,b}));
        _=OresStructuredParser.CompileFrozenScalar(guest,new[]{root,b,c});
        var independent=WithDeps(D);
        var chain=WithDeps(A,b);
        var old=FrozenDependencyGraph.Verify(new[]{chain,b,c,independent});
        var same=FrozenDependencyGraph.Verify(new[]{independent,c,b,chain});
        if(same.PlanAgainst(old).Rebuild.Count!=0||!same.PlanAgainst(old).Reusable.SequenceEqual(old.DependencyOrder)||
            !old.PlanAgainst(null).Rebuild.SequenceEqual(old.DependencyOrder))throw new InvalidOperationException("cold/warm plan");
        var changedSource=Snapshot(C,"1.0.0",new(),new(),Source.Replace("42","43"));
        var b2=WithDeps(B,changedSource);var a2=WithDeps(A,b2);
        foreach(var list in new[]{new[]{a2,b2,changedSource,independent},new[]{independent,changedSource,b2,a2}}) {
            var next=FrozenDependencyGraph.Verify(list);
            var plan=next.PlanAgainst(old);
            if(!plan.Rebuild.SequenceEqual(new[]{C,B,A})||!plan.Reusable.SequenceEqual(new[]{D})||plan.Removed.Count!=0)
                throw new InvalidOperationException("transitive selective invalidation");
            if(next.ProviderIdentity(C,"network")!=old.ProviderIdentity(C,"network"))throw new InvalidOperationException("content changed logical identity");
        }
        var without=FrozenDependencyGraph.Verify(new[]{independent});
        if(!without.PlanAgainst(old).Removed.SequenceEqual(new[]{A,B,C})||
            !old.PlanAgainst(without).Rebuild.SequenceEqual(new[]{C,B,A}))throw new InvalidOperationException("removal or addition plan");
        var rewire=FrozenDependencyGraph.Verify(new[]{WithDeps(A,c),c,independent}).PlanAgainst(old);
        if(!rewire.Rebuild.SequenceEqual(new[]{A})||!rewire.Reusable.SequenceEqual(new[]{C,D})||!rewire.Removed.SequenceEqual(new[]{B}))
            throw new InvalidOperationException("rewire plan");
        Deny("E_LOCK_HASH",()=>FrozenDependencyGraph.Verify(new[]{chain,b,changedSource,independent}));
        if(old.ProviderIdentity(A,"network")!="680d736917e8f15f615de63abf42fdd332543f48c88065637ffb62ae7f82e636"||
            old.ProviderIdentity(A,"network")!=same.ProviderIdentity(A,"network")||
            old.ProviderIdentity(A,"network")==old.ProviderIdentity(D,"network"))
            throw new InvalidOperationException("stable provider identity");
        Deny("E_UNBOUND",()=>without.ProviderIdentity(A,"network"));
        Deny("E_UNBOUND",()=>old.ProviderIdentity(A,"missing"));
        _=OresStructuredParser.CompileFrozenScalar(guest,new[]{root,b,c,independent});
        Console.WriteLine("Frozen dependency closure: deterministic diamond, edge authority, missing and wrong pins, malformed cycles, compiler admission");
    }
}
