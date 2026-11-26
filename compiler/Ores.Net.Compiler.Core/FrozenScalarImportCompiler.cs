using System;
using System.Collections.Generic;
using System.Linq;

namespace Ores.Net.Compiler;

public static partial class OresStructuredParser
{
    /// <summary>
    /// Closed frozen scalar consumer profile, not general source/CLI linking.
    /// A verified snapshot supplies the source-owned export identity and the
    /// original provider is parsed by the existing structured frontend.
    /// The ordinary CFG verifier and CIL emitter then own executable lowering.
    /// </summary>
    public static VerifiedCfgModule CompileFrozenScalar(string consumer, IReadOnlyList<FrozenImportSnapshot> packages)
        => new FrozenScalarSession().Compile(consumer,packages).Module;

    // Only private parser-produced typed providers are retained. No caller can
    // supply cached ASTs. Every build lowers and verifies the entire CFG anew.
    private sealed record CachedProvider(StructuredProgram Program,IReadOnlyList<StaticImportGraph.Export> Exports,string Version,string Source,bool ClosedPackage);
    public sealed record FrozenScalarBuild(VerifiedCfgModule Module,
        IReadOnlyList<string> ReparsedProviders,IReadOnlyList<string> ReusedProviders);

    public sealed class FrozenScalarSession
    {
        private readonly object gate=new();
        private FrozenDependencyGraph? previous;
        private Dictionary<(string Package,string Namespace),CachedProvider> cache=new();
        public FrozenScalarBuild Compile(string consumer,IReadOnlyList<FrozenImportSnapshot> packages)
        {
            lock(gate) {
                var result=CompileFrozenScalarCore(consumer,packages,previous,cache,out var nextGraph,out var next);
                // Publish only after full current-graph CFG verification succeeds.
                previous=nextGraph;cache=next;
                return result;
            }
        }
        public void Clear() {lock(gate){previous=null;cache=new();}}
    }

