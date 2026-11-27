using System.Globalization;

namespace Ores.Net.Compiler;

[Flags]
internal enum MatchTags { Negative = 1, Positive = 2, Any = Negative | Positive }

internal sealed record StructuredMatchArm(MatchTags Tags, long? Literal, string? Binding, IrValueType? BindingType,
    StructuredExpr? Guard, IReadOnlyList<StructuredStmt>? Body, StructuredExpr? Value,
    IReadOnlyList<long>? Membership = null);
internal sealed record StructuredMatch(StructuredExpr Subject, IReadOnlyList<StructuredMatchArm> Arms,
    IrValueType Type, bool Completes);
internal sealed record StructuredDoMatch(int At, StructuredMatch Match) : StructuredStmt(At);
internal sealed record StructuredMatchValue(int At, StructuredMatch Match) : StructuredExpr(At, Match.Type);

public static partial class OresStructuredParser
{
    private sealed partial class Reader
    {
        private StructuredExpr ReadMatchValue(int offset, int depth, StructuredExpr? receiver = null)
        {
            var match = ReadMatch(depth + 1, imperative: false,
                writebackRoot: receiver is null && matchWritebackRoot == offset, receiver: receiver);
            var children = new List<StructuredExpr> { match.Subject };
            foreach (var arm in match.Arms)
            {
                if (arm.Guard is { } guard) children.Add(guard);
                children.Add(arm.Value!);
            }
            return TaggedPostfix(TypedNode(new StructuredMatchValue(offset, match), children.ToArray()), depth);
        }

        private StructuredMatch ReadMatch(int depth, bool imperative, bool writebackRoot = false,
            StructuredExpr? receiver = null)
        {
            Budget(depth);
            IrValueType? result = imperative ? IrValueType.Void : null;
            if (!imperative && Take("<"))
            {
                if (Take("ret")) Expect("=");
                result = Type();
                Expect(">");
            }

            // The result annotation describes the arms, never the scrutinee.
            var context = expectedExpressionType;
            expectedExpressionType = null;
            try
            {
                var subject = receiver ?? Expr(depth + 1);
                if (subject.Type is not (IrValueType.I64 or IrValueType.Bool or IrValueType.OptionI64) && !subject.Type.IsResult())
                    throw Error("MATCH_TYPE_UNSUPPORTED: match requires int, bool, Option<int> or a closed Result");
                Expect("over");
                string end = Take("{") ? "}" : "end";
                var arms = new List<StructuredMatchArm>();
                MatchTags uncovered = MatchTags.Any;
                var coveredLiterals = new HashSet<long>();
                bool completes = !imperative;
                while (Current.Text is "on" or "is" or "in")
                {
                    bool membershipHead = Take("in");
                    if (!membershipHead) Take("on");
                    if (++statements > MaxStatements) throw Error("Structured match statement budget exceeded");
                    IReadOnlyList<long>? membership = null;
                    long? literal = null;
                    MatchTags tags = 0;
                    string? binding = null;
                    IrValueType? bindingType = null;
                    if (membershipHead || patternConstants.ContainsKey(Current.Text) || Current.Text == "is")
                    {
                        var descriptor = membershipHead ? ReadPatternMembership(0)
                            : Current.Text == "is" ? ReadPatternDescriptor(0) : ReadPatternRef(false);
                        (tags, membership) = ResolveStoredPattern(descriptor, subject.Type);
                    }
                    else
                    {
                        literal = subject.Type == IrValueType.I64 ? ReadMatchLiteral() : null;
                        if (literal is null) (tags, binding, bindingType) = ReadMatchPattern(subject.Type);
                    }
                    if (membership is not null ? uncovered == 0 || membership.All(coveredLiterals.Contains)
                        : literal is long n ? uncovered == 0 || coveredLiterals.Contains(n) : (tags & uncovered) == 0)
                        throw Error("MATCH_UNREACHABLE_ARM: prior unguarded arms already cover this pattern");

                    var scope = new Dictionary<string, LocalBinding>(StringComparer.Ordinal);
                    if (binding is not null)
                    {
                        if (scopes.Any(outer => outer.ContainsKey(binding)) || FindAggregate(binding) is not null)
                            throw Error("Match binder shadows a visible local");
                        scope.Add(binding, new LocalBinding(bindingType!.Value, StructuredBindingKind.Val, null));
                    }
                    scopes.Add(scope);
                    try
                    {
                        if (!imperative && Take(":"))
                        {
                            var armType = Type();
                            if (armType == IrValueType.Void || result is { } known && known != armType)
                                throw Error("MATCH_ARM_TYPE: arm annotation must agree with the match value type");
                            result ??= armType;
                        }
                        var guard = Take("when") ? Expr(depth + 1) : null;
                        if (guard is not null) Require(guard, IrValueType.Bool, "Match guard must be bool");
                        else if (membership is not null) coveredLiterals.UnionWith(membership);
                        else if (literal is long covered) coveredLiterals.Add(covered);
                        else uncovered &= ~tags;
                        bool arrow = Take("->");
                        if (imperative)
                        {
                            var previousReturn = localReturn;
                            var scopeReturn = new LocalReturnScope("MATCH_VOID_ARM", IrValueType.Void, loopDepth);
                            localReturn = scopeReturn;
                            try
                            {
                                var (body, terminal) = Block(depth + 1);
                                completes |= !terminal || scopeReturn.HasExit;
                                arms.Add(new StructuredMatchArm(tags, literal, binding, bindingType, guard, body, null, membership));
                            }
                            finally { localReturn = previousReturn; }
                        }
                        else
                        {
                            bool braced = Take("{");
                            if (braced && !Take("return") && !Take("^"))
                                throw Error("MATCH_ARM_RETURN_REQUIRED: a value match block requires return or ^");
                            if (!braced && !arrow) throw Error("Expected a match arm block or '->'");
                            var value = ReadMatchStatementArm(depth + 1, result ?? context, writebackRoot);
                            if (value.Type is IrValueType.FixedArrayI64 or IrValueType.Void)
                                throw Error("MATCH_VALUE_ABI_UNSUPPORTED: match requires a supported scalar or tagged result");
                            result ??= value.Type;
                            Require(value, result.Value, "Match arms must return the same value type");
                            if (braced)
                            {
                                Expect(";");
                                Expect("}");
                            }
                            else Take(";");
                            arms.Add(new StructuredMatchArm(tags, literal, binding, bindingType, guard, null, value, membership));
                        }
                    }
                    finally { scopes.RemoveAt(scopes.Count - 1); }
                    Take(",");
                }
                if (Current.Text == "default") throw MatchDefaultError();
                if (!Take(end)) throw Error("MATCH_SYNTAX: expected 'on', 'in', 'is' or the end of match");
                if (uncovered != 0)
                    throw Error("MATCH_NON_EXHAUSTIVE: cover all tags with unguarded arms or add 'on _'");
                return new StructuredMatch(subject, arms.AsReadOnly(), result!.Value, completes);
            }
            finally { expectedExpressionType = context; }
        }

