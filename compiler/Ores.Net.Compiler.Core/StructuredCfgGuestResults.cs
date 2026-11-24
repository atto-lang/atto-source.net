namespace Ores.Net.Compiler;

public static partial class OresStructuredCfgLowering
{
    private sealed partial class Builder
    {
        private int ConstructResult(StructuredResult result, ref Block block,
            Dictionary<string, Binding> variables)
        {
            if (result.Success && result.Type == IrValueType.ResultOptionI64)
            {
                var (tag, payload) = OptionParts(result.Value, ref block, variables);
                return Emit(block, CfgOpcode.ResultOkOption, result.Type, left: tag, right: payload);
            }
            int value = Expression(result.Value, ref block, variables);
            var opcode = result.Success ? CfgOpcode.ResultOk : result.Value.Type == IrValueType.GuestText
                ? CfgOpcode.ResultErrText : CfgOpcode.ResultErr;
            return Emit(block, opcode,
                result.Type, left: value);
        }

        private (int Tag, int Payload) UnwrapResultOption(int result, Block block) =>
            (Emit(block, CfgOpcode.ResultUnwrapOptionTag, IrValueType.Bool, left: result),
             Emit(block, CfgOpcode.ResultUnwrapOptionPayload, IrValueType.I64, left: result));

        private int Trap(StructuredTrap trap, ref Block block, Dictionary<string, Binding> variables)
        {
            if (outlineTrap is null) throw new FormatException("Result trap requires the verified module ABI");

            var captures = new SortedSet<string>(StringComparer.Ordinal);
            CollectReads(trap.Operand, captures);
            var parameters = new List<StructuredParameter>();
            var arguments = new List<int>();
            foreach (string name in captures)
            {
                if (!variables.TryGetValue(name, out var binding) || binding.Type == IrValueType.FixedArrayI64)
                    throw new FormatException("Trap operand capture requires a verified scalar or tagged layout");
                parameters.Add(new StructuredParameter(name, binding.Type));
                arguments.Add(binding.ValueId);
                if (binding.PayloadId is int payload) arguments.Add(payload);
            }
            // Only already-defined SSA reads are passed outside the trap. The
            // generated method evaluates the whole operand, including any
            // source arguments or short-circuit branches, exactly once.
            string method = outlineTrap(trap.Operand, parameters.AsReadOnly());
            return Emit(block, CfgOpcode.TrapCall, trap.Type, target: method, arguments: arguments.ToArray());
        }

        private static void CollectReads(StructuredExpr expression, ISet<string> captures)
        {
            if (expression is StructuredRead read)
            {
                captures.Add(read.Name);
                return;
            }
            IEnumerable<StructuredExpr> children = expression switch
            {
                StructuredBinary binary => [binary.Left, binary.Right],
                StructuredNegate negate => [negate.Value],
                StructuredConditional choice => [choice.Test, choice.WhenTrue, choice.WhenFalse],
                StructuredCall call => call.Arguments,
                StructuredSome some => [some.Payload],
                StructuredOptionPresent present => [present.Option],
                StructuredOptionAbsent absent => [absent.Option],
                StructuredOptionEqual equal => [equal.LeftOption, equal.RightOption],
                StructuredOptionOr or => [or.Option, or.Fallback],
                StructuredOptionUnwrap unwrap => [unwrap.Option],
                StructuredFixedArray literal => literal.Elements,
                StructuredArrayGet get => [get.Array, get.Index],
                StructuredErrorTest test => [test.Error],
                StructuredResult result => [result.Value],
                StructuredTrap trap => [trap.Operand],
                StructuredResultInspect { Fallback: { } fallback } result => [result.Result, fallback],
                StructuredResultInspect result => [result.Result],
                StructuredBool or StructuredInt or StructuredNone or StructuredGuestError or StructuredGuestText => [],
                _ => throw new FormatException("Trap operand requires independent, local evaluation")
            };
            foreach (var child in children) CollectReads(child, captures);
        }

        private int PropagateResult(StructuredExpr operand, ref Block block, Dictionary<string, Binding> variables)
        {
            int result = Expression(operand, ref block, variables);
            int ok = Emit(block, CfgOpcode.ResultIsOk, IrValueType.Bool, left: result);
            var success = NewBlock();
            var failure = NewBlock();
            block.Terminator = new CfgBranch(ok, success.Name, [], failure.Name, []);
            failure.Terminator = new CfgReturn(result);
            block = success;
            return result;
        }

        private int PropagateOption(StructuredExpr operand, ref Block block, Dictionary<string, Binding> variables)
        {
            var (tag, payload) = OptionParts(operand, ref block, variables);
            var success = NewBlock();
            var failure = NewBlock();
            block.Terminator = new CfgBranch(tag, success.Name, [], failure.Name, []);
            int none = Emit(failure, CfgOpcode.PackOptionI64, IrValueType.OptionI64,
                left: tag, right: payload);
            failure.Terminator = new CfgReturn(none);
            block = success;
            return payload;
        }
    }
}
