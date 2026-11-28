using System.Globalization;

namespace Ores.Net.Compiler;

// Explicit bounded scalar nominal value lane. This is a checked AST-to-CFG
// compiler, not source evaluation or a CLR object/record representation.
// Existing production admission remains closed until the full ABI is proven.
public static partial class OresStructuredParser
{
    internal static StructuredProgram ParseAttachedStructScalar(string source)
    {
        ValidateSourceInput(source);
        return new AttachedScalarReader(source).Parse();
    }

    // Separate explicit class profile. No reference identity, aliasing,
    // heap allocation, methods, inheritance, mutable fields or escape.
    internal static StructuredProgram ParseScalarClassConstructor(string source)
    {
        ValidateSourceInput(source);
        return new AttachedScalarReader(source, scalarClass: true).Parse();
    }

    private sealed class AttachedScalarReader
    {
        private sealed record Field(string Name, string Type);
        private sealed record Leaf(string Path, IrValueType Type);
        private sealed record Constructor(List<Field> Parameters, List<(string Field, string Parameter)> Assignments);
        private sealed record Method(string Internal, IrValueType Type);
        private sealed record Mutation(List<Field> Parameters, List<(string Path,string Helper)> Writes);

        private sealed class Owner(string name)
        {
            public string Name { get; } = name;
            public List<Field> Fields { get; } = new();
            public List<Leaf> Leaves { get; } = new();
            public List<Constructor> Constructors { get; } = new();
            public Dictionary<string, Method> Methods { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, Mutation> Mutations { get; } = new(StringComparer.Ordinal);
        }
        private sealed record Value(Owner Owner, Dictionary<string, StructuredExpr> Slots, bool Mutable);
        private sealed record Argument(string Type, Dictionary<string, StructuredExpr> Slots);
        private readonly Lexer lexer;
        private readonly bool scalarClass;
        private Token current;
        private Token? buffered;
        private int tokens, depth, nextSlot;
        private readonly Dictionary<string, Owner> owners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StructuredFunction> functions = new(StringComparer.Ordinal);
        private readonly List<string> order = new();
        private readonly Dictionary<string, Value> values = new(StringComparer.Ordinal);
        private readonly List<StructuredStmt> body = new();