        private sealed record CompilePattern(long[] Ints, bool[] Bools, IrValueType[] Types);
        private readonly Dictionary<string, (bool Array, CompilePattern Pattern)> patternConstants = new(StringComparer.Ordinal);
        private readonly HashSet<int> patternNameUses = new();
        private readonly Dictionary<string, CompilePattern> patternTypeAliases = new(StringComparer.Ordinal);
        private readonly HashSet<int> patternAliasUses = new();
        private readonly HashSet<int> verifiedPatternBrackets = new();

        private bool TakePatternBracket(string text)
        {
            int at = Current.Offset;
            if (!Take(text)) return false;
            verifiedPatternBrackets.Add(at);
            return true;
        }
        private void ExpectPatternBracket(string text)
        {
            if (!TakePatternBracket(text)) throw Error("Expected pattern bracket " + text);
        }
        private CompilePattern CombinePatterns(List<CompilePattern> parts)
        {
            var ints = new HashSet<long>();
            var bools = new HashSet<bool>();
            var types = new HashSet<IrValueType>();
            foreach (var part in parts)
            {
                foreach (long v in part.Ints)
                    if (!ints.Add(v)) throw Error("PATTERN_DUPLICATE: duplicate integer alternative");
                foreach (bool v in part.Bools)
                    if (!bools.Add(v)) throw Error("PATTERN_DUPLICATE: duplicate boolean alternative");
                foreach (var t in part.Types)
                    if (!types.Add(t)) throw Error("PATTERN_DUPLICATE: duplicate canonical type alternative");
                if (ints.Count + bools.Count + types.Count > 64)
                    throw Error("PATTERN_LIMIT: at most 64 expanded alternatives");
            }
            if (ints.Count + bools.Count + types.Count == 0) throw Error("PATTERN_EMPTY: empty set");
            return new CompilePattern(ints.OrderBy(v => v).ToArray(), bools.OrderBy(v => v).ToArray(),
                types.OrderBy(v => v).ToArray());
        }
        private CompilePattern ReadPatternRef(bool array)
        {
            int at = Current.Offset;
            if (!patternConstants.TryGetValue(Current.Text, out var entry) || entry.Array != array)
                throw Error("PATTERN_TYPE: unknown, forward/cyclic or wrong-category Pattern reference");
            patternNameUses.Add(at);
            index++;
            return entry.Pattern;
        }
        private CompilePattern ReadPatternArray(int depth)
        {
            if (depth >= 32) throw Error("PATTERN_LIMIT: nesting limit");
            ExpectPatternBracket("[");
            var parts = new List<CompilePattern>();
            do
            {
                if (parts.Count >= 64) throw Error("PATTERN_LIMIT: collection size");
                parts.Add(ReadPatternDescriptor(depth + 1));
                if (TakePatternBracket("]")) break;
                Expect(",");
                if (TakePatternBracket("]")) break;
            } while (true);
            return CombinePatterns(parts);
        }
        private CompilePattern ReadPatternMembership(int depth)
        {
            if (depth >= 32) throw Error("PATTERN_LIMIT: nesting limit");
            if (Current.Text != "[") return ReadPatternRef(true);
            string next = tokens[index + 1].Text;
            if (next is "on" or "is" or "in" || patternConstants.ContainsKey(next))
                return ReadPatternArray(depth + 1);
            ExpectPatternBracket("[");
            var parts = new List<CompilePattern>();
            do
            {
                if (parts.Count >= 64) throw Error("PATTERN_LIMIT: collection size");
                long v = ReadMatchLiteral() ?? throw Error("PATTERN_TYPE: expected integer literal");
                parts.Add(new CompilePattern([v], [], []));
                if (TakePatternBracket("]")) break;
                Expect(",");
                if (TakePatternBracket("]")) break;
            } while (true);
            return CombinePatterns(parts);
        }
        private CompilePattern ReadPatternDescriptor(int depth)
        {
            if (depth >= 32) throw Error("PATTERN_LIMIT: nesting limit");
            if (Take("on"))
            {
                if (Take("true")) return new CompilePattern([], [true], []);
                if (Take("false")) return new CompilePattern([], [false], []);
                if (patternConstants.ContainsKey(Current.Text)) return ReadPatternRef(false);
                long v = ReadMatchLiteral() ?? throw Error("PATTERN_TYPE: expected constant int/bool pattern");
                return new CompilePattern([v], [], []);
            }
            if (Take("in")) return ReadPatternMembership(depth + 1);
            if (Take("is"))
            {
                bool list = Take("in");
                if (list && Current.Text != "[")
                {
                    var p = ReadPatternRef(true);
                    if (p.Ints.Length != 0 || p.Bools.Length != 0)
                        throw Error("PATTERN_TYPE: is in requires type-only Pattern[]");
                    return p;
                }
                if (list) ExpectPatternBracket("[");
                var parts = new List<CompilePattern>();
                do
                {
                    parts.Add(ReadPatternTypeAtom());
                    if (!list || TakePatternBracket("]")) break;
                    Expect(",");
                } while (true);
                return CombinePatterns(parts);
            }
            return ReadPatternRef(false);
        }
        private CompilePattern ReadPatternTypeAtom()
        {
            if (patternTypeAliases.TryGetValue(Current.Text, out var resolved))
            {
                patternAliasUses.Add(Current.Offset);
                index++;
                return resolved;
            }
            var type = Current.Text switch
            {
                "int" or "Int" => IrValueType.I64,
                "bool" or "boolean" => IrValueType.Bool,
                _ => throw Error("PATTERN_TYPE: unsupported, unknown or forward/cyclic scalar type")
            };
            index++;
            return new CompilePattern([], [], [type]);
        }
        private void ReadPatternTypeAlias()
        {
            Expect("type");
            int at = Current.Offset;
            string name = Name();
            if (name is "Pattern" or "Int" or "boolean" or "on" or "in" or "_" ||
                patternConstants.ContainsKey(name) || patternTypeAliases.ContainsKey(name) ||
                patternTypeAliases.Count >= 128)
                throw Error("PATTERN_NAME: duplicate, reserved or over-budget type alias");
            Expect("=");
            var parts = new List<CompilePattern>();
            do
            {
                if (parts.Count >= 64) throw Error("PATTERN_LIMIT: alias alternatives");
                parts.Add(ReadPatternTypeAtom());
            } while (Take("|"));
            Expect(";");
            patternTypeAliases.Add(name, CombinePatterns(parts));
            patternAliasUses.Add(at);
        }
        private void ReadPatternConstant()
        {
            Expect("const"); Expect("Pattern");
            bool array = TakePatternBracket("[");
            if (array) ExpectPatternBracket("]");
            int at = Current.Offset;
            string name = Name();
            if (name is "Pattern" or "Int" or "boolean" or "on" or "in" or "_" ||
                patternConstants.ContainsKey(name) || patternTypeAliases.ContainsKey(name) || patternConstants.Count >= 128)
                throw Error("PATTERN_NAME: duplicate, reserved or over-budget declaration");
            Expect("=");
            var p = array ? ReadPatternArray(0) : ReadPatternDescriptor(0);
            Expect(";");
            patternConstants.Add(name, (array, p));
            patternNameUses.Add(at);
        }
        private (MatchTags, IReadOnlyList<long>?) ResolveStoredPattern(CompilePattern p, IrValueType subject)
        {
            if (p.Types.Contains(subject)) return (MatchTags.Any, null);
            if (subject == IrValueType.I64 && p.Ints.Length != 0)
                return (0, p.Ints);
            if (subject == IrValueType.Bool && p.Bools.Length != 0)
            {
                MatchTags tags = 0;
                foreach (bool b in p.Bools) tags |= b ? MatchTags.Positive : MatchTags.Negative;
                return (tags, null);
            }
            throw Error("PATTERN_TYPE: no alternative matches subject");
        }