    private static FrozenScalarBuild CompileFrozenScalarCore(string consumer,IReadOnlyList<FrozenImportSnapshot> packages,
        FrozenDependencyGraph? previous,Dictionary<(string Package,string Namespace),CachedProvider> cache,
        out FrozenDependencyGraph nextGraph,out Dictionary<(string Package,string Namespace),CachedProvider> next)
    {
        if (consumer is null || consumer.Length > 65536 || packages is null ||
            packages.Count is <1 or >16)
            throw new FormatException("E_SOURCE_LIMIT: frozen scalar guest budget exceeded");
        ValidateSourceInput(consumer);
        var available = new SortedDictionary<string,FrozenImportSnapshot>(StringComparer.Ordinal);
        // Snapshot caller inventory before verification and linking.
        packages=packages.ToArray();
        foreach (var package in packages) {
            if(package is null) throw new FormatException("E_LOCK: null snapshot");
            if (!available.TryAdd(package.Name,package))
                throw new FormatException("E_AMBIGUOUS: multiple snapshots for package name");
        }

        var lexer = new Lexer(consumer, true);
        var reader = new FrozenConsumerReader(lexer);
        var imports = new List<(string Source,string? Selector,string? Binding,bool Renamed)>();
        var aliases = new List<StaticImportGraph.Alias>();
        while (reader.Take("import"))
        {
            if (imports.Count >=64) throw new FormatException("E_SOURCE_LIMIT: import budget exceeded");
            if(reader.Take("pkg"))
            {
                string packageName=reader.StaticString();
                string? alias=reader.Take("as")?reader.Path():null;
                reader.Expect(";");
                imports.Add((packageName,null,alias,alias is not null));
                continue;
            }
            reader.Expect("namespace");
            string selector=reader.Path(), binding=selector;
            bool renamed=reader.Take("as");
            if (renamed) binding=reader.Path();
            reader.Expect("from"); reader.Expect("pkg");
            string source=reader.StaticString();
            reader.Expect(";");
            imports.Add((source,selector,binding,renamed));
        }
        while (reader.Take("define"))
        {
            reader.Expect("alias");
            if (aliases.Count>=256) throw new FormatException("E_SOURCE_LIMIT: alias budget exceeded");
            string left=reader.Path(), name, reference;
            if(reader.Take("=")) {name=left;reference=reader.Path();}
            else {reader.Expect("as");reference=left;name=reader.Path();}
            reader.Expect(";");
            aliases.Add(new(name,reference));
        }
        // Diagnose unused cycles before any provider parsing or lookup.
        StaticImportGraph.PrecheckAliases(aliases);
        var closure=FrozenDependencyGraph.Verify(packages);
        var dirty=new HashSet<string>(closure.PlanAgainst(previous).Rebuild,StringComparer.Ordinal);
        var staged=new Dictionary<(string Package,string Namespace),CachedProvider>();
        var reparsed=new List<string>();
        var reused=new List<string>();
        var linkedViews=new Dictionary<string,StaticImportGraph.View>(StringComparer.Ordinal);
        var linkedExports=new Dictionary<(string Package,string Namespace,string Member),StructuredFunction>();
        var linkedPrefixes=new Dictionary<(string Package,string Namespace),string>();
        var methods=new Dictionary<string,StructuredFunction>(StringComparer.Ordinal);
        var order=new List<string>();
        foreach(string identity in closure.DependencyOrder) {
            var owner=available[identity];
            var inventory=new List<StaticImportGraph.Export>();
            foreach(string ns in owner.NamespaceNames) {
                if(linkedPrefixes.Count>=16) throw new FormatException("E_SOURCE_LIMIT: linked provider budget exceeded");
                string prefix="frozen_provider_"+closure.ProviderIdentity(identity,ns)+"_";
                var key=(identity,ns);
                CachedProvider cached;
                string providerSource=owner.ProviderSource(ns);
                // A changed package lock is normally a conservative rebuild.
                // For a dependency-free package, an exact unchanged namespace
                // source and version are sufficient: parser ownership is the
                // stable package/namespace prefix and no foreign signatures
                // can have changed. Dependent packages retain full invalidation.
                if(cache.TryGetValue(key,out var prior)&&(!dirty.Contains(identity)||
                    (prior.ClosedPackage&&owner.Dependencies.Count==0&&prior.Version==owner.Version&&
                     string.Equals(prior.Source,providerSource,StringComparison.Ordinal)))) {
                    cached=prior;
                    reused.Add(prefix);
                } else {
                    var ownExports=new List<StaticImportGraph.Export>();
                    var unit=ParseCore(providerSource,null,ns,ownExports,prefix,(source,member)=>{
                    string[] parts=source.Split('/');
                    if(parts.Length<4) throw new FormatException("E_IMPORT_PATH: provider function import requires explicit exported subpath");
                    string dependency=string.Join("/",parts.Take(3));
                    var target=closure.RequireDependency(identity,dependency);
                    string selected=target.NamespaceForSubpath(string.Join("/",parts.Skip(3)));
                    if(!linkedViews.TryGetValue(target.Name,out var view))
                        throw new FormatException("E_IMPORT_PATH: dependency not linked before owner");
                    var export=view.Lookup(selected+"."+member);
                    if(export.Kind!=StaticImportGraph.Kind.Fnc||
                        !linkedExports.TryGetValue((target.Name,selected,member),out var fn))
                        throw new FormatException("E_IMPORT_KIND: expected public dependency function");
                    return fn;
                    }, frozenNamespaceResolve:(source,selector)=>{
                        string[] parts=source.Split('/');
                        if(parts.Length<3) throw new FormatException("E_IMPORT_PATH: invalid provider namespace package");
                        string dependency=string.Join("/",parts.Take(3));
                        var target=closure.RequireDependency(identity,dependency);
                        string selected=target.SelectNamespace(source,selector);
                        if(!linkedViews.TryGetValue(target.Name,out var view))
                            throw new FormatException("E_IMPORT_PATH: dependency not linked before owner");
                        if(view.Lookup(selected).Kind!=StaticImportGraph.Kind.Namespace)
                            throw new FormatException("E_IMPORT_KIND: expected public dependency namespace");
                        return member=>{
                            var export=view.Lookup(selected+"."+member);
                            if(export.Kind!=StaticImportGraph.Kind.Fnc||
                                !linkedExports.TryGetValue((target.Name,selected,member),out var fn))
                                throw new FormatException("E_IMPORT_KIND: expected public dependency function");
                            return fn;
                        };
                    });
                    cached=new CachedProvider(unit.Program,ownExports.AsReadOnly(),owner.Version,providerSource,owner.Dependencies.Count==0);
                    reparsed.Add(prefix);
                }
                staged.Add(key,cached);
                inventory.AddRange(cached.Exports);
                if(inventory.Count>256) throw new FormatException("E_SOURCE_LIMIT: export inventory exceeded");
                var providerProgram=cached.Program;
                var functions=providerProgram.Functions??throw new FormatException("E_IMPORT_KIND: missing provider functions");
                if(functions.ContainsKey(prefix+"main")) throw new FormatException("E_UNSUPPORTED: provider main remains outside bounded profile");
                if(functions.Count>64-methods.Count) throw new FormatException("E_SOURCE_LIMIT: linked function budget exceeded");
                foreach(string method in providerProgram.FunctionOrder??throw new FormatException("E_IMPORT_KIND: missing provider order")) {
                    if(!methods.TryAdd(method,functions[method])) throw new FormatException("E_DUPLICATE: canonical provider method");
                    order.Add(method);
                }
                foreach(var entry in cached.Exports.Where(e=>e.Visible&&e.Kind==StaticImportGraph.Kind.Fnc&&e.Path.StartsWith(ns+".",StringComparison.Ordinal))) {
                    string member=entry.Path[(ns.Length+1)..];
                    if(member.Contains('.')||!functions.TryGetValue(prefix+member,out var fn)) continue;
                    linkedExports.Add((identity,ns,member),fn);
                }
                linkedPrefixes.Add((identity,ns),prefix);
            }
            linkedViews.Add(identity,new StaticImportGraph.View(new StaticImportGraph.Origin(StaticImportGraph.Domain.Package,owner.Name,owner.LockDigest),inventory));
        }
        var graph=new StaticImportGraph();
        foreach(var import in imports)
        {
            if(import.Selector is null)
            {
                if(!available.TryGetValue(import.Source,out var root))
                    throw new FormatException("E_UNBOUND: package root not in authenticated snapshot set");
                graph.BindPackage(linkedViews[root.Name],import.Binding);
                continue;
            }
            string[] parts=import.Source.Split('/');
            if(parts.Length<3) throw new FormatException("E_IMPORT_PATH: invalid package selector");
            if(!available.TryGetValue(string.Join("/",parts.Take(3)),out var package))
                throw new FormatException("E_UNBOUND: package not in authenticated snapshot set");
            if(!import.Renamed&&import.Selector!=import.Binding) throw new FormatException("E_IMPORT_PATH: rename requires explicit alias");
            var selected=package.SelectNamespace(import.Source,import.Selector);
            var view=linkedViews[package.Name];
            graph.BindGroup(view.Origin,view,new[]{new StaticImportGraph.Selection(selected,import.Binding!,StaticImportGraph.Kind.Namespace,import.Renamed)});
        }
        graph.BindAliases(aliases);

        int calls=0;
        reader.ResolveCall=(reference,arguments)=>{
            if(++calls>32) throw new FormatException("E_SOURCE_LIMIT: imported call budget exceeded");
            var target=graph.Resolve(reference);
            if(target.Kind!=StaticImportGraph.Kind.Fnc)
                throw new FormatException("E_IMPORT_KIND: expected public function");
            if(!available.TryGetValue(target.View.Origin.Identity,out var owner) ||
                !ReferenceEquals(linkedViews[owner.Name],target.View))
                throw new FormatException("E_IMPORT_PATH: selected view not owned by authenticated snapshot");
            int dot=target.Path.LastIndexOf('.');
            if(dot<=0) throw new FormatException("E_IMPORT_PATH: missing provider namespace");
            string ns=target.Path[..dot], member=target.Path[(dot+1)..];
            if(!linkedPrefixes.TryGetValue((owner.Name,ns),out var prefix))
                throw new FormatException("E_IMPORT_PATH: provider not linked");
            string canonical=prefix+member;
            if(!methods.TryGetValue(canonical,out var function)||function.Parameters.Count!=arguments.Count||
                function.Parameters.Any(p=>p.Type!=IrValueType.I64)||function.ResultType!=IrValueType.I64)
                throw new FormatException("E_IMPORT_KIND: selected function must accept exact signed-int arguments and return int");
            return new StructuredCall(0,canonical,arguments,IrValueType.I64);
        };
        reader.Expect("pub"); reader.Expect("fnc"); reader.Expect("main");
        reader.Expect("("); reader.Expect(")");
        if(!reader.Take("->")) reader.Expect(":");
        reader.Expect("int"); reader.Expect("{");
        var body=new List<StructuredStmt>();
        while(reader.Take("val")) {
            if(reader.Locals.Count>=16) throw new FormatException("E_SOURCE_LIMIT: local binding budget exceeded");
            reader.Expect("int");
            string local=reader.LocalName();
            if(reader.Locals.ContainsKey(local)) throw new FormatException("E_DUPLICATE: duplicate local binding");
            reader.Expect("=");
            var initial=reader.Expression(0);
            reader.Expect(";");
            string generated="frozen_local_"+reader.Locals.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            reader.Locals.Add(local,generated);
            body.Add(new StructuredLet(0,generated,IrValueType.I64,initial,StructuredBindingKind.Val));
        }
        reader.Expect("return");
        var result=reader.Expression(0);
        reader.Expect(";"); reader.Expect("}");
        if(!reader.AtEnd) throw new FormatException("E_UNSUPPORTED: trailing guest source");
        if(calls==0) throw new FormatException("E_UNSUPPORTED: frozen guest requires imported call");
        body.Add(new StructuredReturn(0,result));
        var program=new StructuredProgram("main",body.AsReadOnly(),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string,StructuredFunction>(methods),order.AsReadOnly());
        // Verify every admitted method first, including unreachable providers.
        // Frozen scalar modules have no dynamic dispatch, exports or init hooks.
        // Retain only the transitive closure of verified static call targets.
        var verified=OresStructuredCfgLowering.LowerModule(program);
        var byName=verified.Helpers.ToDictionary(m=>m.Name,StringComparer.Ordinal);
        var reached=new HashSet<string>(StringComparer.Ordinal);
        var pending=new Stack<VerifiedCfgFunction>();pending.Push(verified.Main);
        while(pending.Count!=0){
            var method=pending.Pop();
            foreach(var op in method.Blocks.SelectMany(b=>b.Instructions)){
                if(op.Opcode is not (CfgOpcode.Call or CfgOpcode.TrapCall))continue;
                if(op.Target is null||!byName.TryGetValue(op.Target,out var target))
                    throw new FormatException("E_UNBOUND: verified frozen call target missing");
                if(reached.Add(target.Name))pending.Push(target);
            }
        }
        verified=new VerifiedCfgModule(verified.Main,Array.AsReadOnly(verified.Helpers.Where(m=>reached.Contains(m.Name)).ToArray()));
        nextGraph=closure;next=staged;
        return new FrozenScalarBuild(verified,reparsed.AsReadOnly(),reused.AsReadOnly());
    }

