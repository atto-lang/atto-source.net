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
    {
        if (consumer is null || consumer.Length > 65536 || packages is null ||
            packages.Count is <1 or >16)
            throw new FormatException("E_SOURCE_LIMIT: frozen scalar guest budget exceeded");
        ValidateSourceInput(consumer);
        var available = new SortedDictionary<string,FrozenImportSnapshot>(StringComparer.Ordinal);
        foreach (var package in packages)
            if (!available.TryAdd(package.Name,package))
                throw new FormatException("E_AMBIGUOUS: multiple snapshots for package name");

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
        string call=reader.Path();
        reader.Expect("(");
        var arguments=new List<StructuredExpr>();
        if(!reader.Take(")")) {
            do {
                if(arguments.Count>=16) throw new FormatException("E_SOURCE_LIMIT: call argument budget exceeded");
                arguments.Add(reader.Expression(0));
            } while(reader.Take(","));
            reader.Expect(")");
        }
        reader.Expect(";"); reader.Expect("}");
        if(!reader.AtEnd) throw new FormatException("E_UNSUPPORTED: trailing guest source");
        if(!call.Contains('.')) throw new FormatException("E_UNSUPPORTED: qualified static call required");

        if(reader.Locals.ContainsKey(call[..call.IndexOf('.')]))
            throw new FormatException("E_IMPORT_KIND: scalar local shadows qualified call root");
        var graph=new StaticImportGraph();
        foreach(var import in imports)
        {
            if(import.Selector is null)
            {
                if(!available.TryGetValue(import.Source,out var root))
                    throw new FormatException("E_UNBOUND: package root not in authenticated snapshot set");
                graph.BindPackage(root.FunctionView(),import.Binding);
                continue;
            }
            string[] parts=import.Source.Split('/');
            if(parts.Length<3) throw new FormatException("E_IMPORT_PATH: invalid package selector");
            if(!available.TryGetValue(string.Join("/",parts.Take(3)),out var package))
                throw new FormatException("E_UNBOUND: package not in authenticated snapshot set");
            package.BindNamespace(graph,import.Source,import.Selector,import.Binding!,import.Renamed);
        }
        graph.BindAliases(aliases);
        var target=graph.Resolve(call);
        if(target.Kind!=StaticImportGraph.Kind.Fnc)
            throw new FormatException("E_IMPORT_KIND: expected public function");
        if(!available.TryGetValue(target.View.Origin.Identity,out var owner) ||
            !ReferenceEquals(owner.FunctionView(),target.View))
            throw new FormatException("E_IMPORT_PATH: selected view not owned by authenticated snapshot");
        int dot=target.Path.LastIndexOf('.');
        if(dot<=0) throw new FormatException("E_IMPORT_PATH: missing provider namespace");
        var provider=ParseFrozenProvider(owner,target.Path[..dot]);
        string member=target.Path[(dot+1)..];
        var functions=provider.Functions ?? throw new FormatException("E_IMPORT_KIND: missing provider functions");
        if(!functions.TryGetValue(member,out var function) || function.Parameters.Count!=arguments.Count ||
           function.Parameters.Any(p=>p.Type!=IrValueType.I64) || function.ResultType!=IrValueType.I64)
            throw new FormatException("E_IMPORT_KIND: selected function must accept the exact signed-int argument list and return int");
        // Reserve the entry method name; do not confuse an imported main with
        // the synthesized consumer entry. Other helper names remain unchanged.
        if(functions.ContainsKey("main"))
            throw new FormatException("E_UNSUPPORTED: provider main requires canonical method names");
        var result=new StructuredCall(0,member,arguments.AsReadOnly(),IrValueType.I64);
        body.Add(new StructuredReturn(0,result));
        var program=new StructuredProgram("main",body.AsReadOnly(),functions,provider.FunctionOrder);
        return OresStructuredCfgLowering.LowerModule(program);
    }

    private sealed class FrozenConsumerReader
    {
        private readonly Lexer lexer;
        private Token current;
        private int expressionNodes;
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
