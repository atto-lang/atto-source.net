using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static Ores.Net.Compiler.StaticImportGraph;
using Ores.Net.Compiler;

internal static class StaticImportGraphSelfTest
{
    private static Origin Id(Domain domain,string name,char digest) => new(domain,name,new string(digest,64));
    private static View Make(Origin id,bool hasGet)
    {
        var entries=new List<Export> {
            new("net",Kind.Namespace,true),new("net.http",Kind.Namespace,true),
            new("net.http.secret",Kind.Fnc,false),new("hidden",Kind.Namespace,false),
            new("hidden.open",Kind.Fnc,true),new("scalar",Kind.Const,true),
            new("scalar.child",Kind.Fnc,true) };
        if(hasGet) entries.Add(new("net.http.get",Kind.Fnc,true));
        return new View(id,entries);
    }
    private static void Check(bool ok,string detail)
    {
        if(!ok) throw new InvalidOperationException(detail);
    }
    private static void Deny(string code,Action operation)
    {
        try { operation(); }
        catch(FormatException e) {
            Check(e.Message.StartsWith(code+":",StringComparison.Ordinal),e.Message); return;
        }
        throw new InvalidOperationException("expected "+code);
    }
    [ModuleInitializer]
    internal static void Run()
    {
        var a=Make(Id(Domain.Package,"@a/b/net",'a'),true);
        var b=Make(Id(Domain.Package,"@x/y/net",'b'),false);
        foreach(bool reverse in new[]{false,true}) {
            var scope=new StaticImportGraph();
            scope.BindRoot(reverse?b:a,"p",Tier.Deliberate);
            scope.BindRoot(reverse?a:b,"p",Tier.Deliberate);
            Deny("E_AMBIGUOUS",()=>scope.Resolve("p.net.http.get"));
            scope.BindRoot(b,"p",Tier.Explicit);
            Deny("E_UNBOUND",()=>scope.Resolve("p.net.http.get"));
            Deny("E_DUPLICATE",()=>scope.BindRoot(a,"p",Tier.Explicit));
        }
        var ext=Make(Id(Domain.Package,"core.num",'a'),true);
        var g=new StaticImportGraph();
        var get=new Selection("net.http.get","fetch",Kind.Fnc,true);
        Deny("E_IMPORT_PATH",()=>g.BindGroup(Id(Domain.Builtin,"core.num",'a'),ext,new[]{get}));
        foreach(var bad in new[]{new Selection("net.http.secret","bad",Kind.Fnc,true),
            new Selection("hidden.open","bad",Kind.Fnc,true)}) {
            Deny("E_PRIVATE",()=>g.BindGroup(ext.Origin,ext,new[]{get,bad}));
            Deny("E_UNBOUND",()=>g.Resolve("fetch"));
        }
        Deny("E_IMPORT_KIND",()=>g.BindGroup(ext.Origin,ext,new[]{get,new Selection("scalar.child","bad",Kind.Fnc,true)}));
        Deny("E_IMPORT_KIND",()=>g.BindGroup(ext.Origin,ext,new[]{get,new Selection("net.http","bad",Kind.Class,true)}));
        Deny("E_DUPLICATE",()=>g.BindGroup(ext.Origin,ext,new[]{get,get}));
        Deny("E_UNBOUND",()=>g.Resolve("fetch"));
        g.BindGroup(ext.Origin,ext,new[]{get});
        Check(ReferenceEquals(ext,g.Resolve("fetch").View),"origin lost");
        var aliases=new StaticImportGraph();
        aliases.BindRoot(a,"p",Tier.Deliberate);
        aliases.BindAliases(new Alias[]{new("first","second"),new("second","p.net.http")});
        Check(aliases.Resolve("first.get").Path=="net.http.get","forward alias");
        try {
            aliases.BindAliases(new Alias[]{new("c","a"),new("a","b.child"),new("b","c")});
            throw new InvalidOperationException("cycle admitted");
        } catch(FormatException e) { Check(e.Message=="E_IMPORT_CYCLE: a -> b -> c -> a",e.Message); }
        Deny("E_UNBOUND",()=>aliases.Resolve("a"));
        Deny("E_UNBOUND",()=>aliases.BindAliases(new Alias[]{new("good","p.net"),new("zz","missing")}));
        Deny("E_UNBOUND",()=>aliases.Resolve("good"));
        Deny("E_IMPORT_KIND",()=>aliases.BindAliases(new Alias[]{new("bad","p.scalar")}));
        Deny("E_DUPLICATE",()=>aliases.BindAliases(new Alias[]{new("x","p"),new("x","p")}));
        var budget=new StaticImportGraph();
        budget.BindRoot(a,"p",Tier.Deliberate);
        Deny("E_AMBIGUOUS",()=>budget.BindRoot(Make(a.Origin,false),"q",Tier.Deliberate));
        var chain=new List<Alias>();
        for(int i=0;i<255;i++) chain.Add(new("a"+i,i==254?"p":"a"+(i+1)));
        budget.BindAliases(chain);
        Check(budget.Resolve("a0.net.http.get").Path=="net.http.get","long alias chain");
        Deny("E_SOURCE_LIMIT",()=>budget.BindAliases(new Alias[]{new("extra","p")}));
        var oversized=new Alias[257]; Array.Fill(oversized,new Alias("x","p"));
        Deny("E_SOURCE_LIMIT",()=>PrecheckAliases(oversized));
        Console.WriteLine("Static import graph: four resolver suites passed; no guest claim");
    }
}
