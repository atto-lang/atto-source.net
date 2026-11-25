using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

internal static class FrozenScalarImportSelfTest
{
    private const string Name="@org/project/package", Path="src/network.atto";
    private const string Manifest="[package]\nname = \""+Name+"\"\nversion = \"1.0.0\"\n[namespaces]\nnetwork = \""+Path+"\"\n[exports]\n\"network/http\" = \"network\"\n";
    private const string Provider="use namespace network; fnc secret(): int { return 40; } pub fnc answer(): int { return secret() + 2; }";
    private static byte[] Bytes(string s)=>Encoding.UTF8.GetBytes(s);
    private static string Sha(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static FrozenImportSnapshot Snapshot(string source)
    {
        byte[] b=Bytes(source), m=Bytes(Manifest);
        byte[] l=Bytes("[lock]\nmanifest_sha256 = \""+Sha(m)+"\"\n[files]\n\""+Path+"\" = \""+Sha(b)+"\"\n");
        // Test fixture constructs its own trusted pin; production must supply
        // the digest from an independent authenticated lock authority.
        return FrozenImportSnapshot.Verify(Name,"1.0.0",Sha(l),m,l,new Dictionary<string,byte[]>{{Path,b}});
    }
    internal static void Run(string output)
    {
        var snapshot=Snapshot(Provider);
        var packages=new[]{snapshot};
        string header="import namespace network from pkg '"+Name+"'; ";
        string main="pub fnc main() -> int { return web.answer(); }";
        string[] positives={
            header+"define alias web = network; "+main,
            header+"define alias network as web; "+main,
            header+"define alias web = next; define alias next = network; "+main,
            "import namespace net as web from pkg '"+Name+"/network/http'; "+main,
            "import pkg '"+Name+"'; define alias web = package.network; "+main,
            "import pkg '"+Name+"'; define alias web = project.package.network; "+main,
            "import pkg '"+Name+"'; define alias web = org.project.package.network; "+main,
            "import pkg '"+Name+"' as root; define alias root.network as web; "+main
        };
        byte[]? baseline=null;
        foreach(string source in positives)
        {
            var module=OresStructuredParser.CompileFrozenScalar(source,packages);
            CilEmitter.EmitLibrary(module,output);
            byte[] actual=File.ReadAllBytes(output);
            if(baseline is null) baseline=actual;
            else if(!actual.AsSpan().SequenceEqual(baseline))
                throw new InvalidOperationException("equivalent alias spelling changed emitted CIL");
        }
        foreach(var bad in new (string Source,string Code)[]{
            ("import pkg '"+Name+"' as root; pub fnc main()->int{return package.network.answer();}","E_UNBOUND"),
            ("import pkg '"+Name+"'; pub fnc main()->int{return package.answer();}","E_UNBOUND"),
            (header+"pub fnc main()->int{return network.secret();}","E_PRIVATE"),
            (header+"pub fnc main()->int{return network.missing();}","E_UNBOUND"),
            (header+"define alias a = b; define alias b = a; pub fnc main()->int{return network.answer();}","E_IMPORT_CYCLE"),
            ("import namespace network from pkg '@unknown/project/package'; define alias a = a; pub fnc main()->int{return network.answer();}","E_IMPORT_CYCLE"),
            (header+"define alias a = network.answer; pub fnc main()->int{return a.answer();}","E_IMPORT_KIND"),
            (header+"pub fnc main()->int{return network.answer();} fnc extra():int{return 1;}","E_UNSUPPORTED"),
            ("import namespace network from pkg '@wrong/project/package'; pub fnc main()->int{return network.answer();}","E_UNBOUND"),
            ("import namespace network from pkg '"+Name+"/src/network'; pub fnc main()->int{return network.answer();}","E_UNBOUND")
        })
        {
            try {
                _=OresStructuredParser.CompileFrozenScalar(bad.Source,packages);
                throw new InvalidOperationException("unexpectedly compiled: "+bad.Source);
            } catch(FormatException e) {
                if(!e.Message.Contains(bad.Code,StringComparison.Ordinal)) throw;
            }
        }
        // Check a changed public signature does not silently coerce to the
        // closed no-argument scalar call ABI.
        foreach(string provider in new[]{
            "use namespace network; pub fnc answer(int x):int{return x;}",
            "use namespace network; pub fnc answer():bool{return true;}",
            "use namespace network; fnc main():int{return 7;} pub fnc answer():int{return main();}"
        }) {
            try {
                _=OresStructuredParser.CompileFrozenScalar(positives[0],new[]{Snapshot(provider)});
                throw new InvalidOperationException("invalid selected ABI admitted");
            } catch(FormatException e) {
                if(!e.Message.Contains("E_IMPORT_KIND",StringComparison.Ordinal) &&
                   !e.Message.Contains("E_UNSUPPORTED",StringComparison.Ordinal)) throw;
            }
        }
        if(baseline is null || !File.ReadAllBytes(output).AsSpan().SequenceEqual(baseline))
            throw new InvalidOperationException("negative tests altered generated artifact");
        var argsSnapshot=Snapshot("use namespace network; pub fnc answer(int x, int y):int{return x-y;}");
        string argsHeader=header+"define alias web = network; pub fnc main()->int{return web.answer(";
        string argsOutput=output+".args.dll";
        CilEmitter.EmitLibrary(OresStructuredParser.CompileFrozenScalar(argsHeader+"40,-2);}",new[]{argsSnapshot}),argsOutput);
        byte[] argBaseline=File.ReadAllBytes(argsOutput);
        CilEmitter.EmitLibrary(OresStructuredParser.CompileFrozenScalar(argsHeader+"4_0,-2);}",new[]{argsSnapshot}),argsOutput);
        if(!File.ReadAllBytes(argsOutput).AsSpan().SequenceEqual(argBaseline))
            throw new InvalidOperationException("decimal separator changed emitted CIL");
        foreach(string arguments in new[]{"40","40,-2,1","40,true","40,2.0","40,1+1",
            "9223372036854775808,0","-9223372036854775809,0","40,-2,",string.Join(",",System.Linq.Enumerable.Repeat("1",17))})
        {
            try {
                _=OresStructuredParser.CompileFrozenScalar(argsHeader+arguments+");}",new[]{argsSnapshot});
                throw new InvalidOperationException("invalid argument list admitted: "+arguments);
            } catch(FormatException) {}
        }
        var boolProvider=Snapshot("use namespace network; pub fnc answer(bool x,int y):int{return y;}");
        try {
            _=OresStructuredParser.CompileFrozenScalar(argsHeader+"40,-2);}",new[]{boolProvider});
            throw new InvalidOperationException("bool formal coerced from int");
        } catch(FormatException e) {
            if(!e.Message.Contains("E_IMPORT_KIND",StringComparison.Ordinal)) throw;
        }
        Console.WriteLine("Frozen scalar: ten CIL candidates in two equivalent groups, twenty-three rejections; NativeAOT separate");
    }
}