    private sealed class FrozenConsumerReader
    {
        private readonly Lexer lexer;
        private Token current;
        private int expressionNodes;
        internal Func<string,IReadOnlyList<StructuredExpr>,StructuredExpr>? ResolveCall;
        internal Dictionary<string,string> Locals {get;}=new(StringComparer.Ordinal);
        internal FrozenConsumerReader(Lexer lexer) {this.lexer=lexer;current=lexer.Read();}
        internal bool AtEnd => current.Text.Length==0;
        private void Advance() {current=lexer.Read();}
        internal bool Take(string text)
        {
            if(current.Text!=text) return false;
            Advance();return true;
        }
        internal void Expect(string text)
        {
            if(!Take(text)) throw new FormatException("E_UNSUPPORTED: expected "+text+" at "+current.Offset);
        }
        private string Component()
        {
            string name=current.Text;
            if(!ContextualImportPath.Valid(name)) throw new FormatException("E_IMPORT_PATH: invalid qualified component");
            Advance();return name;
        }
        internal string Path()
        {
            string path=Component();int count=1;
            while(Take(".")) {
                if(++count>8) throw new FormatException("E_IMPORT_PATH: path too deep");
                path+="."+Component();
            }
            return path;
        }
        internal string LocalName()=>Component();
        private void ChargeExpression(int depth) {
            if(depth>16||++expressionNodes>128)
                throw new FormatException("E_SOURCE_LIMIT: scalar expression budget exceeded");
        }
        internal StructuredExpr Expression(int depth) {
            var value=Product(depth);
            while(current.Text is "+" or "-") {
                string op=current.Text; Advance(); ChargeExpression(depth);
                value=new StructuredBinary(0,op=="+"?StructuredOperator.Add:StructuredOperator.Subtract,
                    value,Product(depth),IrValueType.I64);
            }
            return value;
        }
        private StructuredExpr Product(int depth) {
            var value=Atom(depth);
            while(Take("*")) {
                ChargeExpression(depth);
                value=new StructuredBinary(0,StructuredOperator.Multiply,value,Atom(depth),IrValueType.I64);
            }
            return value;
        }
        private StructuredExpr Atom(int depth) {
            ChargeExpression(depth);
            if(Take("(")) {
                var value=Expression(depth+1); Expect(")"); return value;
            }
            if(ContextualImportPath.Valid(current.Text)) {
                string name=Component();
                if(current.Text==".") {
                    if(Locals.ContainsKey(name)) throw new FormatException("E_IMPORT_KIND: scalar local shadows qualified call root");
                    string reference=name;
                    int count=1;
                    while(Take(".")) {
                        if(++count>8) throw new FormatException("E_IMPORT_PATH: path too deep");
                        reference+="."+Component();
                    }
                    Expect("(");
                    var args=new List<StructuredExpr>();
                    if(!Take(")")) {
                        do {
                            if(args.Count>=16) throw new FormatException("E_SOURCE_LIMIT: call argument budget exceeded");
                            args.Add(Expression(depth+1));
                        } while(Take(","));
                        Expect(")");
                    }
                    return (ResolveCall ?? throw new FormatException("E_UNSUPPORTED: import call unavailable"))(reference,args.AsReadOnly());
                }
                if(!Locals.TryGetValue(name,out var generated))
                    throw new FormatException("E_UNBOUND: unbound scalar local");
                return new StructuredRead(0,generated,IrValueType.I64);
            }
            return new StructuredInt(0,Integer());
        }
        internal long Integer()
        {
            bool negative=Take("-");
            string raw=current.Text;
            if(raw.Length is <1 or >37) throw new FormatException("E_IMPORT_KIND: invalid decimal int argument");
            int digits=0;
            for(int i=0;i<raw.Length;i++) {
                if(raw[i] is >= '0' and <= '9') digits++;
                else if(raw[i]!='_'||i==0||i==raw.Length-1||
                    raw[i-1] is <'0' or >'9'||raw[i+1] is <'0' or >'9')
                    throw new FormatException("E_IMPORT_KIND: expected decimal int argument");
            }
            if(digits>19||!long.TryParse((negative?"-":"")+raw.Replace("_","",StringComparison.Ordinal),
                System.Globalization.NumberStyles.AllowLeadingSign,System.Globalization.CultureInfo.InvariantCulture,out long value))
                throw new FormatException("E_IMPORT_KIND: int argument outside signed i64 range");
            Advance();
            return value;
        }
        internal string StaticString()
        {
            string value=current.Text;
            if(value.Length<3 || value[0] is not ('"' or '\'') ||
               value[^1]!=value[0] || value.Contains('\\'))
                throw new FormatException("E_IMPORT_PATH: expected unescaped static package string");
            Advance();
            return value[1..^1];
        }
    }
}
