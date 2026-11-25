using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

internal static class FrozenImportSnapshotSelfTest
{
    private const string Name="@org/project/package", Version="1.0.0", Path="src/network/namespace.atto";
    private const string Manifest="[package]\nname = \""+Name+"\"\nversion = \""+Version+"\"\n[namespaces]\nnetwork = \"src/network/\"\n[exports]\n\"network/http\" = \"network\"\n";
    private const string Source="use namespace network; pub fnc answer(): int { return 42; } fnc secret(): int { return 7; }";
    private static byte[] Bytes(string s)=>Encoding.UTF8.GetBytes(s);
    private static string Sha(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static byte[] Lock(string manifest,string source)=>Bytes("[lock]\nmanifest_sha256 = \""+Sha(Bytes(manifest))+"\"\n[files]\n\""+Path+"\" = \""+Sha(Bytes(source))+"\"\n");
    private static Dictionary<string,byte[]> Inputs(byte[] b)=>new(StringComparer.Ordinal){{Path,b}};
    private static void Deny(Action operation) {
        try {operation();} catch(FormatException){return;}
        throw new InvalidOperationException("invalid snapshot admitted");
    }
    [ModuleInitializer]
    internal static void Run() {
        byte[] source=Bytes(Source), l=Lock(Manifest,Source);
        string hash=Sha(l);
        var snap=FrozenImportSnapshot.Verify(Name,Version,hash,Bytes(Manifest),l,Inputs(source));
        Array.Fill(source,(byte)'x');
        if(snap.ProviderSource("network")!=Source||snap.NamespaceForSubpath("network/http")!="network")
            throw new InvalidOperationException("frozen input identity lost");
        Deny(()=>snap.NamespaceForSubpath("src/network"));
        Deny(()=>FrozenImportSnapshot.Verify(Name,Version,new string('0',64),Bytes(Manifest),l,Inputs(Bytes(Source))));
        Deny(()=>FrozenImportSnapshot.Verify(Name,Version,hash,Bytes(Manifest+"\n"),l,Inputs(Bytes(Source))));
        Deny(()=>FrozenImportSnapshot.Verify(Name,Version,hash,Bytes(Manifest),l,Inputs(Bytes(Source+" "))));
        Deny(()=>FrozenImportSnapshot.Verify(Name,Version,hash,Bytes(Manifest),l,new Dictionary<string,byte[]>()));
        Deny(()=>FrozenImportSnapshot.Verify(Name,"2.0.0",hash,Bytes(Manifest),l,Inputs(Bytes(Source))));
        Deny(()=>FrozenImportSnapshot.Verify(Name,Version,hash,Bytes(Manifest),l,new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase){{Path.ToUpperInvariant(),Bytes(Source)}}));
        foreach(string invalid in new[]{
            Manifest.Replace("src/network/","../network/"),
            Manifest.Replace("\"network/http\" = \"network\"","\"network/http\" = \"missing\""),
            Manifest.Replace("[exports]","other = \"src/network/\"\n[exports]"),
            Manifest+"[exports]\n",Manifest.Replace("\n","\r")}) {
            byte[] badLock=Lock(invalid,Source);
            Deny(()=>FrozenImportSnapshot.Verify(Name,Version,Sha(badLock),Bytes(invalid),badLock,Inputs(Bytes(Source))));
        }
        Console.WriteLine("Frozen registry snapshot verification: metadata only; no guest claim");
    }
}