        private long? ReadMatchLiteral()
        {
            bool negative = Take("-");
            var token = Current;
            if (!negative && (token.Text.Length == 0 || !Digit(token.Text[0]))) return null;
            string digits = token.Text.Replace("_", "", StringComparison.Ordinal);
            string literal = negative ? "-" + digits : digits;
            if (!long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                throw Error("Match literal is outside signed int range");
            index++;
            return value;
        }

        private FormatException MatchDefaultError() =>
            Error("MATCH_DEFAULT_BACKEND_UNSUPPORTED: use 'on _' for a fallback arm");

        private (MatchTags Tags, string? Binding, IrValueType? BindingType) ReadMatchPattern(IrValueType subject)
        {
            if (Current.Text == "default") throw MatchDefaultError();
            if (Take("_")) return (MatchTags.Any, null, null);
            if (subject == IrValueType.Bool)
            {
                if (Take("true")) return (MatchTags.Positive, null, null);
                if (Take("false")) return (MatchTags.Negative, null, null);
            }
            if (subject == IrValueType.OptionI64)
            {
                if (Take("None")) return (MatchTags.Negative, null, null);
                if (Take("Some")) return ReadMatchPayload(MatchTags.Positive, IrValueType.I64);
            }
            if (subject.IsResult())
            {
                if (Take("Ok")) return ReadMatchPayload(MatchTags.Positive, subject.Payload());
                if (Take("Err"))
                    return ReadMatchPayload(MatchTags.Negative, subject switch
                    {
                        IrValueType.ResultI64Text => IrValueType.GuestText,
                        IrValueType.ResultI64TextOrError => null,
                        _ => IrValueType.GuestError
                    }, allowEmpty: true);
            }
            // Only constructor names have payloads. A bare ordinary identifier
            // is an irrefutable binding for the complete, already checked value.
            if (Current.Text is "Some" or "None" or "Ok" or "Err" or "true" or "false" or "structural" or "on" or "when")
                throw Error("MATCH_PATTERN_TYPE: pattern does not match the scrutinee type");
            return (MatchTags.Any, Name(), subject);
        }

        private (MatchTags Tags, string? Binding, IrValueType? BindingType) ReadMatchPayload(
            MatchTags tags, IrValueType? type, bool allowEmpty = false)
        {
            Expect("(");
            if (allowEmpty && Take(")")) return (tags, null, null);
            if (Take("_"))
            {
                Expect(")");
                return (tags, null, null);
            }
            if (type is null) throw Error("MATCH_UNION_BINDER_UNSUPPORTED: the Err union needs a typed union ABI");
            string binding = Name();
            Expect(")");
            return (tags, binding, type);
        }
    }
}
