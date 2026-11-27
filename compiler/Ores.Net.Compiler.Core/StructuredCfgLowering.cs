namespace Ores.Net.Compiler;

/// <summary>
/// Experimental, opt-in lowering of bounded typed structured source into
/// independently verified CFGs. Legacy Lower() inlines helper calls for
/// differential tests; LowerModule() retains independent CIL call boundaries.
/// A local is represented by its current SSA value, never a mutable IL slot.
/// </summary>
public static partial class OresStructuredCfgLowering
{
    public static VerifiedCfgFunction Lower(string source)
    {
        var parsed = OresStructuredParser.Parse(source);
        return new Builder().Lower(parsed);
    }

    // Explicit conditional-only strict admission, retaining the same verified CFG builder.
    public static VerifiedCfgFunction LowerStrictConditionals(string source)
        => new Builder().Lower(OresStructuredParser.ParseStrictConditionals(source));

    // ABI path: helpers become independent verified CFG methods. The old
    // Lower() method keeps the deliberately bounded inlining/reference path.
    public static VerifiedCfgModule LowerModule(string source)
        => LowerModule(OresStructuredParser.Parse(source));

    public static VerifiedCfgModule? TryLowerGuardedModule(string source)
        => OresStructuredParser.HasGuardToken(source)
            ? LowerModule(OresStructuredParser.ParseProduction(source))
            : null;

    public static VerifiedCfgModule? TryLowerProductionModule(string source, bool allowStdout = false)
        => OresStructuredParser.HasStructuredFeatureToken(source)
            ? LowerModule(OresStructuredParser.ParseProduction(source, allowStdout))
            : null;

    internal static VerifiedCfgModule LowerModule(StructuredProgram program)
    {
        var helperSignatures = new Dictionary<string, CfgCallSignature>(StringComparer.Ordinal);
        var methods = new List<VerifiedCfgFunction>();
        var declarations = program.Functions ??
            throw new FormatException("Missing structured function declaration table");
        var order = program.FunctionOrder ??
            throw new FormatException("Missing structured function order witness");
        if (order.Count != declarations.Count || order.Count > 64)
            throw new FormatException("Invalid structured function order witness");

        int nextTrap = 0;
        string OutlineTrap(StructuredExpr operand, IReadOnlyList<StructuredParameter> parameters)
        {
            string name;
            do { name = "_atto_trap_" + nextTrap++; } while (declarations.ContainsKey(name));
            var resultType = GuestResultTypes.Wrap(operand.Type);
            var result = operand.Type == IrValueType.ResultI64Text
                ? new StructuredResultWiden(operand.Offset, operand)
                : operand.Type.IsResult() ? operand
                : new StructuredResult(operand.Offset, operand, Success: true, resultType);
            var function = new StructuredFunction(name, parameters, resultType, result,
                new StructuredStmt[] { new StructuredReturn(operand.Offset, result) });
            AddHelper(function);
            return name;
        }

        void AddHelper(StructuredFunction helper)
        {
            var verified = new Builder(emitCalls: true, OutlineTrap)
                .LowerHelper(helper, declarations, helperSignatures);
            if (methods.Count >= 64)
                throw new FormatException("Structured helper and trap method budget exceeded");
            methods.Add(verified);
            helperSignatures.Add(helper.Name, new CfgCallSignature(helper.ResultType, verified.Parameters));
        }

        foreach (string name in order)
        {
            if (!declarations.TryGetValue(name, out var helper) ||
                helper.Name != name || helperSignatures.ContainsKey(name))
                throw new FormatException("Invalid or duplicate ordered helper function");
            // The method order is the exact source order, NOT the incidental
            // iteration order of a dictionary. A helper may refer only to
            // earlier, already verified signatures.
            AddHelper(helper);
        }
        var main = new Builder(emitCalls: true, OutlineTrap).Lower(program, helperSignatures);
        return new VerifiedCfgModule(main, methods.AsReadOnly());
    }

    // A closed Option<int> is represented by two separately verified SSA IDs.
    // Its tag occupies ValueId (Bool), payload occupies PayloadId (I64).
    private readonly record struct Binding(IrValueType Type, int ValueId,
        int? PayloadId = null, IReadOnlyList<int>? Elements = null);

    private sealed class Block(string name)
    {
        public string Name { get; } = name;
        public List<CfgParameter> Parameters { get; } = new();
        public List<CfgInstruction> Instructions { get; } = new();
        public CfgTerminator? Terminator { get; set; }
        public string? OverflowTarget { get; set; }

        public CfgBlock Freeze() => new(Name, Parameters.ToArray(),
            Instructions.ToArray(), Terminator ??
            throw new FormatException("Unterminated structured CFG block"),
            OverflowTarget);
    }

