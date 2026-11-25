namespace Ores.Net.Compiler;

public static partial class OresStructuredCfgLowering
{
    private sealed partial class Builder
    {
        private sealed class LexicalContinuation(Builder builder, Dictionary<string, Binding> variables,
            IrValueType resultType, bool writeBack)
        {
            private readonly string[] names = writeBack ? Names(variables) : [];
            private readonly Dictionary<string, Binding> joined = new(StringComparer.Ordinal);
            private Block? join;
            private int result = -1;
            private int optionPayload = -1;

            public void Finish(Block end, Dictionary<string, Binding> environment, int? value)
            {
                if ((resultType == IrValueType.Void) != (value is null))
                    throw new FormatException("Lexical return does not match scope result type");
                if (join is null)
                {
                    join = builder.Join(names, variables, joined);
                    if (resultType != IrValueType.Void)
                    {
                        result = builder.NextValue();
                        if (resultType == IrValueType.OptionI64)
                        {
                            optionPayload = builder.NextValue();
                            join.Parameters.Add(new CfgParameter(result, IrValueType.Bool));
                            join.Parameters.Add(new CfgParameter(optionPayload, IrValueType.I64));
                        }
                        else join.Parameters.Add(new CfgParameter(result, resultType));
                    }
                }
                var arguments = Values(names, environment);
                if (resultType == IrValueType.OptionI64)
                {
                    int tag = builder.Emit(end, CfgOpcode.ReadOptionI64Tag, IrValueType.Bool, left: value);
                    int payload = builder.Emit(end, CfgOpcode.ReadOptionI64Payload, IrValueType.I64, left: value);
                    arguments = [..arguments, tag, payload];
                }
                else if (value is int id) arguments = [..arguments, id];
                end.Terminator = new CfgJump(join.Name, arguments);
            }

            public (Block? Join, int Value) Complete()
            {
                foreach (var (name, binding) in joined) variables[name] = binding;
                if (join is not null && resultType == IrValueType.OptionI64)
                    result = builder.Emit(join, CfgOpcode.PackOptionI64, IrValueType.OptionI64,
                        left: result, right: optionPayload);
                return (join, result);
            }
        }

        private int PackedValue(StructuredExpr expression, ref Block block,
            Dictionary<string, Binding> variables)
        {
            if (expression.Type != IrValueType.OptionI64) return Expression(expression, ref block, variables);
            var (tag, payload) = OptionParts(expression, ref block, variables);
            return Emit(block, CfgOpcode.PackOptionI64, IrValueType.OptionI64, left: tag, right: payload);
        }

        private int InlineTaskValue(StructuredSyncTaskValue expression, ref Block block,
            Dictionary<string, Binding> variables)
        {
            var (join, value) = LowerInlineTask(expression.Task, block, variables);
            block = join ?? throw new FormatException("Value-context sync task has no continuation");
            return value;
        }

        private (Block? Join, int Value) LowerInlineTask(StructuredInlineTask task, Block block,
            Dictionary<string, Binding> variables)
        {
            // The task is part of this very CFG and uses ordinary lexical
            // branches. There is no task object, call boundary or dispatch.
            var continuation = new LexicalContinuation(this, variables, task.Type,
                writeBack: task.WriteBack);
            var environment = new Dictionary<string, Binding>(variables, StringComparer.Ordinal);
            var end = EmitStatements(task.Body, block, environment, continuation.Finish);
            if (end is not null) continuation.Finish(end, environment, null);
            return continuation.Complete();
        }
    }
}
