using System.Globalization;

namespace Ores.Net.Compiler;

public static partial class OresParser
{
    /// <summary>Recognize the file-local refinement profile using the production source lexer.</summary>
    public static VerifiedCfgModule? TryLowerVerifiedRefinementModule(string source) =>
        LowerRefinementProfile(source, false);

    public static VerifiedCfgModule LowerVerifiedStrictI64(string source) =>
        LowerRefinementProfile(source, true) ?? throw new FormatException(
            "E_VERIFY_UNSUPPORTED: strict signed-i64 refinement source required");

    private static VerifiedCfgModule? LowerRefinementProfile(string source, bool strict)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1_048_576 || !HasVerifiedRefinementPrefix(source)) return null;
        var tokens = Tokenize(source, strictGuards: strict);
        RejectUnsupportedInvertedClasses(tokens);
        var reader = new Reader(tokens, DotnetHostImportPolicy.DenyAll, source, null) { StrictVerifiedI64 = strict };
        var prelude = reader.ParseVerifiedPrelude();
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

        public bool StrictVerifiedI64 { get; init; }
        private readonly Dictionary<string, RefinedQuantity> refinementAliases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RefinedQuantity> verifiedLocals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyCollection<string>> verifiedUpperBounds = new(StringComparer.Ordinal);
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
            if (Take("i32")) return RefinedQuantity.ScalarI32;
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
            if (StrictVerifiedI64 && (refinementAliases.Count == 0 || AtProtocolDeclaration() || AtProtocolActor()))
                throw new FormatException("E_VERIFY_UNSUPPORTED: strict standalone signed-i64 refinement source only");
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
                while (Take("val"))
                {
                    if (body.Count >= 128) throw new FormatException("E_REFINEMENT_LIMIT: too many immutable bindings");
                    int at = Current.Offset;
                    RefinedQuantity? type = null;
                    string local;
                    if (tokens[_index + 1].Text == ":")
                    {
                        local = ReadVerifiedName();
                        Expect(":");
                        type = ReadRefinedType();
                    }
                    else if (tokens[_index + 1].Text == "=")
                    {
                        local = ReadVerifiedName();
                    }
                    else
                    {
                        type = ReadRefinedType();
                        local = ReadVerifiedName();
                    }
                    if (verifiedLocals.ContainsKey(local) || refinementAliases.ContainsKey(local) ||
                        local is "i64" or "main" or "infer" or "_")
                        throw new FormatException("E_REFINEMENT_DUPLICATE: reserved or duplicate binding");
                    Expect("=");
                    var value = VerifiedExpression(0);
                    if (type is null) type = value.Type;
                    else
                    {
                        type.Unit.RequireCompatible(value.Type.Unit);
                        type.Require(value.Type, "E_REFINEMENT_BINDING", "binding " + local);
                    }
                    Expect(";");
                    if (!verifiedLocals.TryAdd(local, type with { Range = value.Type.Range }))
                        throw new FormatException("E_REFINEMENT_DUPLICATE: duplicate verified binding");
                    verifiedUpperBounds.Add(local, value.UpperBounds);
                    body.Add(new StructuredLet(at, local, IrValueType.I64, value.Expression, StructuredBindingKind.Val));
                }
                int returnAt = Current.Offset;
                QuantityTerm returned;
                if (StrictVerifiedI64) returned = StrictVerifiedReturn(0);
                else
                {
                    if (!Take("return"))
                        throw new FormatException("E_REFINEMENT_BODY: expected an explicit return after immutable bindings");
                    returned = VerifiedExpression(0);
                    Expect(";");
                }
                declared.Require(returned.Type, "E_REFINEMENT_RETURN", "return from " + name);
                guaranteed.Require(returned.Type, "E_REFINEMENT_POSTCONDITION", "ensures of " + name);
                foreach (int upper in upperParameters)
                {
                    string parameter = parameters[upper].Name;
                    if (!returned.UpperBounds.Contains(parameter) && !returned.Type.Range.AtMost(required[upper].Range))
                        throw new FormatException($"E_REFINEMENT_POSTCONDITION: cannot prove result <= {parameter}");
                }
                Expect("}");
                body.Add(new StructuredReturn(returnAt, returned.Expression));
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

        private sealed record StrictPredicate(StructuredExpr Expression,
            Dictionary<string, RefinedQuantity> Yes, Dictionary<string, RefinedQuantity> No);
        private void StrictFacts(Dictionary<string, RefinedQuantity> facts)
        {
            verifiedLocals.Clear();
            foreach (var pair in facts) verifiedLocals.Add(pair.Key, pair.Value);
        }
        private static Dictionary<string, RefinedQuantity> StrictJoin(
            Dictionary<string, RefinedQuantity> a, Dictionary<string, RefinedQuantity> b)
        {
            var result = new Dictionary<string, RefinedQuantity>(StringComparer.Ordinal);
            foreach (var pair in a) result.Add(pair.Key, pair.Value with { Range = pair.Value.Range.Join(b[pair.Key].Range) });
            return result;
        }
        private static VerifiedI64Range StrictRestrict(VerifiedI64Range range, string op, long value)
        {
            try { return op == "neq" ? range.Excluding(value) : range.Constrain(op, value); }
            catch (FormatException) { return range; } // no vacuous unreachable proof
        }
        private bool StrictTakePair(string symbol)
        {
            if (Current.Text == symbol + symbol) { _index++; return true; }
            if (Current.Text != symbol || _index + 1 >= tokens.Count ||
                tokens[_index+1].Text != symbol || tokens[_index+1].Offset != Current.Offset + 1)
                return false;
            _index += 2; return true;
        }
        private StrictPredicate StrictReadPredicate(Dictionary<string, RefinedQuantity> incoming, int depth)
        {
            if (depth > 16) throw new FormatException("E_UNIT_LIMIT: strict condition depth");
            StrictFacts(incoming);
            int at = Current.Offset;
            if (Take("!"))
            {
                var p = StrictReadPredicate(incoming, depth+1);
                return new(new StructuredConditional(at,p.Expression,new StructuredBool(at,false),
                    new StructuredBool(at,true),IrValueType.Bool),p.No,p.Yes);
            }
            if (Take("("))
            {
                var p = StrictOr(incoming,depth+1); Expect(")"); return p;
            }
            var left = VerifiedExpression(0);
            string op = Current.Text;
            if (op is "=" or "!" or "<" or ">")
            {
                _index++;
                if (Current.Text == "=" && Current.Offset == tokens[_index-1].Offset + op.Length)
                { op += "="; _index++; }
            }
            else _index++;
            if (op == "==") op = "eq";
            if (op == "!=") op = "neq";
            var (kind,inverse) = op switch
            {
                "eq" => (StructuredOperator.EqualI64,"neq"),
                "neq" => (StructuredOperator.NotEqualI64,"eq"),
                "<" => (StructuredOperator.LessThanI64,">="),
                "<=" => (StructuredOperator.LessThanOrEqualI64,">"),
                ">" => (StructuredOperator.GreaterThanI64,"<="),
                ">=" => (StructuredOperator.GreaterThanOrEqualI64,"<"),
                _ => throw new FormatException("E_VERIFY_UNSUPPORTED: expected strict comparison")
            };
            var right = VerifiedExpression(0);
            left.Type.Unit.RequireCompatible(right.Type.Unit);
            var yes = new Dictionary<string,RefinedQuantity>(incoming,StringComparer.Ordinal);
            var no = new Dictionary<string,RefinedQuantity>(incoming,StringComparer.Ordinal);
            if (left.Expression is StructuredRead read && incoming.TryGetValue(read.Name,out var type)
                && right.Type.Range.Minimum == right.Type.Range.Maximum)
            {
                yes[read.Name] = type with { Range = StrictRestrict(type.Range,op,right.Type.Range.Minimum) };
                no[read.Name] = type with { Range = StrictRestrict(type.Range,inverse,right.Type.Range.Minimum) };
            }
            return new(new StructuredBinary(at,kind,left.Expression,right.Expression,IrValueType.Bool),yes,no);
        }
        private StrictPredicate StrictAnd(Dictionary<string, RefinedQuantity> incoming, int depth)
        {
            var p = StrictReadPredicate(incoming,depth); int count=0;
            while (StrictTakePair("&"))
            {
                if (++count>16) throw new FormatException("E_UNIT_LIMIT: strict conjunction budget");
                var q=StrictReadPredicate(p.Yes,depth);
                p=new(new StructuredConditional(Current.Offset,p.Expression,q.Expression,
                    new StructuredBool(Current.Offset,false),IrValueType.Bool),q.Yes,StrictJoin(p.No,q.No));
            }
            return p;
        }
        private StrictPredicate StrictOr(Dictionary<string, RefinedQuantity> incoming, int depth)
        {
            var p=StrictAnd(incoming,depth); int count=0;
            while (StrictTakePair("|"))
            {
                if (++count>16) throw new FormatException("E_UNIT_LIMIT: strict disjunction budget");
                var q=StrictAnd(p.No,depth);
                p=new(new StructuredConditional(Current.Offset,p.Expression,new StructuredBool(Current.Offset,true),
                    q.Expression,IrValueType.Bool),StrictJoin(p.Yes,q.Yes),q.No);
            }
            return p;
        }
        private QuantityTerm StrictVerifiedReturn(int depth)
        {
            if (depth>16) throw new FormatException("E_UNIT_LIMIT: strict return depth");
            int at=Current.Offset;
            if (Take("if"))
            {
                var incoming=new Dictionary<string,RefinedQuantity>(verifiedLocals,StringComparer.Ordinal);
                var p=StrictOr(incoming,0);
                Expect("{");StrictFacts(p.Yes);
                var yes=StrictVerifiedReturn(depth+1);Expect("}");
                StrictFacts(p.No);bool alternate=Take("else");
                if(alternate)Expect("{");
                var no=StrictVerifiedReturn(depth+1);if(alternate)Expect("}");
                StrictFacts(incoming);
                yes.Type.Unit.RequireCompatible(no.Type.Unit);
                if(yes.Type.I32!=no.Type.I32)throw new FormatException("E_VERIFY_UNSUPPORTED: mixed branch carriers");
                return new(new StructuredConditional(at,p.Expression,yes.Expression,no.Expression,IrValueType.I64),
                    yes.Type with { Range=yes.Type.Range.Join(no.Type.Range) },Math.Max(yes.Height,no.Height)+1,
                    yes.UpperBounds.Intersect(no.UpperBounds).ToArray());
            }
            if(!Take("return"))throw new FormatException("E_VERIFY_UNSUPPORTED: strict returning branch requires return");
            var value=VerifiedExpression(0);Expect(";");return value;
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
            if (StrictVerifiedI64 && !target.Range.Includes(term.Type.Range))
                throw new FormatException("E_VERIFY_UNPROVEN: refinement cast may fail");
            if (term.Height + 1 >= MaxDepth)
                throw new FormatException("E_UNIT_LIMIT: quantity expression is too deep");
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
                if (!StrictVerifiedI64 && divide && !left.Type.I32 && !right.Type.I32
                        && left.Type.Unit.IsScalar && right.Type.Unit.IsScalar)
                    throw new FormatException("E_UNIT_SCALAR_DIVISION: two plain int operands use ordinary division");
                var unit = left.Type.Unit.Combine(right.Type.Unit, divide);
                var range = divide ? left.Type.Range.Divide(right.Type.Range) : left.Type.Range.Multiply(right.Type.Range);
                left = VerifiedBinary(op.Offset, divide ? StructuredOperator.DivideQuantity : StructuredOperator.Multiply,
                    left, right, new(unit, range));
            }
            return left;
        }

        private QuantityTerm VerifiedBinary(int at, StructuredOperator op,
            QuantityTerm left, QuantityTerm right, RefinedQuantity type)
        {
            bool i32 = left.Type.I32 || right.Type.I32;
            if (StrictVerifiedI64)
            {
                if (i32) throw new FormatException("E_VERIFY_UNSUPPORTED: strict i32 arithmetic not implemented");
                System.Numerics.BigInteger a = left.Type.Range.Minimum, b = left.Type.Range.Maximum,
                    c = right.Type.Range.Minimum, d = right.Type.Range.Maximum;
                static bool Fits(System.Numerics.BigInteger n) => n >= long.MinValue && n <= long.MaxValue;
                bool proven = op switch
                {
                    StructuredOperator.Add => Fits(a+c) && Fits(b+d),
                    StructuredOperator.Subtract => Fits(a-d) && Fits(b-c),
                    StructuredOperator.Multiply => Fits(a*c) && Fits(a*d) && Fits(b*c) && Fits(b*d),
                    StructuredOperator.DivideQuantity => !right.Type.Range.ContainsValue(0) &&
                        !(left.Type.Range.ContainsValue(long.MinValue) && right.Type.Range.ContainsValue(-1)),
                    _ => false
                };
                if (!proven) throw new FormatException("E_VERIFY_UNPROVEN: signed arithmetic may overflow or divide by zero");
            }
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
                if (StrictVerifiedI64 && term.Type.Range.Minimum == long.MinValue)
                    throw new FormatException("E_VERIFY_UNPROVEN: negation may overflow signed i64");
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
