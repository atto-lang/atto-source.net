using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Ores.Net.Compiler;

// Bounded metadata-only admission; no provider imports are lowered by this graph.
public sealed class FrozenDependencyGraph
{
    private readonly IReadOnlyDictionary<string,FrozenImportSnapshot> packages;
    public IReadOnlyList<string> DependencyOrder {get;}
    private FrozenDependencyGraph(SortedDictionary<string,FrozenImportSnapshot> packages,List<string> order)
    {
        this.packages=new ReadOnlyDictionary<string,FrozenImportSnapshot>(packages);
        DependencyOrder=order.AsReadOnly();
    }
    private static FormatException Fail(string code,string detail)=>new(code+": "+detail);
    public static FrozenDependencyGraph Verify(IReadOnlyList<FrozenImportSnapshot> supplied)
    {
        if(supplied is null||supplied.Count is <1 or >16)
            throw Fail("E_SOURCE_LIMIT","package inventory budget exceeded");
        var packages=new SortedDictionary<string,FrozenImportSnapshot>(StringComparer.Ordinal);
        foreach(var p in supplied) {
            if(p is null) throw Fail("E_LOCK","null snapshot");
            if(!packages.TryAdd(p.Name,p)) throw Fail("E_AMBIGUOUS","multiple snapshots for package "+p.Name);
        }
        var remaining=new SortedDictionary<string,SortedSet<string>>(StringComparer.Ordinal);
        int edges=0;
        foreach(var p in packages.Values) {
            var deps=new SortedSet<string>(p.Dependencies.Keys,StringComparer.Ordinal);
            edges+=deps.Count;
            if(edges>128) throw Fail("E_SOURCE_LIMIT","dependency edge budget exceeded");
            foreach(string name in deps)
                if(!packages.ContainsKey(name)) throw Fail("E_UNBOUND","missing pinned dependency "+p.Name+" -> "+name);
            remaining.Add(p.Name,deps);
        }
        var order=new List<string>();
        while(remaining.Count!=0) {
            string? next=null;
            foreach(var e in remaining)
                if(e.Value.Count==0) {next=e.Key;break;}
            if(next is null) {
                var path=new List<string>();
                var seen=new Dictionary<string,int>(StringComparer.Ordinal);
                string node=remaining.First().Key;
                while(!seen.ContainsKey(node)) {
                    seen.Add(node,path.Count);path.Add(node);
                    node=remaining[node].Min!;
                }
                var cycle=path.Skip(seen[node]).Append(node);
                throw Fail("E_IMPORT_CYCLE",string.Join(" -> ",cycle));
            }
            order.Add(next);remaining.Remove(next);
            foreach(var deps in remaining.Values) deps.Remove(next);
        }
        foreach(var p in packages.Values)
            foreach(var e in p.Dependencies.OrderBy(x=>x.Key,StringComparer.Ordinal)) {
                var target=packages[e.Key];
                if(target.Version!=e.Value.Version||target.LockDigest!=e.Value.LockDigest)
                    throw Fail("E_LOCK_HASH","dependency pin mismatch "+p.Name+" -> "+target.Name);
            }
        return new FrozenDependencyGraph(packages,order);
    }
    public FrozenImportSnapshot RequireDependency(string owner,string dependency)
    {
        if(!packages.TryGetValue(owner,out var p)||!p.Dependencies.ContainsKey(dependency))
            throw Fail("E_UNBOUND","undeclared direct dependency "+owner+" -> "+dependency);
        return packages[dependency];
    }
}
