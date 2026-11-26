using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

internal static class FrozenImportGraphSelfTest
{
    private const string Name="@org/project/package", Path="src/network.atto";
    private const string Manifest="[package]\nname = \""+Name+"\"\nversion = \"1.0.0\"\n[namespaces]\nnetwork = \""+Path+"\"\n[exports]\n\"network/http\" = \"network\"\n";
    private static byte[] Bytes(string s)=>Encoding.UTF8.GetBytes(s);
    private static string Sha(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static FrozenImportSnapshot Snapshot(string source)
    {
        byte[] b=Bytes(source), m=Bytes(Manifest);
        byte[] l=Bytes("[lock]\nmanifest_sha256 = \""+Sha(m)+"\"\n[files]\n\""+Path+"\" = \""+Sha(b)+"\"\n");
        return FrozenImportSnapshot.Verify(Name,"1.0.0",Sha(l),m,l,new Dictionary<string,byte[]>{{Path,b}});
    }
    private static void Deny(string code,Action operation)
    {
        try {operation();} catch(FormatException e) {
            if(!e.Message.Contains(code,StringComparison.Ordinal)) throw;
            return;
        }
        throw new InvalidOperationException("expected "+code);
    }
    internal static void Run()
    {
        var snap=Snapshot("use namespace network; fnc secret(): int { return 7; } pub fnc answer(): int { return 42; }");
        var view=snap.FunctionView();
        var graph=new StaticImportGraph();
        snap.BindNamespace(graph,Name+"/network/http","net","web",true);
        graph.BindAliases(new[]{new StaticImportGraph.Alias("api","web")});
        if(!ReferenceEquals(view,snap.FunctionView()) || graph.Resolve("api.answer").Path!="network.answer")
            throw new InvalidOperationException("source-derived namespace binding changed");
        Deny("E_PRIVATE",()=>graph.Resolve("api.secret"));
        Deny("E_UNBOUND",()=>graph.Resolve("api.missing"));
        Deny("E_MANIFEST",()=>Snapshot("use namespace other; pub fnc answer():int{return 42;}").FunctionView());
        Deny("E_UNSUPPORTED",()=>Snapshot("use namespace network; import fnc a from './other.atto'; pub fnc answer():int{return 42;}").FunctionView());
        Deny("RETURN_EXPLICIT_REQUIRED",()=>Snapshot("use namespace network; pub fnc answer():int{}").FunctionView());
        Deny("E_UNSUPPORTED",()=>Snapshot("use namespace network; routine answer():int{return 42;}").FunctionView());
        Console.WriteLine("Frozen typed provider exports: checked parser and graph, NOT CIL or NativeAOT guest execution");
    }
}