    private sealed partial class Builder(bool emitCalls = false,
        Func<StructuredExpr, IReadOnlyList<StructuredParameter>, string>? outlineTrap = null)
    {
        private readonly List<Block> blocks = new();
        private IReadOnlyDictionary<string, StructuredFunction> functions =
            new Dictionary<string, StructuredFunction>(StringComparer.Ordinal);
        private int nextValue;
        private IReadOnlyDictionary<string, CfgCallSignature>? callSignatures;
        private sealed record LoopTarget(Block Header, Block Exit, string[] Names);
        private readonly Stack<LoopTarget> loopTargets = new();

        public VerifiedCfgFunction LowerHelper(StructuredFunction helper,
            IReadOnlyDictionary<string, StructuredFunction> declarations,
            IReadOnlyDictionary<string, CfgCallSignature> signatures)
        {
            functions = declarations;
            callSignatures = signatures;
            var entry = NewBlock();
            var parameters = new Dictionary<string, Binding>(StringComparer.Ordinal);
            var slots = new List<IrValueType>();
            int Argument(IrValueType type)
            {
                int id = Emit(entry, CfgOpcode.Argument, type, value: slots.Count);
                slots.Add(type);
                return id;
            }
            foreach (var parameter in helper.Parameters)
            {
                var value = parameter.Type == IrValueType.OptionI64
                    ? new Binding(parameter.Type, Argument(IrValueType.Bool), Argument(IrValueType.I64))
                    : new Binding(parameter.Type, Argument(parameter.Type));
                parameters.Add(parameter.Name, value);
            }
            // Parameter values are defined once by ldarg in the entry block.
            // Branch, loop and local scope semantics reuse the verified main
            // builder, but always retain a distinct MethodDef/SSA namespace.
            if (EmitStatements(helper.Body, entry, parameters) is not null)
                throw new FormatException("Typed helper has unterminated return path");
            return OresCfgVerifier.Verify(new CfgFunction(helper.Name, helper.ResultType,
                entry.Name, blocks.Select(b => b.Freeze()).ToArray(),
                slots), signatures);
        }

        public VerifiedCfgFunction Lower(StructuredProgram program,
            IReadOnlyDictionary<string, CfgCallSignature>? signatures = null)
        {
            if (program.Name != "main")
                throw new FormatException("Only typed main is supported");
            functions = program.Functions ??
                new Dictionary<string, StructuredFunction>(StringComparer.Ordinal);
            callSignatures = signatures;
            var entry = NewBlock();
            var environment = new Dictionary<string, Binding>(StringComparer.Ordinal);
            if (EmitStatements(program.Body, entry, environment) is not null)
                throw new FormatException("Structured CFG has an unterminated return path");
            return OresCfgVerifier.Verify(new CfgFunction(program.Name,
                IrValueType.I64, entry.Name, blocks.Select(b => b.Freeze()).ToArray()),
                signatures);
        }

        private int NextValue()
        {
            if (nextValue >= OresCfgVerifier.MaxValues)
                throw new FormatException("Structured CFG SSA budget exceeded");
            return nextValue++;
        }

        private Block NewBlock()
        {
            if (blocks.Count >= OresCfgVerifier.MaxBlocks)
                throw new FormatException("Structured CFG block budget exceeded");
            var block = new Block(blocks.Count == 0 ? "entry" : "b" + blocks.Count);
            blocks.Add(block);
            return block;
        }

        private int Emit(Block block, CfgOpcode opcode, IrValueType type,
            int? left = null, int? right = null,
            long? value = null, bool? boolean = null,
            string? target = null, IReadOnlyList<int>? arguments = null, string? text = null)
        {
            int id = NextValue();
            block.Instructions.Add(new CfgInstruction(id, opcode, type,
                Left: left, Right: right, I64Constant: value, BoolConstant: boolean,
                Target: target, Arguments: arguments, TextOperand: text));
            return id;
        }

        // Each fixed-array element is its own previously verified SSA i64.
        // This prevents guest pointers, unchecked native loads, aliases,
        // resize or managed CLR arrays in the first immutable value slice.
        private IReadOnlyList<int> ArrayElements(StructuredExpr array, ref Block block,
            Dictionary<string, Binding> variables)
        {
            switch (array)
            {
                case StructuredFixedArray literal:
                {
                    if (literal.Elements.Count > OresCfgVerifier.MaxFixedArrayElements)
                        throw new FormatException("E_ARRAY_SIZE: fixed array exceeds compiler limit");
                    var values = new int[literal.Elements.Count];
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (literal.Elements[i].Type != IrValueType.I64)
                            throw new FormatException("E_ARRAY_TYPE: expected int value");
                        values[i] = Expression(literal.Elements[i], ref block, variables);
                    }
                    return Array.AsReadOnly(values);
                }
                case StructuredRead read when read.Type == IrValueType.FixedArrayI64:
                {
                    if (!variables.TryGetValue(read.Name, out var binding) ||
                        binding.Type != IrValueType.FixedArrayI64 ||
                        binding.Elements is null ||
                        binding.Elements.Count > OresCfgVerifier.MaxFixedArrayElements)
                        throw new FormatException("E_ARRAY_OWNER: missing immutable fixed array SSA snapshot");
                    return binding.Elements;
                }
                default:
                    throw new FormatException("E_ARRAY_I64_ONLY: unsupported array expression");
            }
        }

