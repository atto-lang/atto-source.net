using System.Globalization;

namespace Ores.Net.Compiler;

public static partial class OresParser
{
    /// <summary>Recognize the file-local refinement profile using the production source lexer.</summary>
    public static VerifiedCfgModule? TryLowerVerifiedRefinementModule(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576 || !HasVerifiedRefinementPrefix(source)) return null;
        var tokens = Tokenize(source);
        RejectUnsupportedInvertedClasses(tokens);
        var reader = new Reader(tokens, DotnetHostImportPolicy.DenyAll, source, null);
        var prelude = reader.ParseVerifiedPrelude();
        var program = reader.ParseVerifiedFunctions(prelude);
        ValidateVerifiedSourceTokens(tokens, prelude.Actors);
        return OresStructuredCfgLowering.LowerModule(program);
    }

    /// <summary>Opt-in strict signed-i64 source proof; no ordinary fallback.</summary>
    public static VerifiedCfgModule LowerVerifiedStrict(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576 || !HasVerifiedRefinementPrefix(source))
            throw new FormatException("E_VERIFY_UNSUPPORTED: strict refinement profile required");
        var tokens = Tokenize(source, strictBoolean: true);
        RejectUnsupportedInvertedClasses(tokens);
        var reader = new Reader(tokens, DotnetHostImportPolicy.DenyAll, source, null) { StrictI64 = true };
        var prelude = reader.ParseVerifiedPrelude();
        if (prelude.Actors.Count != 0 || prelude.Protocols.Count != 0)
            throw new FormatException("E_VERIFY_UNSUPPORTED: strict protocol effects not established");
        var program = reader.ParseVerifiedFunctions(prelude);
        ValidateVerifiedSourceTokens(tokens, prelude.Actors);
        return OresStructuredCfgLowering.LowerModule(program);
    }

    /// <summary>Try the verified protocol actor profile before the legacy actor emitter.</summary>
    public static string? TryEmitVerifiedProtocolActors(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576 || !HasVerifiedRefinementPrefix(source)) return null;
        var tokens = Tokenize(source);
        RejectUnsupportedInvertedClasses(tokens);
        var reader = new Reader(tokens, DotnetHostImportPolicy.DenyAll, source, null);
        var prelude = reader.ParseVerifiedPrelude();
        if (prelude.Actors.Count == 0)
            throw new FormatException("E_PROTOCOL_ACTOR: expected at least one verified protocol actor");
        if (!reader.AtEndOfVerifiedSource)
            _ = OresStructuredCfgLowering.LowerModule(reader.ParseVerifiedFunctions(prelude));
        ValidateVerifiedSourceTokens(tokens, prelude.Actors);
        return ProtocolActorSourceEmitter.Emit(prelude.Actors);
    }

    public static string? TryEmitVerifiedProtocolGuest(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576 || !HasVerifiedRefinementPrefix(source)) return null;
        var tokens = Tokenize(source);
        RejectUnsupportedInvertedClasses(tokens);
        var reader = new Reader(tokens, DotnetHostImportPolicy.DenyAll, source, null);
        var prelude = reader.ParseVerifiedPrelude();
        var guest = reader.TryParseProtocolGuest(prelude);
        if (guest is null) return null;
        ValidateVerifiedSourceTokens(tokens, prelude.Actors);
        return ProtocolActorSourceEmitter.Emit(prelude.Actors, guest);
    }

    private static void ValidateVerifiedSourceTokens(List<Token> tokens, IReadOnlyList<VerifiedProtocolActor> actors)
    {
        // These exact source declarations were validated as checked protocol
        // bindings. No legacy actor declaration or executable call bypasses
        // the ordinary Native AOT admission gates.
        var protocolActorOffsets = actors.Select(actor => actor.Offset).ToHashSet();
        RejectUnsupportedSourceActors(tokens, protocolActorOffsets);
        RejectUnsupportedScalarTokens(tokens, protocolActorOffsets);
    }

    private static bool HasVerifiedRefinementPrefix(string source)
    {
        // Probe only the declaration prelude. An unrelated lexical error is
        // diagnosed by the selected ordinary frontend, not by this probe.
        var lexer = new Lexer(source, false);
        try
        {
            Token first = lexer.Read();
            for (int i = 0; i <= MaxTokens; i++)
            {
                if (first.Text == "type")
                    return Reader.IsVerifiedIdentifier(lexer.Read().Text) && lexer.Read().Text == "="
                        && lexer.Read().Text is ("int" or "i32") && lexer.Read().Text == "where";
                if (first.Text != "define") return false;
                Token kind = lexer.Read();
                if (kind.Text == "protocol") return true;
                if (kind.Text is not ("dimension" or "unit")) return false;
                Token next;
                do
                {
                    if (++i > MaxTokens) return false;
                    next = lexer.Read();
                    if (next.Text.Length == 0) return false;
                } while (next.Text != ";");
                first = lexer.Read();
            }
        }
        catch (FormatException)
        {
            return false;
        }
        return false;
    }

    private sealed record VerifiedPrelude(IReadOnlyList<VerifiedProtocol> Protocols,
        IReadOnlyList<VerifiedProtocolActor> Actors);

    private sealed partial class Reader
    {
        public bool StrictI64 { get; init; }

        private sealed record RefinedQuantity(QuantityUnit Unit, VerifiedI64Range Range, bool I32 = false)
        {
            public static RefinedQuantity Scalar { get; } = new(QuantityUnit.Scalar, VerifiedI64Range.Full);
            public static RefinedQuantity ScalarI32 { get; } = new(QuantityUnit.Scalar, VerifiedI64Range.SignedI32, true);

            public void Require(RefinedQuantity actual, string category, string operation)
            {
                if (Unit.IsScalar && !actual.Unit.IsScalar)
                    throw new FormatException($"E_UNIT_ERASURE: {operation} requires dimensionless scale one");
                Unit.RequireCompatible(actual.Unit);
                if (!Range.Includes(actual.Range))
                    throw new FormatException($"{category}: cannot prove {operation}");
            }
        }

        private sealed record VerifiedFunction(IReadOnlyList<RefinedQuantity> Parameters,
            RefinedQuantity Result, IReadOnlyList<int> UpperParameters);
        private sealed record QuantityTerm(StructuredExpr Expression, RefinedQuantity Type, int Height = 0,
            IReadOnlyCollection<string>? Bounds = null)
        {
            public IReadOnlyCollection<string> UpperBounds => Bounds ?? Array.Empty<string>();
        }

        private readonly Dictionary<string, RefinedQuantity> refinementAliases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RefinedQuantity> verifiedLocals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyCollection<string>> verifiedUpperBounds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RefinedQuantity> strictMutable = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VerifiedFunction> verifiedFunctions = new(StringComparer.Ordinal);

        internal static bool IsVerifiedIdentifier(string name) => IsSourceIdentifier(name);

        private string ReadVerifiedName()
        {
            var token = Current;
            if (!IsSourceIdentifier(token.Text) || token.Text is
                "int" or "i32" or "type" or "fnc" or "val" or "let" or "return" or "pub" or "private" or "self")
                throw new FormatException($"E_REFINEMENT_NAME: expected a name at offset {token.Offset}");
            _index++;
            return token.Text;
        }

        private bool AtRefinedQuantityAlias() => Current.Text == "type" && _index + 4 < tokens.Count
            && tokens[_index + 2].Text == "=" && tokens[_index + 3].Text is ("int" or "i32")
            && tokens[_index + 4].Text == "where";

        private RefinedQuantity ReadRefinedType()
        {
            if (Take("int")) return RefinedQuantity.Scalar;
            if (Take("i32"))
            {
                if (StrictI64) throw new FormatException("E_VERIFY_UNSUPPORTED: i32 strict proof pending");
                return RefinedQuantity.ScalarI32;
            }
            string name = ReadVerifiedName();
            if (!refinementAliases.TryGetValue(name, out var type))
                throw new FormatException($"E_REFINEMENT_NAME: unknown verified scalar type '{name}'");
            return type;
        }

        private string ReadQuantityComparison()
        {
            Token op = Current;
            if (op.Text is not ("<" or ">" or "eq"))
                throw new FormatException($"E_REFINEMENT_PREDICATE: expected an interval comparison at offset {op.Offset}");
            _index++;
            return op.Text != "eq" && Current.Offset == op.Offset + op.Text.Length && Take("=")
                ? op.Text + "=" : op.Text;
        }

        private (Token Token, long Value) ReadQuantityInteger(bool negative)
        {
            var token = Current;
            string digits = token.Text.Replace("_", "", StringComparison.Ordinal);
            if (digits.Length == 0 || !char.IsAsciiDigit(digits[0])
                || !long.TryParse(negative ? "-" + digits : digits, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out long value))
                throw new FormatException($"E_UNIT_EXPRESSION: expected a signed integer at offset {token.Offset}");
            _index++;
            return (token, value);
        }

        private void ParseRefinementAlias()
        {
            Expect("type");
            if (Current.Text is "int" or "i32")
                throw new FormatException($"E_REFINEMENT_DUPLICATE: reserved refinement carrier '{Current.Text}'");
            string name = ReadVerifiedName();
            Expect("=");
            bool i32 = Take("i32");
            if (StrictI64 && i32) throw new FormatException("E_VERIFY_UNSUPPORTED: i32 strict proof pending");
            if (!i32) Expect("int");
            Expect("where");
            QuantityUnit? unit = null;
            var range = i32 ? VerifiedI64Range.SignedI32 : VerifiedI64Range.Full;
            do
            {
                if (Take("units"))
                {
                    if (unit is not null) throw new FormatException("E_UNIT_DUPLICATE: repeated unit clause");
                    Expect("in");
                    unit = ReadUnit();
                }
                else
                {
                    Expect("self");
                    string op = ReadQuantityComparison();
                    range = range.Constrain(op, ReadQuantityInteger(Take("-")).Value);
                }
            } while (Take("and"));
            Expect(";");
            if (refinementAliases.Count >= 128) throw new FormatException("E_REFINEMENT_LIMIT: too many aliases");
            if (!refinementAliases.TryAdd(name, new(unit ?? QuantityUnit.Scalar, range, i32)))
                throw new FormatException($"E_REFINEMENT_DUPLICATE: duplicate refinement alias '{name}'");
        }

        private RefinedQuantity ReadVerifiedBound(RefinedQuantity type, string op)
        {
            var (number, value) = ReadQuantityInteger(Take("-"));
            var unit = QuantityUnit.Scalar;
            if (Current.Offset == number.Offset + number.Text.Length && Take("<"))
            {
                unit = ReadUnit();
                Expect(">");
            }
            type.Unit.RequireCompatible(unit);
            return type with { Range = type.Range.Constrain(op, value) };
        }

        public bool AtEndOfVerifiedSource => Current.Text.Length == 0;

        public VerifiedPrelude ParseVerifiedPrelude()
        {
            ParseQuantityDeclarations();
            while (AtRefinedQuantityAlias()) ParseRefinementAlias();
            if (StrictI64 && refinementAliases.Count == 0)
                throw new FormatException("E_VERIFY_UNSUPPORTED: strict typed refinement profile required");
            var protocols = new List<VerifiedProtocol>();
            var protocolNames = new HashSet<string>(StringComparer.Ordinal);
            while (AtProtocolDeclaration()) protocols.Add(ParseProtocolDeclaration(protocolNames));
            var protocolActors = new List<VerifiedProtocolActor>();
            var actorNames = new HashSet<string>(StringComparer.Ordinal);
            while (AtProtocolActor()) protocolActors.Add(ParseProtocolActor(protocols, actorNames));
            return new VerifiedPrelude(protocols.AsReadOnly(), protocolActors.AsReadOnly());
        }

        public StructuredProgram ParseVerifiedFunctions(VerifiedPrelude prelude)
        {
            var functions = new Dictionary<string, StructuredFunction>(StringComparer.Ordinal);
            var order = new List<string>();
            while (Current.Text.Length != 0)
            {
                if (functions.Count >= 64) throw new FormatException("E_REFINEMENT_LIMIT: too many verified functions");
                bool isPublic = Take("pub");
                if (!isPublic) Take("private");
                Expect("fnc");
                string name = ReadVerifiedName();
                bool main = name == "main";
                if (main != isPublic) throw new FormatException("E_REFINEMENT_EXPORT: only the scalar main may be public");
                if (functions.ContainsKey(name))
                    throw new FormatException("E_REFINEMENT_DUPLICATE: duplicate verified function");
                verifiedLocals.Clear();
                verifiedUpperBounds.Clear();
                strictMutable.Clear();
                Expect("(");
                var parameters = new List<StructuredParameter>();
                if (Current.Text != ")")
                {
                    do
                    {
                        if (parameters.Count >= 16) throw new FormatException("E_REFINEMENT_LIMIT: too many parameters");
                        var type = ReadRefinedType();
                        string param = ReadVerifiedName();
                        if (!verifiedLocals.TryAdd(param, type))
                            throw new FormatException("E_REFINEMENT_DUPLICATE: duplicate parameter");
                        verifiedUpperBounds.Add(param, new[] { param });
                        parameters.Add(new(param, IrValueType.I64));
                    } while (Take(","));
                }
                Expect(")");
                Expect("->");
                var declared = ReadRefinedType();
                if (main && (parameters.Count != 0 || !declared.Unit.IsScalar
                        || !declared.Range.Includes(VerifiedI64Range.Full)))
                    throw new FormatException("E_REFINEMENT_EXPORT: main must have signature () -> int");
                while (Take("requires"))
                {
                    if (main) throw new FormatException("E_REFINEMENT_EXPORT: main cannot require caller facts");
                    string param = ReadVerifiedName();
                    if (!verifiedLocals.TryGetValue(param, out var type))
                        throw new FormatException("E_REFINEMENT_NAME: requires must name a parameter");
                    verifiedLocals[param] = ReadVerifiedBound(type, ReadQuantityComparison());
                }
                var required = parameters.Select(param => verifiedLocals[param.Name]).ToArray();
                var guaranteed = declared;
                var upperParameters = new List<int>();
                while (Take("ensures"))
                {
                    Expect("result");
                    var op = ReadQuantityComparison();
                    if (op == "<=" && IsSourceIdentifier(Current.Text))
                    {
                        string parameter = ReadVerifiedName();
                        int parameterIndex = parameters.FindIndex(param => param.Name == parameter);
                        if (parameterIndex < 0)
                            throw new FormatException("E_REFINEMENT_NAME: ensures must name a parameter");
                        guaranteed.Unit.RequireCompatible(required[parameterIndex].Unit);
                        upperParameters.Add(parameterIndex);
                    }
                    else
                    {
                        guaranteed = ReadVerifiedBound(guaranteed, op);
                    }
                }
                Expect("{");
                var body = new List<StructuredStmt>();
                while (!StrictI64 && Take("val"))
                {
                    if (body.Count >= 128) throw new FormatException("E_REFINEMENT_LIMIT: too many immutable bindings");
                    int at = Current.Offset;
                    RefinedQuantity? type = null;
                    string local;
                    if (StrictI64 && _index + 1 < tokens.Count && tokens[_index + 1].Text == "=")
                    {
                        local = ReadVerifiedName();
                    }
                    else if (tokens[_index + 1].Text == ":")
                    {
                        local = ReadVerifiedName();
                        Expect(":");
                        type = ReadRefinedType();
                    }
                    else
                    {
                        type = ReadRefinedType();
                        local = ReadVerifiedName();
                    }
                    Expect("=");
                    var value = VerifiedExpression(0);
                    // Retain proven units and ranges; verify before adding the binding.
                    type ??= value.Type;
                    type.Require(value.Type, "E_REFINEMENT_BINDING", "binding " + local);
                    Expect(";");
                    if (!verifiedLocals.TryAdd(local, type with { Range = value.Type.Range }))
                        throw new FormatException("E_REFINEMENT_DUPLICATE: duplicate verified binding");
                    verifiedUpperBounds.Add(local, value.UpperBounds);
                    body.Add(new StructuredLet(at, local, IrValueType.I64, value.Expression, StructuredBindingKind.Val));
                }
                if (StrictI64)
                    body.AddRange(ParseStrictReturns(declared, guaranteed, name, upperParameters, parameters, required, 0));
                else
                {
                    int returnAt = Current.Offset;
                    if (!Take("return"))
                        throw new FormatException("E_REFINEMENT_BODY: expected an explicit return after immutable bindings");
                    var returned = VerifiedExpression(0);
                    declared.Require(returned.Type, "E_REFINEMENT_RETURN", "return from " + name);
                    guaranteed.Require(returned.Type, "E_REFINEMENT_POSTCONDITION", "ensures of " + name);
                    foreach (int upper in upperParameters)
                    {
                        string parameter = parameters[upper].Name;
                        if (!returned.UpperBounds.Contains(parameter) && !returned.Type.Range.AtMost(required[upper].Range))
                            throw new FormatException($"E_REFINEMENT_POSTCONDITION: cannot prove result <= {parameter}");
                    }
                    Expect(";");
                    body.Add(new StructuredReturn(returnAt, returned.Expression));
                }
                Expect("}");
                if (main)
                {
                    if (Current.Text.Length != 0)
                        throw new FormatException("E_REFINEMENT_SCOPE: main must be the final verified function");
                    return new StructuredProgram(name, body, functions, order)
                    {
                        Protocols = prelude.Protocols,
                        ProtocolActors = prelude.Actors,
                    };
                }
                functions.Add(name, new StructuredFunction(name, parameters, IrValueType.I64,
                    ReturnValue: null, Body: body));
                order.Add(name);
                verifiedFunctions.Add(name, new VerifiedFunction(required, guaranteed, upperParameters));
            }
            throw new FormatException("E_REFINEMENT_EXPORT: verified source requires a public main");
        }

        private sealed record StrictEdge(StructuredExpr Test,
            Dictionary<string,RefinedQuantity> Yes, Dictionary<string,RefinedQuantity> No);
        private Dictionary<string,RefinedQuantity> SnapshotStrict() => new(verifiedLocals, StringComparer.Ordinal);
        private void RestoreStrict(Dictionary<string,RefinedQuantity> facts)
        {
            verifiedLocals.Clear();
            foreach(var pair in facts) verifiedLocals.Add(pair.Key,pair.Value);
        }
        private static Dictionary<string,RefinedQuantity> JoinStrict(Dictionary<string,RefinedQuantity> a,
            Dictionary<string,RefinedQuantity> b) =>
            a.ToDictionary(p=>p.Key,p=>p.Value with { Range=p.Value.Range.Join(b[p.Key].Range) },StringComparer.Ordinal);
        private StrictEdge StrictCondition(Dictionary<string,RefinedQuantity> facts, int depth)
        {
            if(depth>16) throw new FormatException("E_VERIFY_UNSUPPORTED: condition depth");
            var p=StrictAnd(facts,depth);
            while(Current.Text is "||" or "or")
            {
                int at=Current.Offset; _index++;
                var q=StrictAnd(p.No,depth);
                p=new(new StructuredConditional(at,p.Test,new StructuredBool(at,true),q.Test,IrValueType.Bool),
                    JoinStrict(p.Yes,q.Yes),q.No);
            }
            return p;
        }
        private StrictEdge StrictAnd(Dictionary<string,RefinedQuantity> facts,int depth)
        {
            var p=StrictPredicate(facts,depth);
            while(Current.Text is "&&" or "and")
            {
                int at=Current.Offset; _index++;
                var q=StrictPredicate(p.Yes,depth);
                p=new(new StructuredConditional(at,p.Test,q.Test,new StructuredBool(at,false),IrValueType.Bool),
                    q.Yes,JoinStrict(p.No,q.No));
            }
            return p;
        }
        private StrictEdge StrictPredicate(Dictionary<string,RefinedQuantity> facts,int depth)
        {
            if(depth>16) throw new FormatException("E_VERIFY_UNSUPPORTED: predicate depth");
            RestoreStrict(facts);
            int at=Current.Offset;
            if(Take("!"))
            {
                var p=StrictPredicate(facts,depth+1);
                return new(new StructuredBinary(at,StructuredOperator.EqualBool,p.Test,new StructuredBool(at,false),IrValueType.Bool),p.No,p.Yes);
            }
            if(Take("("))
            {
                var p=StrictCondition(facts,depth+1);Expect(")");return p;
            }
            var left=VerifiedExpression(0);
            string op=Current.Text;
            int end=Current.Offset+op.Length;
            _index++;
            if((op is "<" or ">" or "=" or "!") && Current.Offset==end && Take("="))op+="=";
            op=op switch {"eq"=>"==","neq" or "!eq"=>"!=",_=>op};
            var cmp=op switch {
                "<"=>StructuredOperator.LessThanI64,"<="=>StructuredOperator.LessThanOrEqualI64,
                ">"=>StructuredOperator.GreaterThanI64,">="=>StructuredOperator.GreaterThanOrEqualI64,
                "=="=>StructuredOperator.EqualI64,"!="=>StructuredOperator.NotEqualI64,
                _=>throw new FormatException("E_VERIFY_UNSUPPORTED: strict comparison required")
            };
            var right=VerifiedExpression(0);left.Type.Unit.RequireCompatible(right.Type.Unit);
            var yes=SnapshotStrict();var no=SnapshotStrict();
            if(left.Expression is StructuredRead read && facts.TryGetValue(read.Name,out var binding)
                && right.Type.Range.Minimum==right.Type.Range.Maximum)
            {
                string inverse=op switch {"<"=>">=","<="=>">",">"=>"<=",">="=>"<","=="=>"!=",_=>"=="};
                long n=right.Type.Range.Minimum;
                yes[read.Name]=binding with { Range=binding.Range.Restrict(op,n) };
                no[read.Name]=binding with { Range=binding.Range.Restrict(inverse,n) };
            }
            return new(new StructuredBinary(at,cmp,left.Expression,right.Expression,IrValueType.Bool),yes,no);
        }
        private List<StructuredStmt> ParseStrictReturns(RefinedQuantity declared,RefinedQuantity guaranteed,
            string name,List<int> upperParameters,List<StructuredParameter> parameters,
            RefinedQuantity[] required,int depth)
        {
            if(depth>16)throw new FormatException("E_VERIFY_UNSUPPORTED: strict return depth");
            int at=Current.Offset;
            if(Take("val"))
            {
                RefinedQuantity? type=null;
                string local;
                if(_index+1<tokens.Count&&tokens[_index+1].Text=="=")local=ReadVerifiedName();
                else if(_index+1<tokens.Count&&tokens[_index+1].Text==":"){
                    local=ReadVerifiedName();Expect(":");type=ReadRefinedType();
                }else{type=ReadRefinedType();local=ReadVerifiedName();}
                Expect("=");var value=VerifiedExpression(0);Expect(";");
                type??=value.Type;
                type.Require(value.Type,"E_REFINEMENT_BINDING","immutable binding "+local);
                if(!verifiedLocals.TryAdd(local,type with {Range=value.Type.Range}))
                    throw new FormatException("E_REFINEMENT_DUPLICATE: duplicate immutable binding");
                verifiedUpperBounds.Add(local,value.UpperBounds);
                var tail=ParseStrictReturns(declared,guaranteed,name,upperParameters,parameters,required,depth+1);
                tail.Insert(0,new StructuredLet(at,local,IrValueType.I64,value.Expression,StructuredBindingKind.Val));
                return tail;
            }
            if(Take("let"))
            {
                var type=ReadRefinedType();
                string local=ReadVerifiedName();
                Expect("=");var value=VerifiedExpression(0);Expect(";");
                type.Require(value.Type,"E_REFINEMENT_BINDING","mutable binding "+local);
                if(!verifiedLocals.TryAdd(local,type with {Range=value.Type.Range}))
                    throw new FormatException("E_REFINEMENT_DUPLICATE: duplicate mutable binding");
                strictMutable.Add(local,type);
                verifiedUpperBounds.Add(local,value.UpperBounds);
                var tail=ParseStrictReturns(declared,guaranteed,name,upperParameters,parameters,required,depth+1);
                tail.Insert(0,new StructuredLet(at,local,IrValueType.I64,value.Expression,StructuredBindingKind.Let));
                return tail;
            }
            if(IsSourceIdentifier(Current.Text)&&_index+1<tokens.Count&&tokens[_index+1].Text=="=")
            {
                string local=ReadVerifiedName();Expect("=");
                if(!strictMutable.TryGetValue(local,out var type))
                    throw new FormatException("E_VERIFY_UNSUPPORTED: assignment requires mutable binding");
                var value=VerifiedExpression(0);Expect(";");
                type.Require(value.Type,"E_REFINEMENT_BINDING","assignment "+local);
                verifiedLocals[local]=type with {Range=value.Type.Range};
                verifiedUpperBounds[local]=value.UpperBounds;
                var tail=ParseStrictReturns(declared,guaranteed,name,upperParameters,parameters,required,depth+1);
                tail.Insert(0,new StructuredSet(at,local,value.Expression));
                return tail;
            }
            if(Take("while"))
            {
                // Only one direct scalar update is admitted on each backedge.
                // Scan the target then independently parse the entire guard/body.
                int brace=_index;
                while(brace<tokens.Count&&tokens[brace].Text!=""&&tokens[brace].Text!="{")brace++;
                if(brace+2>=tokens.Count||tokens[brace+2].Text!="="||
                   !strictMutable.TryGetValue(tokens[brace+1].Text,out var invariant))
                    throw new FormatException("E_VERIFY_UNSUPPORTED: strict while requires one mutable update");
                string local=tokens[brace+1].Text;
                invariant.Require(verifiedLocals[local],"E_REFINEMENT_BINDING","loop entry "+local);
                var header=SnapshotStrict();
                header[local]=invariant;
                // Relational facts about prior values cannot survive the backedge.
                verifiedUpperBounds[local]=Array.Empty<string>();
                var p=StrictCondition(header,0);
                Expect("{");RestoreStrict(p.Yes);
                if(ReadVerifiedName()!=local)throw new FormatException("E_VERIFY_UNSUPPORTED: wrong loop target");
                Expect("=");var update=VerifiedExpression(0);Expect(";");
                invariant.Require(update.Type,"E_REFINEMENT_BINDING","loop backedge "+local);
                if(!Take("}"))throw new FormatException("E_VERIFY_UNSUPPORTED: one direct loop update only");
                RestoreStrict(p.No);
                var tail=ParseStrictReturns(declared,guaranteed,name,upperParameters,parameters,required,depth+1);
                tail.Insert(0,new StructuredWhile(at,p.Test,new StructuredStmt[]{new StructuredSet(at,local,update.Expression)}));
                return tail;
            }
            if(Take("if"))
            {
                var p=StrictCondition(SnapshotStrict(),0);
                var savedBounds=new Dictionary<string,IReadOnlyCollection<string>>(verifiedUpperBounds,StringComparer.Ordinal);
                var savedMutable=new Dictionary<string,RefinedQuantity>(strictMutable,StringComparer.Ordinal);
                Expect("{");RestoreStrict(p.Yes);
                var yes=ParseStrictReturns(declared,guaranteed,name,upperParameters,parameters,required,depth+1);
                Expect("}");RestoreStrict(p.No);
                verifiedUpperBounds.Clear();
                foreach(var pair in savedBounds)verifiedUpperBounds.Add(pair.Key,pair.Value);
                strictMutable.Clear();
                foreach(var pair in savedMutable)strictMutable.Add(pair.Key,pair.Value);
                bool hasElse=Take("else");if(hasElse)Expect("{");
                var no=ParseStrictReturns(declared,guaranteed,name,upperParameters,parameters,required,depth+1);
                if(hasElse)Expect("}");
                return new(){new StructuredIf(at,p.Test,yes,no)};
            }
            if(!Take("return"))throw new FormatException("E_VERIFY_UNSUPPORTED: strict returning conditionals only");
            var returned=VerifiedExpression(0);
            declared.Require(returned.Type,"E_REFINEMENT_RETURN","return from "+name);
            guaranteed.Require(returned.Type,"E_REFINEMENT_POSTCONDITION","ensures of "+name);
            foreach(int upper in upperParameters)
            {
                string parameter=parameters[upper].Name;
                if(!returned.UpperBounds.Contains(parameter)&&!returned.Type.Range.AtMost(required[upper].Range))
                    throw new FormatException("E_REFINEMENT_POSTCONDITION: cannot prove result <= "+parameter);
            }
            Expect(";");
            return new(){new StructuredReturn(at,returned.Expression)};
        }

        private QuantityTerm AttachVerifiedUnit(QuantityTerm term, string name)
        {
            if (!_units.TryGetValue(name, out var unit))
                throw new FormatException("E_UNIT_NAME: unknown unit");
            if (refinementAliases.ContainsKey(name) || name is "int" or "i64" or "i32")
                throw new FormatException("E_UNIT_NAME: ambiguous unit and type target");
            if (!term.Type.Unit.IsScalar)
                throw new FormatException("E_UNIT_ATTACHMENT: unit attachment requires dimensionless scale one");
            CheckDepth(term.Height + 1);
            return new QuantityTerm(term.Expression, term.Type with { Unit = unit }, term.Height + 1);
        }

        private QuantityTerm ApplyVerifiedCast(int at, QuantityTerm term, RefinedQuantity target)
        {
            if (target.Unit.IsScalar && !term.Type.Unit.IsScalar)
                throw new FormatException("E_UNIT_ERASURE: scalar cast cannot erase units");
            target.Unit.RequireCompatible(term.Type.Unit);
            if (term.Height + 1 >= MaxDepth)
                throw new FormatException("E_UNIT_LIMIT: quantity expression is too deep");
            if (StrictI64 && !target.Range.Includes(term.Type.Range))
                throw new FormatException("E_VERIFY_UNPROVEN: refinement cast may fail");
            var range = term.Type.Range.OnNormalResultWithin(target.Range);
            return new QuantityTerm(
                new StructuredCheckRefinement(at, term.Expression, target.Range.Minimum, target.Range.Maximum),
                target with { Range = range }, term.Height + 1, term.UpperBounds);
        }

        private RefinedQuantity ReadVerifiedCastTarget() => Take("i64") ? RefinedQuantity.Scalar : ReadRefinedType();

        private QuantityTerm ReadAndApplyVerifiedCast(int at, QuantityTerm term)
        {
            if (_units.ContainsKey(Current.Text)) return AttachVerifiedUnit(term, ReadQuantityName());
            return ApplyVerifiedCast(at, term, ReadVerifiedCastTarget());
        }

        private QuantityTerm VerifiedExpression(int depth)
        {
            var term = VerifiedSum(depth);
            while (Current.Text == "as")
            {
                int at = Current.Offset;
                _index++;
                if (Current.Text == "?")
                    throw new FormatException("E_REFINEMENT_CAST: optional refinement casts are unsupported");
                term = ReadAndApplyVerifiedCast(at, term);
            }
            return term;
        }

        private QuantityTerm VerifiedSum(int depth)
        {
            var left = VerifiedProduct(depth);
            while (Current.Text is "+" or "-")
            {
                Token op = Current;
                _index++;
                var right = VerifiedProduct(depth);
                left.Type.Unit.RequireCompatible(right.Type.Unit);
                bool add = op.Text == "+";
                if (StrictI64 && !left.Type.Range.ProvesArithmetic(op.Text, right.Type.Range))
                    throw new FormatException("E_VERIFY_UNPROVEN: signed arithmetic may overflow");
                var range = add ? left.Type.Range.Add(right.Type.Range) : left.Type.Range.Subtract(right.Type.Range);
                left = VerifiedBinary(op.Offset, add ? StructuredOperator.Add : StructuredOperator.Subtract,
                    left, right, new(left.Type.Unit, range));
            }
            return left;
        }

        private QuantityTerm VerifiedProduct(int depth)
        {
            var left = VerifiedAtom(depth);
            while (Current.Text is "*" or "/")
            {
                Token op = Current;
                _index++;
                var right = VerifiedAtom(depth);
                bool divide = op.Text == "/";
                if (StrictI64 && !left.Type.Range.ProvesArithmetic(op.Text, right.Type.Range))
                    throw new FormatException("E_VERIFY_UNPROVEN: signed arithmetic may overflow or divide by zero");
                if (!StrictI64 && divide && !left.Type.I32 && !right.Type.I32
                        && left.Type.Unit.IsScalar && right.Type.Unit.IsScalar)
                    throw new FormatException("E_UNIT_SCALAR_DIVISION: two plain int operands use ordinary division");
                var unit = left.Type.Unit.Combine(right.Type.Unit, divide);
                var range = divide ? left.Type.Range.Divide(right.Type.Range) : left.Type.Range.Multiply(right.Type.Range);
                left = VerifiedBinary(op.Offset, divide ? StructuredOperator.DivideQuantity : StructuredOperator.Multiply,
                    left, right, new(unit, range));
            }
            return left;
        }

        private static QuantityTerm VerifiedBinary(int at, StructuredOperator op,
            QuantityTerm left, QuantityTerm right, RefinedQuantity type)
        {
            bool i32 = left.Type.I32 || right.Type.I32;
            var carrier = VerifiedI64Range.SignedI32;
            if (i32 && (!carrier.Includes(left.Type.Range) || !carrier.Includes(right.Type.Range)))
                throw new FormatException("E_I32_RANGE: checked i32 arithmetic requires both operands to fit i32");
            int height = Math.Max(left.Height, right.Height) + (i32 ? 2 : 1);
            if (height >= MaxDepth) throw new FormatException("E_UNIT_LIMIT: quantity expression is too deep");
            bool boundedByLeft = op switch
            {
                StructuredOperator.Add => right.Type.Range.Nonpositive,
                StructuredOperator.Subtract => right.Type.Range.Nonnegative,
                StructuredOperator.DivideQuantity => left.Type.Range.Nonnegative
                    && right.Type.Range.Positive && right.Type.Unit.IsScalar,
                _ => false,
            };
            StructuredExpr value = new StructuredBinary(at, op, left.Expression, right.Expression, IrValueType.I64);
            if (i32)
            {
                value = new StructuredCheckedI32(at, value);
                type = type with { I32 = true, Range = type.Range.OnNormalResultWithin(carrier) };
            }
            return new QuantityTerm(value, type, height, boundedByLeft ? left.UpperBounds : null);
        }

        private QuantityTerm VerifiedAtom(int depth)
        {
            var term = VerifiedAtomBase(depth);
            while (Current.Text is "." or "::" && _index + 1 < tokens.Count && tokens[_index + 1].Text == "<")
            {
                int at = Current.Offset;
                bool unitOnly = Take(".");
                if (!unitOnly) Expect("::");
                Expect("<");
                term = unitOnly ? AttachVerifiedUnit(term, ReadQuantityName()) : ReadAndApplyVerifiedCast(at, term);
                Expect(">");
            }
            return term;
        }

        private QuantityTerm VerifiedAtomBase(int depth)
        {
            CheckDepth(depth);
            if (Current.Text == "<")
            {
                int offset = Current.Offset;
                _index++;
                var target = ReadVerifiedCastTarget();
                Expect(">");
                return ApplyVerifiedCast(offset, VerifiedAtom(depth + 1), target);
            }
            if (Take("("))
            {
                var term = VerifiedExpression(depth + 1);
                Expect(")");
                return term;
            }
            if (Take("+")) return VerifiedAtom(depth + 1);
            int at = Current.Offset;
            if (Take("-"))
            {
                if (Current.Text.Replace("_", "", StringComparison.Ordinal).TrimStart('0') == "9223372036854775808")
                    return VerifiedLiteral(negative: true);
                var term = VerifiedAtom(depth + 1);
                if (StrictI64 && !term.Type.Range.ProvesNegation)
                    throw new FormatException("E_VERIFY_UNPROVEN: signed negation may overflow");
                if (!term.Type.I32)
                    return new QuantityTerm(new StructuredNegate(at, term.Expression),
                        term.Type with { Range = VerifiedI64Range.Point(0).Subtract(term.Type.Range) }, term.Height + 1);
                var zero = new QuantityTerm(new StructuredInt(at, 0), new(term.Type.Unit, VerifiedI64Range.Point(0)));
                return VerifiedBinary(at, StructuredOperator.Subtract, zero, term,
                    new(term.Type.Unit, zero.Type.Range.Subtract(term.Type.Range)));
            }
            if (IsSourceIdentifier(Current.Text)) return VerifiedReference(depth);
            return VerifiedLiteral(negative: false);
        }

        private QuantityTerm VerifiedLiteral(bool negative)
        {
            var (token, value) = ReadQuantityInteger(negative);
            var unit = QuantityUnit.Scalar;
            if (Current.Offset == token.Offset + token.Text.Length && Take("<"))
            {
                unit = ReadUnit();
                Expect(">");
                if (Current.Text == "." && tokens[_index + 1].Text != "<")
                {
                    _index++;
                    Expect("to_unit");
                    Expect("<");
                    var target = ReadUnit();
                    Expect(">");
                    Expect("(");
                    Expect(")");
                    value = unit.ConvertExact(value, target);
                    unit = target;
                }
            }
            return new(new StructuredInt(token.Offset, value), new(unit, VerifiedI64Range.Point(value)));
        }

        private QuantityTerm VerifiedReference(int depth)
        {
            int at = Current.Offset;
            string name = ReadVerifiedName();
            if (!Take("("))
            {
                if (!verifiedLocals.TryGetValue(name, out var type))
                    throw new FormatException($"E_REFINEMENT_NAME: unknown immutable value '{name}'");
                return new(new StructuredRead(at, name, IrValueType.I64), type, Bounds: verifiedUpperBounds[name]);
            }
            if (!verifiedFunctions.TryGetValue(name, out var function))
                throw new FormatException("E_REFINEMENT_CALL: expected an earlier verified function");
            var arguments = new List<StructuredExpr>();
            var upperBounds = new HashSet<string>(StringComparer.Ordinal);
            int height = 1;
            if (Current.Text != ")")
            {
                do
                {
                    int index = arguments.Count;
                    if (index >= function.Parameters.Count)
                        throw new FormatException("E_REFINEMENT_CALL: incorrect arity");
                    var arg = VerifiedExpression(depth + 1);
                    function.Parameters[index].Require(arg.Type, "E_REFINEMENT_PRECONDITION",
                        $"argument {index + 1} of {name}");
                    arguments.Add(arg.Expression);
                    if (function.UpperParameters.Contains(index)) upperBounds.UnionWith(arg.UpperBounds);
                    height = Math.Max(height, arg.Height + 1);
                } while (Take(","));
            }
            Expect(")");
            if (arguments.Count != function.Parameters.Count)
                throw new FormatException("E_REFINEMENT_CALL: incorrect arity");
            if (height >= MaxDepth) throw new FormatException("E_UNIT_LIMIT: quantity expression is too deep");
            return new(new StructuredCall(at, name, arguments, IrValueType.I64), function.Result, height, upperBounds);
        }
    }
}
