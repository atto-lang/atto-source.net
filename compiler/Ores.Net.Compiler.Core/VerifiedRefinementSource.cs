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
        RejectUnsupportedSourceActors(tokens);
        RejectUnsupportedInvertedClasses(tokens);
        RejectUnsupportedScalarTokens(tokens);
        var reader = new Reader(tokens, DotnetHostImportPolicy.DenyAll, source, null);
        return OresStructuredCfgLowering.LowerModule(reader.ParseVerifiedFunctions());
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
                if (first.Text != "define" || lexer.Read().Text is not ("dimension" or "unit"))
                    return false;
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

        public StructuredProgram ParseVerifiedFunctions()
        {
            ParseQuantityDeclarations();
            while (AtRefinedQuantityAlias()) ParseRefinementAlias();
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
                    RefinedQuantity type;
                    string local;
                    if (tokens[_index + 1].Text == ":")
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
                    type.Require(value.Type, "E_REFINEMENT_BINDING", "binding " + local);
                    Expect(";");
                    if (!verifiedLocals.TryAdd(local, type with { Range = value.Type.Range }))
                        throw new FormatException("E_REFINEMENT_DUPLICATE: duplicate verified binding");
                    verifiedUpperBounds.Add(local, value.UpperBounds);
                    body.Add(new StructuredLet(at, local, IrValueType.I64, value.Expression, StructuredBindingKind.Val));
                }
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
                Expect("}");
                body.Add(new StructuredReturn(returnAt, returned.Expression));
                if (main)
                {
                    if (Current.Text.Length != 0)
                        throw new FormatException("E_REFINEMENT_SCOPE: main must be the final verified function");
                    return new StructuredProgram(name, body, functions, order);
                }
                functions.Add(name, new StructuredFunction(name, parameters, IrValueType.I64,
                    ReturnValue: null, Body: body));
                order.Add(name);
                verifiedFunctions.Add(name, new VerifiedFunction(required, guaranteed, upperParameters));
            }
            throw new FormatException("E_REFINEMENT_EXPORT: verified source requires a public main");
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
                var target = ReadRefinedType();
                target.Unit.RequireCompatible(term.Type.Unit);
                if (term.Height + 1 >= MaxDepth)
                    throw new FormatException("E_UNIT_LIMIT: quantity expression is too deep");
                var range = term.Type.Range.OnNormalResultWithin(target.Range);
                term = new QuantityTerm(
                    new StructuredCheckRefinement(at, term.Expression, target.Range.Minimum, target.Range.Maximum),
                    target with { Range = range }, term.Height + 1, term.UpperBounds);
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
                if (divide && !left.Type.I32 && !right.Type.I32
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
            CheckDepth(depth);
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
                if (Take("."))
                {
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