        // Explicit tagged Option<int> representation: no all-bits-value
        // sentinel, nullable managed heap object or implicit flattening.
        private (int Tag, int Payload) OptionParts(StructuredExpr option, ref Block block,
            Dictionary<string, Binding> variables)
        {
            switch (option)
            {
                case StructuredSome some:
                {
                    int payload = Expression(some.Payload, ref block, variables);
                    int tag = Emit(block, CfgOpcode.ConstantBool, IrValueType.Bool,
                        boolean: true);
                    return (tag, payload);
                }
                case StructuredNone:
                {
                    int tag = Emit(block, CfgOpcode.ConstantBool, IrValueType.Bool,
                        boolean: false);
                    int payload = Emit(block, CfgOpcode.ConstantI64, IrValueType.I64,
                        value: 0);
                    return (tag, payload);
                }
                case StructuredArrayGet get:
                {
                    // Evaluate the array elements first, then index once.
                    // Every element is a value, never an address/loan.
                    var elements = ArrayElements(get.Array, ref block, variables);
                    if (get.Index.Type != IrValueType.I64)
                        throw new FormatException("E_ARRAY_INDEX_TYPE: expected int index");
                    int index = Expression(get.Index, ref block, variables);
                    int tag = Emit(block, CfgOpcode.ArrayIndexValidI64,
                        IrValueType.Bool, left: index, value: elements.Count);
                    int selected = Emit(block, CfgOpcode.ConstantI64,
                        IrValueType.I64, value: 0);
                    for (int i = 0; i < elements.Count; i++)
                    {
                        int candidate = Emit(block, CfgOpcode.ConstantI64,
                            IrValueType.I64, value: i);
                        int equal = Emit(block, CfgOpcode.EqualI64,
                            IrValueType.Bool, left: index, right: candidate);
                        selected = Emit(block, CfgOpcode.OptionOrI64,
                            IrValueType.I64, left: equal, right: elements[i],
                            arguments: new[] { selected });
                    }
                    return (tag, selected);
                }
                case StructuredRead read when read.Type == IrValueType.OptionI64:
                {
                    if (!variables.TryGetValue(read.Name, out var value) ||
                        value.Type != IrValueType.OptionI64 || !value.PayloadId.HasValue)
                        throw new FormatException("Unbound or unverified Option<int> local");
                    return (value.ValueId, value.PayloadId.Value);
                }
                case StructuredCall call when call.Type == IrValueType.OptionI64:
                {
                    var (function, parameters, slots) = PrepareCall(call, ref block, variables);
                    if (!emitCalls)
                        return OptionParts(InlineReturn(function), ref block, parameters);

                    int result = Emit(block, CfgOpcode.Call, call.Type,
                        target: call.Name, arguments: slots);
                    int tag = Emit(block, CfgOpcode.ReadOptionI64Tag, IrValueType.Bool, left: result);
                    int payload = Emit(block, CfgOpcode.ReadOptionI64Payload, IrValueType.I64, left: result);
                    return (tag, payload);
                }
                case StructuredMatchValue match when match.Type == IrValueType.OptionI64:
                {
                    int value = MatchValue(match, ref block, variables);
                    return (Emit(block, CfgOpcode.ReadOptionI64Tag, IrValueType.Bool, left: value),
                        Emit(block, CfgOpcode.ReadOptionI64Payload, IrValueType.I64, left: value));
                }
                case StructuredConditional conditional when conditional.Type == IrValueType.OptionI64 &&
                    HasConditionalWriteback(conditional):
                {
                    int value = ConditionalWritebackValue(conditional, ref block, variables);
                    return (Emit(block, CfgOpcode.ReadOptionI64Tag, IrValueType.Bool, left: value),
                        Emit(block, CfgOpcode.ReadOptionI64Payload, IrValueType.I64, left: value));
                }
                case StructuredSyncTaskValue task when task.Type == IrValueType.OptionI64:
                {
                    int value = InlineTaskValue(task, ref block, variables);
                    return (Emit(block, CfgOpcode.ReadOptionI64Tag, IrValueType.Bool, left: value),
                        Emit(block, CfgOpcode.ReadOptionI64Payload, IrValueType.I64, left: value));
                }
                case StructuredResultInspect result when result.ValueType == IrValueType.OptionI64:
                    return UnwrapResultOption(Expression(result.Result, ref block, variables), block);
                case StructuredPropagate propagation when propagation.Type == IrValueType.OptionI64:
                {
                    int result = PropagateResult(propagation.Operand, ref block, variables);
                    return UnwrapResultOption(result, block);
                }
                default:
                    throw new FormatException(
                        "E_OPTION_I64_ONLY: unverified Option<int> expression");
            }
        }

