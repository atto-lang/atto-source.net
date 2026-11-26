using System;
using System.Collections.Generic;
using System.Linq;

namespace Ores.Net.Compiler;

public sealed partial class FrozenImportSnapshot
{
    private readonly object graphGate = new();
    private StaticImportGraph.View? checkedView;

    // No caller-supplied visibility or declaration inventory. The original
    // frozen source bytes are consumed by the actual typed structured parser.
    public StaticImportGraph.View FunctionView()
    {
        lock(graphGate)
        {
            if(checkedView is not null) return checkedView;
            var inventory = new List<StaticImportGraph.Export>();
            foreach(string ns in namespaces.Keys.OrderBy(x=>x,StringComparer.Ordinal))
            {
                var entries=OresStructuredParser.ParseFrozenExports(this,ns);
                if(entries.Count>256-inventory.Count)
                    throw Fail("E_SOURCE_LIMIT","export inventory exceeds budget");
                inventory.AddRange(entries);
            }
            var candidate=new StaticImportGraph.View(
                new StaticImportGraph.Origin(StaticImportGraph.Domain.Package,Name,LockDigest),inventory);
            checkedView=candidate;
            return candidate;
        }
    }

    public void BindNamespace(StaticImportGraph scope,string source,string selector,string binding,bool explicitAlias)
    {
        if(!explicitAlias && selector!=binding) throw Fail("E_IMPORT_PATH","rename requires explicit alias");
        string selected=SelectNamespace(source,selector);
        var view=FunctionView();
        scope.BindGroup(view.Origin,view,new[]{new StaticImportGraph.Selection(
            selected,binding,StaticImportGraph.Kind.Namespace,explicitAlias)});
    }
}
