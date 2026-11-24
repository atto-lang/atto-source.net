namespace Ores.Net.Compiler;

[Flags]
internal enum MatchTags { Negative = 1, Positive = 2, Any = Negative | Positive }

internal sealed record StructuredMatchArm(MatchTags Tags, string? Binding, IrValueType? BindingType,
    StructuredExpr? Guard, IReadOnlyList<StructuredStmt>? Body, StructuredExpr? Value);
internal sealed record StructuredMatch(StructuredExpr Subject, IReadOnlyList<StructuredMatchArm> Arms,
    IrValueType Type, bool Completes);
internal sealed record StructuredDoMatch(int At, StructuredMatch Match) : StructuredStmt(At);
internal sealed record StructuredMatchValue(int At, StructuredMatch Match) : StructuredExpr(At, Match.Type);
internal sealed record StructuredMatchExit(int At) : StructuredStmt(At);

public static partial class OresStructuredParser
{
    private sealed partial class Reader
    {
        private int doMatchDepth;

        private StructuredExpr ReadMatchValue(int offset, int depth)
        {
            var match = ReadMatch(depth + 1, imperative: false);
            var children = new List<StructuredExpr> { match.Subject };
            foreach (var arm in match.Arms)
            {
                if (arm.Guard is { } guard) children.Add(guard);
                children.Add(arm.Value!);
            }
            return TaggedPostfix(TypedNode(new StructuredMatchValue(offset, match), children.ToArray()), depth);
        }

        private StructuredMatch ReadMatch(int depth, bool imperative)
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
                var subject = Expr(depth + 1);
                if (subject.Type is not (IrValueType.Bool or IrValueType.OptionI64) && !subject.Type.IsResult())
                    throw Error("MATCH_DEFAULT_BACKEND_UNSUPPORTED: match requires bool, Option<int> or a closed Result");
                Expect("over");
                string end = Take("{") ? "}" : "end";
                var arms = new List<StructuredMatchArm>();
                MatchTags uncovered = MatchTags.Any;
                bool completes = !imperative;
                while (Take("on"))
                {
                    if (++statements > MaxStatements) throw Error("Structured match statement budget exceeded");
                    var (tags, binding, bindingType) = ReadMatchPattern(subject.Type);
                    if ((tags & uncovered) == 0)
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
                        var guard = Take("when") ? Expr(depth + 1) : null;
                        if (guard is not null) Require(guard, IrValueType.Bool, "Match guard must be bool");
                        else uncovered &= ~tags;
                        bool arrow = Take("->");
                        if (imperative)
                        {
                            doMatchDepth++;
                            try
                            {
                                var (body, terminal) = Block(depth + 1);
                                completes |= !terminal || HasArmExit(body);
                                arms.Add(new StructuredMatchArm(tags, binding, bindingType, guard, body, null));
                            }
                            finally { doMatchDepth--; }
                        }
                        else
                        {
                            bool braced = Take("{");
                            if (braced && !Take("return") && !Take("^"))
                                throw Error("MATCH_ARM_RETURN_REQUIRED: a value match block requires return or ^");
                            if (!braced && !arrow) throw Error("Expected a match arm block or '->'");
                            var value = (result ?? context) is { } expected
                                ? ExpectedExpr(depth + 1, expected) : Expr(depth + 1);
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
                            arms.Add(new StructuredMatchArm(tags, binding, bindingType, guard, null, value));
                        }
                    }
                    finally { scopes.RemoveAt(scopes.Count - 1); }
                }
                if (uncovered != 0)
                    throw Error("MATCH_NON_EXHAUSTIVE: cover all tags with unguarded arms or add 'on _'");
                Expect(end);
                return new StructuredMatch(subject, arms.AsReadOnly(), result!.Value, completes);
            }
            finally { expectedExpressionType = context; }
        }

        private static bool HasArmExit(IReadOnlyList<StructuredStmt> body) => body.Any(statement =>
            statement switch
            {
                StructuredMatchExit => true,
                StructuredIf conditional => HasArmExit(conditional.Then) ||
                    conditional.Else is { } otherwise && HasArmExit(otherwise),
                StructuredWhile loop => HasArmExit(loop.Body),
                _ => false
            });

        private (MatchTags Tags, string? Binding, IrValueType? BindingType) ReadMatchPattern(IrValueType subject)
        {
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
                    });
            }
            // Only constructor names have payloads. A bare ordinary identifier
            // is an irrefutable binding for the complete, already checked value.
            if (Current.Text is "Some" or "None" or "Ok" or "Err" or "true" or "false" or "structural" or "on" or "when")
                throw Error("MATCH_PATTERN_TYPE: pattern does not match the scrutinee type");
            return (MatchTags.Any, Name(), subject);
        }

        private (MatchTags Tags, string? Binding, IrValueType? BindingType) ReadMatchPayload(
            MatchTags tags, IrValueType? type)
        {
            Expect("(");
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
