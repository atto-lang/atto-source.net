namespace Ores.Net.Compiler;

public static partial class OresStructuredCfgLowering
{
    private sealed partial class Builder
    {
        private int MatchValue(StructuredMatchValue expression, ref Block block,
            Dictionary<string, Binding> variables)
        {
            var (join, value) = LowerMatch(expression.Match, block, variables);
            block = join ?? throw new FormatException("Value match has no continuation");
            return value;
        }

        private (Block? Join, int Value) LowerMatch(StructuredMatch match, Block block,
            Dictionary<string, Binding> variables)
        {
            Binding subject;
            int tag;
            if (match.Subject.Type == IrValueType.OptionI64)
            {
                var parts = OptionParts(match.Subject, ref block, variables);
                subject = new Binding(IrValueType.OptionI64, parts.Tag, parts.Payload);
                tag = parts.Tag;
            }
            else
            {
                int value = Expression(match.Subject, ref block, variables);
                subject = new Binding(match.Subject.Type, value);
                tag = !subject.Type.IsResult() ? value
                    : Emit(block, CfgOpcode.ResultIsOk, IrValueType.Bool, left: value);
            }

            var continuation = new LexicalContinuation(this, variables, match.Type,
                writeBack: match.Type == IrValueType.Void ||
                    match.Arms.Any(arm => arm.Value is StructuredSyncTaskValue { Task.WriteBack: true }));

            MatchTags remaining = MatchTags.Any;
            foreach (var arm in match.Arms)
            {
                bool testsPattern = arm.Literal is not null || (arm.Tags & remaining) != remaining;
                Block? next = testsPattern || arm.Guard is not null ? NewBlock() : null;
                Block selected = block;
                if (testsPattern)
                {
                    selected = NewBlock();
                    int condition = tag;
                    if (arm.Literal is long literal)
                    {
                        int constant = Emit(block, CfgOpcode.ConstantI64, IrValueType.I64, value: literal);
                        condition = Emit(block, CfgOpcode.EqualI64, IrValueType.Bool, left: subject.ValueId, right: constant);
                    }
                    bool inverted = arm.Tags == MatchTags.Negative;
                    block.Terminator = new CfgBranch(condition, inverted ? next!.Name : selected.Name, [],
                        inverted ? selected.Name : next!.Name, []);
                }

                var environment = new Dictionary<string, Binding>(variables, StringComparer.Ordinal);
                if (arm.Binding is { } binder)
                    environment.Add(binder, MatchBinding(arm, subject, selected));

                if (arm.Guard is { } guard)
                {
                    int success = Expression(guard, ref selected, environment);
                    var guardedBody = NewBlock();
                    selected.Terminator = new CfgBranch(success, guardedBody.Name, [], next!.Name, []);
                    selected = guardedBody;
                }
                else remaining &= ~arm.Tags;

                if (arm.Value is { } expression)
                {
                    int value = PackedValue(expression, ref selected, environment);
                    continuation.Finish(selected, environment, value);
                }
                else
                {
                    var end = EmitStatements(arm.Body!, selected, environment, continuation.Finish);
                    if (end is not null) continuation.Finish(end, environment, null);
                }

                if (next is not null) block = next;
            }
            if (remaining != 0) throw new FormatException("Missing verified exhaustive match witness");
            return continuation.Complete();
        }

        private Binding MatchBinding(StructuredMatchArm arm, Binding subject, Block block)
        {
            if (arm.WholeAlias || arm.Tags == MatchTags.Any) return subject;
            if (subject.Type == IrValueType.OptionI64)
                return new Binding(IrValueType.I64, subject.PayloadId!.Value);
            if (arm.BindingType == IrValueType.OptionI64)
            {
                var (tag, payload) = UnwrapResultOption(subject.ValueId, block);
                return new Binding(IrValueType.OptionI64, tag, payload);
            }
            var operation = arm.Tags == MatchTags.Positive ? CfgOpcode.ResultUnwrap
                : arm.BindingType == IrValueType.GuestText ? CfgOpcode.ResultUnwrapTextErr : CfgOpcode.ResultUnwrapErr;
            IrValueType type = arm.BindingType!.Value;
            return new Binding(type, Emit(block, operation, type, left: subject.ValueId));
        }
    }
}