        public AttachedScalarReader(string source, bool scalarClass = false)
        {
            this.scalarClass = scalarClass;
            if (source.Length > 65536) throw new FormatException("E_SOURCE_LIMIT: attached scalar source exceeds 64 KiB");
            lexer = new Lexer(source, attachedStructTarget: true);
            current = lexer.Read();
        }
        private FormatException Error(string code, string why) => new($"{code}: {why} at offset {current.Offset}");
        private void Next()
        {
            if (++tokens > 16384) throw Error("E_SOURCE_LIMIT", "attached scalar token budget");
            current = buffered ?? lexer.Read();
            buffered = null;
        }
        private Token Peek() { buffered ??= lexer.Read(); return buffered.Value; }
        private bool Eat(string t) { if (current.Text != t) return false; Next(); return true; }
        private void Expect(string t) { if (!Eat(t)) throw Error("E_STRUCT_SYNTAX", "expected " + t); }
        private string Word()
        {
            string s = current.Text;
            if (scalarClass && s is ("class" or "actor" or "isoactor" or "as" or "end" or "final" or "extends" or "implements" or "rt" or "unsafe" or "suspends" or "await" or "spawn" or "if" or "else" or "while" or "for"))
                throw Error("E_CLASS_PROFILE", "reserved class profile word");
            if (!IsSimpleNominalWord(s) || s is "self" or "true" or "false" or "int" or "bool" or "void" or "let" or "mut" or "return" or "with" or "constructor" or "mtd" or "define" or "type" or "struct" or "fnc" or "pub" or "private" or "const" or "val" or "new" or "copy" or "take" or "move" or "borrow" or "share")
                throw Error("E_STRUCT_SYNTAX", "expected unreserved identifier");
            Next(); return s;
        }
        private string TypeName()
        {
            if (Eat("int")) return "int";
            if (Eat("bool")) return "bool";
            string name = Word();
            if (!owners.ContainsKey(name)) throw Error("E_TYPE", "unknown, cyclic or forward nominal type " + name);
            return name;
        }
        private static bool Scalar(string name) => name is "int" or "bool";
        private IrValueType ScalarType(string name) => name switch {
            "int" => IrValueType.I64, "bool" => IrValueType.Bool,
            _ => throw Error("E_TYPE", "scalar result required")
        };
        private List<Leaf> Leaves(string type) => Scalar(type)
            ? new() { new Leaf("", ScalarType(type)) } : owners[type].Leaves;
        private static string Path(string a, string b) => b.Length == 0 ? a : a + "." + b;
        private string Slot() => "$struct_slot_" + nextSlot++;
        private StructuredRead Snapshot(StructuredExpr expression)
        {
            if (body.Count >= 4096) throw Error("E_SOURCE_LIMIT", "expanded scalar slot budget");
            string slot = Slot();
            body.Add(new StructuredLet(expression.Offset, slot, expression.Type, expression, StructuredBindingKind.Val));
            return new StructuredRead(expression.Offset, slot, expression.Type);
        }
        private static void Require(bool ok, string code, string message)
        {
            if (!ok) throw new FormatException(code + ": " + message);
        }
        private List<Field> Formals()
        {
            var result = new List<Field>(); var names = new HashSet<string>(StringComparer.Ordinal);
            Expect("(");
            if (!Eat(")"))
            {
                do {
                    string type = TypeName(), name = Word();
                    Require(names.Add(name) && !owners.ContainsKey(name), "E_DUPLICATE", "parameter name");
                    result.Add(new(name, type));
                    Require(result.Sum(p => Leaves(p.Type).Count) <= 16, "E_SOURCE_LIMIT", "constructor scalar parameter budget");
                } while (Eat(","));
                Expect(")");
            }
            return result;
        }
        private void Member(Owner owner)
        {
            if (Eat("mut")) { MutableMember(owner); return; }
            if (Eat("constructor"))
            {
                var parameters = Formals();
                var signature = string.Join(",", parameters.Select(p => p.Type));
                Require(!owner.Constructors.Any(c => string.Join(",",c.Parameters.Select(p => p.Type)) == signature),"E_DUPLICATE","constructor signature");
                var assignments = new List<(string Field, string Parameter)>();
                var assigned = new HashSet<string>(StringComparer.Ordinal);
                Expect("{");
                while (!Eat("}"))
                {
                    Expect("self"); Expect("."); string field = Word(); Expect("=");
                    string parameter = Word(); Expect(";");
                    var f = owner.Fields.Find(f => f.Name == field);
                    var p = parameters.Find(p => p.Name == parameter);
                    Require(f is not null && p is not null, "E_STRUCT_INIT", "whole-field parameter initialization required");
                    Require(f!.Type == p!.Type, "E_TYPE", "constructor field nominal identity mismatch");
                    Require(assigned.Add(field), "E_STRUCT_INIT", "duplicate initialization");
                    assignments.Add((field, parameter));
                }
                Require(assigned.Count == owner.Fields.Count,"E_STRUCT_INIT","missing field initialization");
                owner.Constructors.Add(new(parameters,assignments));
            }
            else
            {
                // No mutable/private/static/suspending methods in this profile.
                Expect("mtd"); string name; string type;
                if (current.Text is "int" or "bool")
                {
                    type = TypeName();
                    if (!Eat("•") && !Eat("<-")) throw Error("E_STRUCT_SYNTAX","expected prefix return separator");
                    name = Word(); Expect("("); Expect(")");
                }
                else
                {
                    name = Word(); Expect("("); Expect(")");
                    if (!Eat("->") && !Eat(":")) throw Error("E_STRUCT_SYNTAX","expected postfix return separator");
                    type = TypeName();
                }
                Require(!owner.Methods.ContainsKey(name) && !owner.Mutations.ContainsKey(name),"E_DUPLICATE","method signature");
                Require(order.Count < 64,"E_SOURCE_LIMIT","method budget");
                string generated = "_atto_struct_method_" + order.Count;
                var parameters = new List<StructuredParameter>();
                var self = new Dictionary<string,StructuredExpr>(StringComparer.Ordinal);
                foreach(var leaf in owner.Leaves)
                {
                    string slot = Slot();
                    parameters.Add(new(slot,leaf.Type));
                    self.Add(leaf.Path,new StructuredRead(current.Offset,slot,leaf.Type));
                }
                Expect("{"); Expect("return");
                var result = Expression(self); Expect(";"); Expect("}");
                Require(result.Type == ScalarType(type),"E_TYPE","method result mismatch");
                functions.Add(generated,new StructuredFunction(generated,parameters,result.Type,result,new StructuredStmt[]{new StructuredReturn(result.Offset,result)}));
                order.Add(generated);
                owner.Methods.Add(name,new Method(generated,result.Type));
            }
            Require(owner.Constructors.Count + owner.Methods.Count + owner.Mutations.Count <= 64,"E_SOURCE_LIMIT","member budget");
        }
        // Each leaf write is a separately checked pure scalar helper. Calls
        // snapshot arguments once, and later writes observe earlier writes.
        // No mutable alias, pointer, aggregate escape or nested call is admitted.
        private void MutableMember(Owner owner)
        {
            Expect("mtd"); string name = Word(); var formals = Formals();
            Require(formals.All(p=>Scalar(p.Type)), "E_TYPE","scalar mutation parameters required");
            Require(owner.Leaves.Count + formals.Count <= 16,"E_SOURCE_LIMIT","mutation parameter slots");
            Require(!owner.Methods.ContainsKey(name) && !owner.Mutations.ContainsKey(name),"E_DUPLICATE","method signature");
            if (!Eat(":") && !Eat("->")) throw Error("E_STRUCT_SYNTAX","mutable method result separator");
            Expect("void"); Expect("{");
            var writes = new List<(string Path,string Helper)>();
            while (!Eat("}"))
            {
                if (Eat("return")) { Expect(";"); Expect("}"); break; }
                Expect("self"); Expect("."); string field = Word();
                while(Eat(".")) field=Path(field,Word());
                var leaf=owner.Leaves.Find(l=>l.Path==field);
                Require(leaf is not null,"E_TYPE","unknown mutable leaf");
                Expect("=");
                var ps=new List<StructuredParameter>();
                var env=new Dictionary<string,StructuredExpr>(StringComparer.Ordinal);
                foreach(var l in owner.Leaves) {
                    string slot=Slot(); ps.Add(new(slot,l.Type));
                    env.Add(l.Path,new StructuredRead(current.Offset,slot,l.Type));
                }
                foreach(var formal in formals) {
                    var type=ScalarType(formal.Type); string slot=Slot(); ps.Add(new(slot,type));
                    env.Add("$arg."+formal.Name,new StructuredRead(current.Offset,slot,type));
                }
                var expression=Expression(env); Expect(";");
                Require(expression.Type==leaf!.Type,"E_TYPE","mutable leaf type mismatch");
                Require(order.Count<64,"E_SOURCE_LIMIT","mutation helper budget");
                string helper="_atto_struct_method_"+order.Count;
                functions.Add(helper,new StructuredFunction(helper,ps,expression.Type,expression,new StructuredStmt[]{new StructuredReturn(expression.Offset,expression)}));
                order.Add(helper); writes.Add((field,helper));
            }
            Require(writes.Count>0,"E_STRUCT_SYNTAX","empty mutation outside profile");
            Require(owner.Constructors.Count + owner.Methods.Count + owner.Mutations.Count <64,"E_SOURCE_LIMIT","member budget");
            owner.Mutations.Add(name,new(formals,writes));
        }
        private void InvokeMutation()
        {
            string name=Word(); Expect("."); string member=Word();
            if(!values.TryGetValue(name,out var value))throw Error("E_TYPE","unknown receiver");
            if(!value.Owner.Mutations.TryGetValue(member,out var mutation))throw Error("E_TYPE","unknown mutable method");
            Require(value.Mutable,"E_BORROW_MUT_REQUIRED","immutable receiver");
            Expect("("); var args=new List<StructuredExpr>();
            if(!Eat(")")){
                do{
                    Require(args.Count<mutation.Parameters.Count,"E_TYPE","mutation arity");
                    var expression=Expression();
                    Require(expression.Type==ScalarType(mutation.Parameters[args.Count].Type),"E_TYPE","mutation argument type");
                    args.Add(Snapshot(expression));
                }while(Eat(","));
                Expect(")");
            }
            Require(args.Count==mutation.Parameters.Count,"E_TYPE","mutation arity");
            Expect(";");
            foreach(var write in mutation.Writes) {
                var operands=value.Owner.Leaves.Select(l=>value.Slots[l.Path]).Concat(args).ToArray();
                var result=new StructuredCall(current.Offset,write.Helper,operands,functions[write.Helper].ResultType);
                value.Slots[write.Path]=Snapshot(result);
            }
        }
        private void ClassDeclaration()
        {
            Expect("define"); Expect("class"); string name = Word(); Expect("as");
            Require(owners.Count == 0, "E_CLASS_PROFILE", "one closed scalar class only");
            var owner = new Owner(name);
            while (Eat("pub"))
            {
                Expect("final"); Expect("int"); string field = Word(); Expect(";");
                Require(!owner.Fields.Any(f => f.Name == field), "E_DUPLICATE", "field name");
                Require(owner.Fields.Count < 16, "E_SOURCE_LIMIT", "class field budget");
                owner.Fields.Add(new(field, "int"));
                owner.Leaves.Add(new(field, IrValueType.I64));
            }
            Require(owner.Fields.Count > 0, "E_CLASS_PROFILE", "public deferred final int fields required");
            Expect("constructor");
            var parameters = Formals();
            Require(parameters.All(p => p.Type == "int"), "E_CLASS_PROFILE", "int constructor parameters required");
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            var assignments = new List<(string Field, string Parameter)>();
            Expect("{");
            while (Eat("self"))
            {
                Expect("."); string field = Word(); Expect("="); string parameter = Word(); Expect(";");
                Require(owner.Fields.Any(f => f.Name == field) && parameters.Any(p => p.Name == parameter),
                    "E_CLASS_INIT", "direct parameter to deferred final field required");
                Require(assigned.Add(field), "E_CLASS_INIT", "duplicate initialization");
                assignments.Add((field, parameter));
            }
            if (Eat("return")) Expect(";");
            Require(current.Text == "}", "E_CLASS_PROFILE", "constructor permits only direct field initialization and optional return");
            Expect("}"); Expect("end");
            Require(assigned.Count == owner.Fields.Count, "E_CLASS_INIT", "missing field initialization");
            owner.Constructors.Add(new(parameters, assignments));
            owners.Add(name, owner);
        }
        private void Declaration()
        {
            if (scalarClass) { ClassDeclaration(); return; }
            string name;
            if (Eat("define")) { Expect("struct"); name=Word(); }
            else { Expect("type"); name=Word(); Expect("="); Expect("struct"); }
            Require(!owners.ContainsKey(name) && owners.Count<16,"E_DUPLICATE","duplicate or excessive nominal owner");
            var owner=new Owner(name);
            Expect("{");
            while(!Eat("}"))
            {
                string type=TypeName(), field=Word(); Expect(";");
                Require(!owner.Fields.Any(f=>f.Name==field),"E_DUPLICATE","field name");
                owner.Fields.Add(new(field,type));
                foreach(var leaf in Leaves(type)) owner.Leaves.Add(new(Path(field,leaf.Path),leaf.Type));
                Require(owner.Leaves.Count<=16,"E_SOURCE_LIMIT","flattened field budget");
            }
            Require(owner.Fields.Count>0,"E_TYPE","empty struct outside bounded profile");
            do {
                Expect("with");
                if(Eat("{")) {
                    Require(current.Text!="}","E_STRUCT_SYNTAX","empty attachment");
                    while(!Eat("}")) Member(owner);
                } else Member(owner);
            } while(current.Text=="with");
            Require(owner.Constructors.Count>0,"E_STRUCT_INIT","constructor required");
            owners.Add(name,owner);
        }
        private static int Prec(string op) => op switch {
            "||"=>1,"&&"=>2,"==" or "!=" or "!eq"=>3,"<" or ">" or "<=" or ">="=>4,
            "+" or "-"=>5,"*"=>6,_=>0
        };
        private StructuredExpr Expression(Dictionary<string,StructuredExpr>? self=null,int minimum=1)
        {
            if(++depth>64) throw Error("E_SOURCE_LIMIT","expression depth");
            try {
                var a=Atom(self);
                int chain=0;
                while(Prec(current.Text)>=minimum) {
                    if(++chain>32)throw Error("E_SOURCE_LIMIT","scalar expression chain");
                    string op=current.Text; int at=current.Offset, precedence=Prec(op); Next();
                    if (scalarClass && op is not ("+" or "-" or "*")) throw Error("E_CLASS_PROFILE", "only integer arithmetic in scalar class profile");
                    var b=Expression(self,precedence+1);
                    Require(a.Type==b.Type,"E_TYPE","mixed scalar operands");
                    bool ints=a.Type==IrValueType.I64;
                    StructuredOperator kind=op switch {
                        "+" when ints=>StructuredOperator.Add,"-" when ints=>StructuredOperator.Subtract,"*" when ints=>StructuredOperator.Multiply,
                        "=="=>ints?StructuredOperator.EqualI64:StructuredOperator.EqualBool,
                        "!=" or "!eq"=>ints?StructuredOperator.NotEqualI64:StructuredOperator.NotEqualBool,
                        "<" when ints=>StructuredOperator.LessThanI64,">" when ints=>StructuredOperator.GreaterThanI64,
                        "<=" when ints=>StructuredOperator.LessThanOrEqualI64,">=" when ints=>StructuredOperator.GreaterThanOrEqualI64,
                        "&&" when !ints=>StructuredOperator.LogicalAnd,"||" when !ints=>StructuredOperator.LogicalOr,
                        _=>throw Error("E_TYPE","invalid scalar operator")
                    };
                    a=new StructuredBinary(at,kind,a,b,op is "+" or "-" or "*"?IrValueType.I64:IrValueType.Bool);
                }
                return a;
            } finally { depth--; }
        }
        private StructuredExpr Atom(Dictionary<string,StructuredExpr>? self)
        {
            int at=current.Offset;
            if(Eat("(")){var e=Expression(self);Expect(")");return e;}
            if(Eat("-")){
                if(current.Text=="9223372036854775808"){Next();return new StructuredInt(at,long.MinValue);}
                var e=Expression(self,7);Require(e.Type==IrValueType.I64,"E_TYPE","integer negation");return new StructuredNegate(at,e);
            }
            if(Eat("true"))return new StructuredBool(at,true);
            if(Eat("false"))return new StructuredBool(at,false);
            if(long.TryParse(current.Text,NumberStyles.None,CultureInfo.InvariantCulture,out long n)){Next();return new StructuredInt(at,n);}
            string root;
            if(Eat("self")){root="self";Require(self is not null,"E_TYPE","self outside method");}
            else root=Word();
            if(self is not null && root!="self" && self.TryGetValue("$arg."+root,out var parameter))return parameter;
            if (scalarClass && current.Text != ".") throw Error("E_CLASS_PROFILE", "class identity, whole value and escape unavailable");
            Expect("."); string name=Word();
            if(self is null && values.TryGetValue(root,out var value)){
                if(Eat("(")){
                    Require(!scalarClass, "E_CLASS_PROFILE", "class methods unavailable in scalar constructor profile");
                    Expect(")");
                    if(!value.Owner.Methods.TryGetValue(name,out var method))throw Error("E_TYPE","unknown nominal method");
                    return new StructuredCall(at,method.Internal,value.Owner.Leaves.Select(l=>value.Slots[l.Path]).ToArray(),method.Type);
                }
                while(Eat("."))name=Path(name,Word());
                if(value.Slots.TryGetValue(name,out var read))return read;
            } else if(root=="self" && self is not null){
                while(Eat("."))name=Path(name,Word());
                if(self.TryGetValue(name,out var read))return read;
            }
            throw Error("E_TYPE","unknown scalar projection");
        }
        private void Binding()
        {
            Expect("let"); bool mutable=Eat("mut"); string first=Word(),name=first; Owner? annotated=null;
            Require(!scalarClass || !mutable, "E_CLASS_PROFILE", "mutable class receiver unavailable");
            if(owners.TryGetValue(first,out annotated)) name=Word();
            Require(!values.ContainsKey(name)&&!owners.ContainsKey(name),"E_DUPLICATE","local shadow");
            Expect("="); bool explicitCopy=Eat("copy!");
            if (scalarClass)
            {
                Require(annotated is not null && !explicitCopy, "E_CLASS_PROFILE", "explicit nominal local required");
                if (!Eat("new")) throw Error("E_CLASS_PROFILE", "class alias/copy unavailable; direct new required");
            }
            Owner owner; Dictionary<string,StructuredExpr> slots=new(StringComparer.Ordinal);
            // A nominal copy reads all source leaves before creating the new value.
            if(values.TryGetValue(current.Text,out var existing)){
                Require(!scalarClass, "E_CLASS_PROFILE", "class alias/copy unavailable");
                Next(); owner=existing.Owner;
                foreach(var leaf in owner.Leaves)slots.Add(leaf.Path,Snapshot(existing.Slots[leaf.Path]));
            } else {
                Require(!explicitCopy,"E_TYPE","copy requires a nominal local");
                string target=Word();
                if(!owners.TryGetValue(target,out var selected))throw Error("E_TYPE","unknown nominal constructor");
                owner=selected;
                Expect("("); var args=new List<Argument>();
                if(!Eat(")")){
                    do{
                        if(values.TryGetValue(current.Text,out var v) && Peek().Text != "."){
                            Require(!scalarClass, "E_CLASS_PROFILE", "class-valued constructor arguments unavailable");
                            Next();
                            // Only whole nominal constructor arguments are admitted.
                            var leaves=new Dictionary<string,StructuredExpr>(StringComparer.Ordinal);
                            foreach(var leaf in v.Owner.Leaves)leaves.Add(leaf.Path,Snapshot(v.Slots[leaf.Path]));
                            args.Add(new(v.Owner.Name,leaves));
                        }else{
                            var expr=Expression(); var snapshot=Snapshot(expr);
                            args.Add(new(expr.Type==IrValueType.I64?"int":"bool",new(StringComparer.Ordinal){{"",snapshot}}));
                        }
                        Require(args.Sum(a=>a.Slots.Count)<=16,"E_SOURCE_LIMIT","constructor argument slots");
                    }while(Eat(","));
                    Expect(")");
                }
                var candidates=owner.Constructors.Where(c=>c.Parameters.Select(p=>p.Type).SequenceEqual(args.Select(a=>a.Type))).ToArray();
                Require(candidates.Length==1,"E_TYPE","no unique exact nominal constructor");
                var ctor=candidates[0];
                foreach(var assignment in ctor.Assignments){
                    int index=ctor.Parameters.FindIndex(p=>p.Name==assignment.Parameter);
                    foreach(var leaf in args[index].Slots)slots.Add(Path(assignment.Field,leaf.Key),Snapshot(leaf.Value));
                }
            }
            Require(annotated is null || ReferenceEquals(annotated,owner),"E_TYPE","nominal binding mismatch");
            Expect(";"); values.Add(name,new(owner,slots,mutable));
        }
        public StructuredProgram Parse()
        {
            while(current.Text is "define" or "type")Declaration();
            Require(owners.Count>0,"E_STRUCT_SYNTAX","attached struct required");
            Eat("pub"); Expect("fnc"); Expect("main"); Expect("("); Expect(")");
            if(!Eat(":")&&!Eat("->"))throw Error("E_STRUCT_SYNTAX","main return separator");
            Expect("int"); Expect("{");
            while(current.Text!="return") {
                if(current.Text=="let") Binding();
                else if (scalarClass) throw Error("E_CLASS_PROFILE", "only immutable local construction and scalar return");
                else InvokeMutation();
            }
            Expect("return"); var result=Expression();
            Require(result.Type==IrValueType.I64,"E_TYPE","int main result required");
            Expect(";"); Expect("}"); Expect("");
            body.Add(new StructuredReturn(result.Offset,result));
            return new StructuredProgram("main",body,functions,order);
        }
    }
}

public static partial class OresStructuredCfgLowering
{
    /// Opt-in, fully checked scalar attached struct compilation. Does not
    /// enable unimplemented attached structs in the ordinary production path.
    public static VerifiedCfgModule LowerAttachedStructScalar(string source)
        => LowerModule(OresStructuredParser.ParseAttachedStructScalar(source));

    public static VerifiedCfgModule LowerScalarClassConstructor(string source)
        => LowerModule(OresStructuredParser.ParseScalarClassConstructor(source));
}
