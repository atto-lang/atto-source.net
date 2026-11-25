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