        private (StructuredFunction Function, Dictionary<string, Binding> Bindings, int[] Slots)
            PrepareCall(StructuredCall call, ref Block block, Dictionary<string, Binding> variables)
        {
            if (!functions.TryGetValue(call.Name, out var function) ||
                function.ResultType != call.Type || function.Parameters.Count != call.Arguments.Count ||
                (emitCalls && (callSignatures is null || !callSignatures.ContainsKey(call.Name))))
                throw new FormatException("Unverified structured helper call");

            var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
            var slots = new List<int>();
            // Evaluate each source operand once before starting the next one.
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                var parameter = function.Parameters[i];
                var argument = call.Arguments[i];
                if (parameter.Type != argument.Type)
                    throw new FormatException("Unverified structured call argument type");
                Binding binding;
                if (argument.Type == IrValueType.OptionI64)
                {
                    var parts = OptionParts(argument, ref block, variables);
                    binding = new Binding(argument.Type, parts.Tag, parts.Payload);
                }
                else
                    binding = new Binding(argument.Type, Expression(argument, ref block, variables));

                if (emitCalls)
                {
                    slots.Add(binding.ValueId);
                    if (binding.PayloadId is int payload) slots.Add(payload);
                }
                else bindings.Add(parameter.Name, binding);
            }
            return (function, bindings, slots.ToArray());
        }

        private static StructuredExpr InlineReturn(StructuredFunction function) =>
            function.ReturnValue ?? throw new FormatException(
                "Statement-bodied helper requires the independent MethodDef ABI");

        private int Expression(StructuredExpr expression, ref Block block,
            Dictionary<string, Binding> variables)
        {
            switch (expression)
            {
                case StructuredInt number:
                    return Emit(block, CfgOpcode.ConstantI64, IrValueType.I64,
                        value: number.Value);
                case StructuredBool boolean:
                    return Emit(block, CfgOpcode.ConstantBool, IrValueType.Bool,
                        boolean: boolean.Value);
                case StructuredGuestError error:
                    return Emit(block, CfgOpcode.GuestErrorLiteral, error.Type, text: error.Message);
                case StructuredGuestText literal:
                    return Emit(block, CfgOpcode.GuestTextLiteral, literal.Type, text: literal.Text);
                case StructuredResultWiden widen:
                {
                    int operand = Expression(widen.Operand, ref block, variables);
                    return Emit(block, CfgOpcode.ResultWiden, widen.Type, left: operand);
                }
                case StructuredErrorTest test:
                {
                    int error = Expression(test.Error, ref block, variables);
                    return Emit(block, test.IsType ? CfgOpcode.GuestErrorIsType : CfgOpcode.GuestErrorHasMessage,
                        IrValueType.Bool, left: error, text: test.Name);
                }
                case StructuredResult result:
                    return ConstructResult(result, ref block, variables);
                case StructuredTrap trap:
                    return Trap(trap, ref block, variables);
                case StructuredResultInspect selected:
                {
                    int result = Expression(selected.Result, ref block, variables);
                    int? fallback = selected.Fallback is null ? null : Expression(selected.Fallback, ref block, variables);
                    return Emit(block, selected.Operation, selected.Type, left: result, right: fallback);
                }
                case StructuredOptionUnwrap unwrap:
                {
                    var (tag, payload) = OptionParts(unwrap.Option, ref block, variables);
                    return Emit(block, CfgOpcode.OptionUnwrap, IrValueType.I64, left: tag, right: payload);
                }
                case StructuredPropagate propagation:
                    if (propagation.Operand.Type == IrValueType.OptionI64)
                        return PropagateOption(propagation.Operand, ref block, variables);
                    int propagated = PropagateResult(propagation.Operand, ref block, variables);
                    return Emit(block, CfgOpcode.ResultUnwrap, IrValueType.I64, left: propagated);
                case StructuredRead read:
                    if (!variables.TryGetValue(read.Name, out var variable) ||
                        variable.Type != read.Type || variable.Type == IrValueType.OptionI64)
                        throw new FormatException("Unbound or unverified scalar CFG local");
                    return variable.ValueId;
                case StructuredMatchValue match:
                    return MatchValue(match, ref block, variables);
                case StructuredSyncTaskValue task:
                    return InlineTaskValue(task, ref block, variables);
                // Operands may short-circuit into new CFG blocks (ref block), so
                // evaluate them fully before emitting into the current block.
                case StructuredOptionPresent present:
                    return OptionParts(present.Option, ref block, variables).Tag;
                case StructuredOptionAbsent absent:
                {
                    int tag = OptionParts(absent.Option, ref block, variables).Tag;
                    return Emit(block, CfgOpcode.BoolNot, IrValueType.Bool, left: tag);
                }
                case StructuredOptionEqual comparison:
                {
                    // Preserve left-to-right source evaluation exactly once.
                    var lhs = OptionParts(comparison.LeftOption, ref block, variables);
                    var rhs = OptionParts(comparison.RightOption, ref block, variables);
                    int equal = Emit(block, CfgOpcode.OptionEqualI64, IrValueType.Bool,
                        left: lhs.Tag, right: lhs.Payload,
                        arguments: new[] { rhs.Tag, rhs.Payload });
                    return comparison.Negated
                        ? Emit(block, CfgOpcode.BoolNot, IrValueType.Bool, left: equal)
                        : equal;
                }
                case StructuredOptionOr selected:
                {
                    var option = OptionParts(selected.Option, ref block, variables);
                    // Eager source evaluation: evaluate the fallback once even
                    // when the option is Some; only selection is conditional.
                    int fallback = Expression(selected.Fallback, ref block, variables);
                    return Emit(block, CfgOpcode.OptionOrI64, IrValueType.I64,
                        left: option.Tag, right: option.Payload,
                        arguments: new[] { fallback });
                }
                case StructuredCall call:
                {
                    if (call.Type == IrValueType.OptionI64)
                        throw new FormatException("Option<int> helper result requires a tagged consumer");
                    var (function, parameters, slots) = PrepareCall(call, ref block, variables);
                    if (emitCalls)
                        return Emit(block, CfgOpcode.Call, call.Type,
                            target: call.Name, arguments: slots);
                    return Expression(InlineReturn(function), ref block, parameters);
                }
                case StructuredNegate negate:
                {
                    int zero = Emit(block, CfgOpcode.ConstantI64, IrValueType.I64, value: 0);
                    int operand = Expression(negate.Value, ref block, variables);
                    return Emit(block, CfgOpcode.CheckedSubtract, IrValueType.I64,
                        left: zero, right: operand);
                }
                case StructuredConditional conditional:
                {
                    if (HasConditionalWriteback(conditional))
                        return ConditionalWritebackValue(conditional, ref block, variables);
                    int condition = Expression(conditional.Test, ref block, variables);
                    var yes = NewBlock();
                    var no = NewBlock();
                    var join = NewBlock();
                    int joined = NextValue();
                    join.Parameters.Add(new CfgParameter(joined, conditional.Type));
                    block.Terminator = new CfgBranch(condition, yes.Name,
                        Array.Empty<int>(), no.Name, Array.Empty<int>());
                    int trueValue = Expression(conditional.WhenTrue, ref yes, variables);
                    yes.Terminator = new CfgJump(join.Name, new[] { trueValue });
                    int falseValue = Expression(conditional.WhenFalse, ref no, variables);
                    no.Terminator = new CfgJump(join.Name, new[] { falseValue });
                    block = join;
                    return joined;
                }
                case StructuredBinary binary:
                {
                    if (binary.Operator is StructuredOperator.LogicalAnd or StructuredOperator.LogicalOr)
                    {
                        int condition = Expression(binary.Left, ref block, variables);
                        var rhs = NewBlock();
                        // Only a parser-authorized immediate task RHS can
                        // publish writes here. Keep independent environments
                        // for the skipped and evaluated edges.
                        if (binary.Right is StructuredSyncTaskValue { Task.WriteBack: true })
                        {
                            var names = Names(variables);
                            var merged = new Dictionary<string, Binding>(StringComparer.Ordinal);
                            var continuation = Join(names, variables, merged);
                            int result = NextValue();
                            continuation.Parameters.Add(new CfgParameter(result, IrValueType.Bool));
                            int[] skipped = [.. Values(names, variables), condition];
                            var rhsVariables = new Dictionary<string, Binding>(variables, StringComparer.Ordinal);
                            block.Terminator = binary.Operator == StructuredOperator.LogicalAnd
                                ? new CfgBranch(condition, rhs.Name, [], continuation.Name, skipped)
                                : new CfgBranch(condition, continuation.Name, skipped, rhs.Name, []);
                            int evaluated = Expression(binary.Right, ref rhs, rhsVariables);
                            rhs.Terminator = new CfgJump(continuation.Name,
                                [.. Values(names, rhsVariables), evaluated]);
                            foreach (var (name, binding) in merged) variables[name] = binding;
                            block = continuation;
                            return result;
                        }
                        var join = NewBlock();
                        int joined = NextValue();
                        join.Parameters.Add(new CfgParameter(joined, IrValueType.Bool));
                        // The short-circuit edge carries the tested bool itself.
                        // RHS is compiled into a separate CFG block and can never
                        // be evaluated on that edge, including for nested calls.
                        block.Terminator = binary.Operator == StructuredOperator.LogicalAnd
                            ? new CfgBranch(condition, rhs.Name, Array.Empty<int>(),
                                join.Name, new[] { condition })
                            : new CfgBranch(condition, join.Name, new[] { condition },
                                rhs.Name, Array.Empty<int>());
                        int value = Expression(binary.Right, ref rhs, variables);
                        rhs.Terminator = new CfgJump(join.Name, new[] { value });
                        block = join;
                        return joined;
                    }
                    int left = Expression(binary.Left, ref block, variables);
                    int right = Expression(binary.Right, ref block, variables);
                    // Left then right are fully evaluated above (eager left-to-right);
                    // each relation maps to its own verified CFG opcode.
                    var opcode = binary.Operator switch
                    {
                        StructuredOperator.Add => CfgOpcode.CheckedAdd,
                        StructuredOperator.Subtract => CfgOpcode.CheckedSubtract,
                        StructuredOperator.Multiply => CfgOpcode.CheckedMultiply,
                        StructuredOperator.Divide => CfgOpcode.CheckedDivide,
                        StructuredOperator.Remainder => CfgOpcode.CheckedRemainder,
                        StructuredOperator.Modulo => CfgOpcode.CheckedModulo,
                        StructuredOperator.EqualGuestError => CfgOpcode.GuestErrorEqual,
                        StructuredOperator.NotEqualGuestError => CfgOpcode.GuestErrorNotEqual,
                        StructuredOperator.EqualGuestText => CfgOpcode.GuestTextEqual,
                        StructuredOperator.NotEqualGuestText => CfgOpcode.GuestTextNotEqual,
                        StructuredOperator.BitXor => CfgOpcode.BitXorI64,
                        StructuredOperator.ShiftLeft => CfgOpcode.ShiftLeftI64,
                        StructuredOperator.ShiftRightLogical => CfgOpcode.ShiftRightLogicalI64,
                        StructuredOperator.ShiftRightArithmetic => CfgOpcode.ShiftRightArithmeticI64,
                        StructuredOperator.EqualI64 => CfgOpcode.EqualI64,
                        StructuredOperator.NotEqualI64 => CfgOpcode.NotEqualI64,
                        StructuredOperator.LessThanI64 => CfgOpcode.LessThanI64,
                        StructuredOperator.GreaterThanI64 => CfgOpcode.GreaterThanI64,
                        StructuredOperator.LessThanOrEqualI64 => CfgOpcode.LessThanOrEqualI64,
                        StructuredOperator.GreaterThanOrEqualI64 => CfgOpcode.GreaterThanOrEqualI64,
                        StructuredOperator.EqualBool => CfgOpcode.EqualBool,
                        StructuredOperator.NotEqualBool => CfgOpcode.NotEqualBool,
                        StructuredOperator.LogicalXor => CfgOpcode.XorBool,
                        _ => throw new FormatException("Unsupported structured operation")
                    };
                    return Emit(block, opcode, binary.Type, left: left, right: right);
                }
                default:
                    throw new FormatException("Unsupported structured CFG expression");
            }
        }

        private static bool HasConditionalWriteback(StructuredConditional conditional) =>
            conditional.WhenTrue is StructuredSyncTaskValue { Task.WriteBack: true } ||
            conditional.WhenFalse is StructuredSyncTaskValue { Task.WriteBack: true };

        private int ConditionalWritebackValue(StructuredConditional conditional, ref Block block,
            Dictionary<string, Binding> variables)
        {
            int condition = Expression(conditional.Test, ref block, variables);
            var yes = NewBlock();
            var no = NewBlock();
            block.Terminator = new CfgBranch(condition, yes.Name, [], no.Name, []);
            var yesVariables = new Dictionary<string, Binding>(variables, StringComparer.Ordinal);
            var noVariables = new Dictionary<string, Binding>(variables, StringComparer.Ordinal);
            var continuation = new LexicalContinuation(this, variables, conditional.Type, writeBack: true);
            int yesValue = PackedValue(conditional.WhenTrue, ref yes, yesVariables);
            continuation.Finish(yes, yesVariables, yesValue);
            int noValue = PackedValue(conditional.WhenFalse, ref no, noVariables);
            continuation.Finish(no, noVariables, noValue);
            var (join, value) = continuation.Complete();
            block = join ?? throw new FormatException("Conditional writeback has no continuation");
            return value;
        }

        private static string[] Names(Dictionary<string, Binding> variables) =>
            variables.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        private Block Join(string[] names, Dictionary<string, Binding> source,
            Dictionary<string, Binding> result)
        {
            var destination = NewBlock();
            foreach (string name in names)
            {
                var type = source[name].Type;
                if (type == IrValueType.FixedArrayI64)
                {
                    // val-only immutable arrays cannot change on either
                    // control-flow edge. Their scalar SSA elements dominate
                    // the branch/loop and require no phis or aliases.
                    if (source[name].Elements is null)
                        throw new FormatException("E_ARRAY_OWNER: unbound immutable array at join");
                    result[name] = source[name];
                    continue;
                }
                int id = NextValue();
                if (type == IrValueType.OptionI64)
                {
                    int payloadId = NextValue();
                    destination.Parameters.Add(new CfgParameter(id, IrValueType.Bool));
                    destination.Parameters.Add(new CfgParameter(payloadId, IrValueType.I64));
                    result[name] = new Binding(type, id, payloadId);
                }
                else
                {
                    destination.Parameters.Add(new CfgParameter(id, type));
                    result[name] = new Binding(type, id);
                }
            }
            return destination;
        }

        private static int[] Values(string[] names,
            Dictionary<string, Binding> variables)
        {
            var ids = new List<int>(names.Length * 2);
            foreach (string name in names)
            {
                var value = variables[name];
                if (value.Type == IrValueType.FixedArrayI64)
                    continue; // immutable scalar SSA values dominate the edge
                ids.Add(value.ValueId);
                if (value.Type == IrValueType.OptionI64)
                {
                    if (!value.PayloadId.HasValue)
                        throw new FormatException("Missing Option<int> CFG payload");
                    ids.Add(value.PayloadId.Value);
                }
            }
            return ids.ToArray();
        }

        private Block? EmitStatements(IReadOnlyList<StructuredStmt> statements,
            Block start, Dictionary<string, Binding> variables,
            Action<Block, Dictionary<string, Binding>, int?>? localExit = null)
        {
            Block? current = start;
            foreach (var stmt in statements)
            {
                if (current is null)
                    throw new FormatException("Unreachable structured CFG statement");
                switch (stmt)
                {
                    case StructuredLocalTypeDecl localType:
                        throw new FormatException(
                            $"Local {localType.Kind.ToString().ToLowerInvariant()} '{localType.Name}' requires verified value layout and CIL lowering at offset {localType.At}");
                    case StructuredAggregateRuntimeLet aggregate:
                    {
                        // Eagerly evaluate EVERY write exactly once in source
                        // order. Even an overwritten field can contain an
                        // observable call/trap and must not be elided.
                        foreach (var write in aggregate.Writes)
                        {
                            if (variables.ContainsKey(write.Place) ||
                                write.Value.Type != write.Type)
                                throw new FormatException(
                                    "Invalid aggregate scalar SSA place or type");
                            Block writeBlock = current;
                            int value = Expression(write.Value, ref writeBlock, variables);
                            current = writeBlock;
                            variables.Add(write.Place, new Binding(write.Type, value));
                        }
                        break;
                    }
                    case StructuredLet declaration:
                    {
                        if (variables.ContainsKey(declaration.Name))
                            throw new FormatException("Shadowed structured CFG local");
                        Block evalBlock = current;
                        if (declaration.ValueType == IrValueType.OptionI64)
                        {
                            var option = OptionParts(declaration.Initializer, ref evalBlock, variables);
                            current = evalBlock;
                            variables.Add(declaration.Name, new Binding(
                                IrValueType.OptionI64, option.Tag, option.Payload));
                        }
                        else if (declaration.ValueType == IrValueType.FixedArrayI64)
                        {
                            if (declaration.Kind != StructuredBindingKind.Val)
                                throw new FormatException("E_ARRAY_IMMUTABLE_ONLY: fixed array must be val");
                            var elements = ArrayElements(declaration.Initializer, ref evalBlock, variables);
                            current = evalBlock;
                            variables.Add(declaration.Name, new Binding(
                                IrValueType.FixedArrayI64, -1, Elements: elements));
                        }
                        else
                        {
                            int value = Expression(declaration.Initializer, ref evalBlock, variables);
                            current = evalBlock;
                            variables.Add(declaration.Name, new Binding(declaration.ValueType, value));
                        }
                        break;
                    }
                    case StructuredSet assignment:
                    {
                        if (!variables.TryGetValue(assignment.Name, out var original))
                            throw new FormatException("Unknown structured CFG assignment");
                        if (original.Type == IrValueType.FixedArrayI64)
                            throw new FormatException("E_ARRAY_IMMUTABLE_ONLY: fixed arrays cannot be reassigned");
                        Block evalBlock = current;
                        if (original.Type == IrValueType.OptionI64)
                        {
                            var option = OptionParts(assignment.Value, ref evalBlock, variables);
                            current = evalBlock;
                            variables[assignment.Name] = new Binding(
                                IrValueType.OptionI64, option.Tag, option.Payload);
                        }
                        else
                        {
                            int value = Expression(assignment.Value, ref evalBlock, variables);
                            current = evalBlock;
                            variables[assignment.Name] = new Binding(original.Type, value);
                        }
                        break;
                    }
                    case StructuredReturn returned:
                    {
                        Block evalBlock = current;
                        int result = PackedValue(returned.Value, ref evalBlock, variables);
                        evalBlock.Terminator = new CfgReturn(result);
                        current = null;
                        break;
                    }
                    case StructuredReturnVoid:
                        current.Terminator = new CfgReturnVoid();
                        current = null;
                        break;
                    case StructuredSignal signal:
                    {
                        Block evalBlock = current;
                        int error = Expression(signal.Error, ref evalBlock, variables);
                        evalBlock.Terminator = new CfgSignal(error, signal.Effect);
                        current = null;
                        break;
                    }
                    case StructuredOverflowCatchReturn caught:
                        current = OverflowCatch(caught, current, variables);
                        break;
                    case StructuredIf conditional:
                        current = If(conditional, current, variables, localExit);
                        break;
                    case StructuredWhile loop:
                        current = While(loop, current, variables, localExit);
                        break;
                    case StructuredDoMatch match:
                        current = LowerMatch(match.Match, current, variables).Join;
                        break;
                    case StructuredDoSyncTask task:
                        current = LowerInlineTask(task.Task, current, variables).Join;
                        break;
                    case StructuredDeferredStdout stdout:
                        Emit(current, CfgOpcode.RootStdoutLiteral, IrValueType.Bool, text: stdout.Text);
                        break;
                    case StructuredInlineCooperate:
                        Emit(current, CfgOpcode.InlineCooperate, IrValueType.Bool);
                        break;
                    case StructuredLocalExit returned:
                    {
                        if (localExit is null) throw new FormatException("Unscoped lexical return");
                        Block evalBlock = current;
                        int? value = returned.Value is { } expression
                            ? PackedValue(expression, ref evalBlock, variables) : null;
                        localExit(evalBlock, variables, value);
                        current = null;
                        break;
                    }
                    case StructuredBreak:
                    {
                        if (loopTargets.Count == 0)
                            throw new FormatException("break outside structured CFG loop");
                        var loop = loopTargets.Peek();
                        current.Terminator = new CfgJump(loop.Exit.Name,
                            Values(loop.Names, variables));
                        current = null;
                        break;
                    }
                    case StructuredContinue:
                    {
                        if (loopTargets.Count == 0)
                            throw new FormatException("continue outside structured CFG loop");
                        var loop = loopTargets.Peek();
                        current.Terminator = new CfgJump(loop.Header.Name,
                            Values(loop.Names, variables));
                        current = null;
                        break;
                    }
                    default:
                        throw new FormatException("Unsupported structured CFG statement");
                }
            }
            return current;
        }

        private Block? OverflowCatch(StructuredOverflowCatchReturn statement,
            Block origin, Dictionary<string, Binding> variables)
        {
            if (statement.TypeTest is not null &&
                statement.TypeTest is not ("ArithmeticOverflowError" or "Error"))
                throw new FormatException("Unsupported structured guest Error type");

            // V1 is intentionally terminal and result-only. The CLR
            // OverflowException is an internal transport detail: only
            // checked arithmetic overflow may enter this handler, therefore
            // both ArithmeticOverflowError and its Error root select the
            // source match arm. The source mismatch literal is retained in
            // the AST for future multi-error regions but is unreachable here.
            var protectedBlock = NewBlock();
            var handlerBlock = NewBlock();
            var joinBlock = NewBlock();
            int joinValue = NextValue();
            joinBlock.Parameters.Add(new CfgParameter(joinValue, IrValueType.I64));

            origin.Terminator = new CfgJump(
                protectedBlock.Name, Array.Empty<int>());
            protectedBlock.OverflowTarget = handlerBlock.Name;

            Block protectedEnd = protectedBlock;
            int protectedValue = Expression(
                statement.Protected, ref protectedEnd, variables);
            protectedEnd.Terminator = new CfgJump(
                joinBlock.Name, new[] { protectedValue });

            int handlerValue = Emit(
                handlerBlock, CfgOpcode.ConstantI64, IrValueType.I64,
                value: statement.MatchValue);
            handlerBlock.Terminator = new CfgJump(
                joinBlock.Name, new[] { handlerValue });

            joinBlock.Terminator = new CfgReturn(joinValue);
            return null;
        }

        private Block? If(StructuredIf statement, Block origin,
            Dictionary<string, Binding> variables, Action<Block, Dictionary<string, Binding>, int?>? localExit)
        {
            Block conditionBlock = origin;
            int condition = Expression(statement.Condition, ref conditionBlock, variables);
            var names = Names(variables);
            var thenBlock = NewBlock();
            var thenEnvironment = new Dictionary<string, Binding>(
                variables, StringComparer.Ordinal);
            Block? thenEnd = EmitStatements(statement.Then, thenBlock, thenEnvironment, localExit);

            Block? elseBlock = null;
            Block? elseEnd = null;
            var elseEnvironment = new Dictionary<string, Binding>(
                variables, StringComparer.Ordinal);
            if (statement.Else is not null)
            {
                elseBlock = NewBlock();
                elseEnd = EmitStatements(statement.Else, elseBlock, elseEnvironment, localExit);
            }

            if (thenEnd is null && statement.Else is not null && elseEnd is null)
            {
                conditionBlock.Terminator = new CfgBranch(condition, thenBlock.Name,
                    Array.Empty<int>(), elseBlock!.Name, Array.Empty<int>());
                return null;
            }

            var joined = new Dictionary<string, Binding>(StringComparer.Ordinal);
            Block join = Join(names, variables, joined);
            if (thenEnd is not null)
                thenEnd.Terminator = new CfgJump(join.Name, Values(names, thenEnvironment));
            if (statement.Else is null)
                conditionBlock.Terminator = new CfgBranch(condition, thenBlock.Name,
                    Array.Empty<int>(), join.Name, Values(names, variables));
            else
            {
                if (elseEnd is not null)
                    elseEnd.Terminator = new CfgJump(join.Name, Values(names, elseEnvironment));
                conditionBlock.Terminator = new CfgBranch(condition, thenBlock.Name,
                    Array.Empty<int>(), elseBlock!.Name, Array.Empty<int>());
            }
            foreach (string name in names)
                variables[name] = joined[name];
            return join;
        }

        private Block While(StructuredWhile statement, Block origin,
            Dictionary<string, Binding> variables, Action<Block, Dictionary<string, Binding>, int?>? localExit)
        {
            var names = Names(variables);
            var headerVariables = new Dictionary<string, Binding>(StringComparer.Ordinal);
            Block header = Join(names, variables, headerVariables);
            origin.Terminator = new CfgJump(header.Name, Values(names, variables));

            Block conditionBlock = header;
            int condition = Expression(statement.Condition, ref conditionBlock, headerVariables);
            // Create exit before body: break jumps to the same typed join
            // as the condition false path, without leaking loop-local values.
            var exitVariables = new Dictionary<string, Binding>(StringComparer.Ordinal);
            Block exit = Join(names, headerVariables, exitVariables);
            Block body = NewBlock();
            var bodyVariables = new Dictionary<string, Binding>(
                headerVariables, StringComparer.Ordinal);
            loopTargets.Push(new LoopTarget(header, exit, names));
            Block? bodyEnd;
            try { bodyEnd = EmitStatements(statement.Body, body, bodyVariables, localExit); }
            finally { loopTargets.Pop(); }
            if (bodyEnd is not null)
                bodyEnd.Terminator = new CfgJump(header.Name, Values(names, bodyVariables));
            conditionBlock.Terminator = new CfgBranch(condition, body.Name,
                Array.Empty<int>(), exit.Name, Values(names, headerVariables));
            foreach (string name in names)
                variables[name] = exitVariables[name];
            return exit;
        }
    }
}
