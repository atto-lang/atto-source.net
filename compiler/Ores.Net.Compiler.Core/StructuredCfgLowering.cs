namespace Ores.Net.Compiler;

/// <summary>
/// Experimental, non-shipping lowering of the bounded typed structured parser
/// into independently verified SSA CFG. No runtime execution or CLI routing.
/// A local is represented by its current SSA value, never a mutable IL slot.
/// </summary>
public static class OresStructuredCfgLowering
{
    public static VerifiedCfgFunction Lower(string source)
    {
        var parsed = OresStructuredParser.Parse(source);
        return new Builder().Lower(parsed);
    }

    private readonly record struct Binding(IrValueType Type, int ValueId);

    private sealed class Block(string name)
    {
        public string Name { get; } = name;
        public List<CfgParameter> Parameters { get; } = new();
        public List<CfgInstruction> Instructions { get; } = new();
        public CfgTerminator? Terminator { get; set; }

        public CfgBlock Freeze() => new(Name, Parameters.ToArray(),
            Instructions.ToArray(), Terminator ??
            throw new FormatException("Unterminated structured CFG block"));
    }

    private sealed class Builder
    {
        private readonly List<Block> blocks = new();
        private int nextValue;

        public VerifiedCfgFunction Lower(StructuredProgram program)
        {
            if (program.Name != "main")
                throw new FormatException("Only typed main is supported");
            var entry = NewBlock();
            var environment = new Dictionary<string, Binding>(StringComparer.Ordinal);
            if (EmitStatements(program.Body, entry, environment) is not null)
                throw new FormatException("Structured CFG has an unterminated return path");
            return OresCfgVerifier.Verify(new CfgFunction(program.Name,
                IrValueType.I64, entry.Name, blocks.Select(b => b.Freeze()).ToArray()));
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
            long? value = null, bool? boolean = null)
        {
            int id = NextValue();
            block.Instructions.Add(new CfgInstruction(id, opcode, type,
                Left: left, Right: right, I64Constant: value, BoolConstant: boolean));
            return id;
        }

        private int Expression(StructuredExpr expression, Block block,
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
                case StructuredRead read:
                    if (!variables.TryGetValue(read.Name, out var variable) ||
                        variable.Type != read.Type)
                        throw new FormatException("Unbound structured CFG local");
                    return variable.ValueId;
                case StructuredNegate negate:
                {
                    int zero = Emit(block, CfgOpcode.ConstantI64, IrValueType.I64, value: 0);
                    int operand = Expression(negate.Value, block, variables);
                    return Emit(block, CfgOpcode.CheckedSubtract, IrValueType.I64,
                        left: zero, right: operand);
                }
                case StructuredBinary binary:
                {
                    int left = Expression(binary.Left, block, variables);
                    int right = Expression(binary.Right, block, variables);
                    var opcode = binary.Operator switch
                    {
                        StructuredOperator.Add => CfgOpcode.CheckedAdd,
                        StructuredOperator.Subtract => CfgOpcode.CheckedSubtract,
                        StructuredOperator.Multiply => CfgOpcode.CheckedMultiply,
                        StructuredOperator.EqualI64 => CfgOpcode.EqualI64,
                        StructuredOperator.LessThanI64 => CfgOpcode.LessThanI64,
                        _ => throw new FormatException("Unsupported structured operation")
                    };
                    return Emit(block, opcode, binary.Type, left: left, right: right);
                }
                default:
                    throw new FormatException("Unsupported structured CFG expression");
            }
        }

        private static string[] Names(Dictionary<string, Binding> variables) =>
            variables.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        private Block Join(string[] names, Dictionary<string, Binding> source,
            Dictionary<string, Binding> result)
        {
            var destination = NewBlock();
            foreach (string name in names)
            {
                int id = NextValue();
                var type = source[name].Type;
                destination.Parameters.Add(new CfgParameter(id, type));
                result[name] = new Binding(type, id);
            }
            return destination;
        }

        private static int[] Values(string[] names,
            Dictionary<string, Binding> variables) =>
            names.Select(name => variables[name].ValueId).ToArray();

        // Returns null if every path has already terminated with return.
        private Block? EmitStatements(IReadOnlyList<StructuredStmt> statements,
            Block start, Dictionary<string, Binding> variables)
        {
            Block? current = start;
            foreach (var stmt in statements)
            {
                if (current is null)
                    throw new FormatException("Unreachable structured CFG statement");
                switch (stmt)
                {
                    case StructuredLet declaration:
                    {
                        if (variables.ContainsKey(declaration.Name))
                            throw new FormatException("Shadowed structured CFG local");
                        int value = Expression(declaration.Initializer, current, variables);
                        variables.Add(declaration.Name, new Binding(declaration.ValueType, value));
                        break;
                    }
                    case StructuredSet assignment:
                    {
                        if (!variables.TryGetValue(assignment.Name, out var original))
                            throw new FormatException("Unknown structured CFG assignment");
                        int value = Expression(assignment.Value, current, variables);
                        variables[assignment.Name] = new Binding(original.Type, value);
                        break;
                    }
                    case StructuredReturn returned:
                        current.Terminator = new CfgReturn(
                            Expression(returned.Value, current, variables));
                        current = null;
                        break;
                    case StructuredIf conditional:
                        current = If(conditional, current, variables);
                        break;
                    case StructuredWhile loop:
                        current = While(loop, current, variables);
                        break;
                    default:
                        throw new FormatException("Unsupported structured CFG statement");
                }
            }
            return current;
        }

        private Block? If(StructuredIf statement, Block origin,
            Dictionary<string, Binding> variables)
        {
            int condition = Expression(statement.Condition, origin, variables);
            var names = Names(variables);
            var thenBlock = NewBlock();
            var thenEnvironment = new Dictionary<string, Binding>(
                variables, StringComparer.Ordinal);
            Block? thenEnd = EmitStatements(statement.Then, thenBlock, thenEnvironment);

            Block? elseBlock = null;
            Block? elseEnd = null;
            var elseEnvironment = new Dictionary<string, Binding>(
                variables, StringComparer.Ordinal);
            if (statement.Else is not null)
            {
                elseBlock = NewBlock();
                elseEnd = EmitStatements(statement.Else, elseBlock, elseEnvironment);
            }

            if (thenEnd is null && statement.Else is not null && elseEnd is null)
            {
                origin.Terminator = new CfgBranch(condition, thenBlock.Name,
                    Array.Empty<int>(), elseBlock!.Name, Array.Empty<int>());
                return null;
            }

            var joined = new Dictionary<string, Binding>(StringComparer.Ordinal);
            Block join = Join(names, variables, joined);
            if (thenEnd is not null)
                thenEnd.Terminator = new CfgJump(join.Name, Values(names, thenEnvironment));
            if (statement.Else is null)
                origin.Terminator = new CfgBranch(condition, thenBlock.Name,
                    Array.Empty<int>(), join.Name, Values(names, variables));
            else
            {
                if (elseEnd is not null)
                    elseEnd.Terminator = new CfgJump(join.Name, Values(names, elseEnvironment));
                origin.Terminator = new CfgBranch(condition, thenBlock.Name,
                    Array.Empty<int>(), elseBlock!.Name, Array.Empty<int>());
            }
            foreach (string name in names)
                variables[name] = joined[name];
            return join;
        }

        private Block While(StructuredWhile statement, Block origin,
            Dictionary<string, Binding> variables)
        {
            var names = Names(variables);
            var headerVariables = new Dictionary<string, Binding>(StringComparer.Ordinal);
            Block header = Join(names, variables, headerVariables);
            origin.Terminator = new CfgJump(header.Name, Values(names, variables));

            int condition = Expression(statement.Condition, header, headerVariables);
            Block body = NewBlock();
            var bodyVariables = new Dictionary<string, Binding>(
                headerVariables, StringComparer.Ordinal);
            Block? bodyEnd = EmitStatements(statement.Body, body, bodyVariables);
            if (bodyEnd is not null)
                bodyEnd.Terminator = new CfgJump(header.Name, Values(names, bodyVariables));

            var exitVariables = new Dictionary<string, Binding>(StringComparer.Ordinal);
            Block exit = Join(names, headerVariables, exitVariables);
            header.Terminator = new CfgBranch(condition, body.Name,
                Array.Empty<int>(), exit.Name, Values(names, headerVariables));
            foreach (string name in names)
                variables[name] = exitVariables[name];
            return exit;
        }
    }
}

/// Experimental, opt-in lowering of bounded typed structured source into
/// independently verified CFGs. Legacy Lower() inlines helper calls for
/// differential tests; LowerModule() retains independent CIL call boundaries.

    // ABI path: helpers become independent verified CFG methods. The old
    // Lower() method keeps the deliberately bounded inlining/reference path.
    public static VerifiedCfgModule LowerModule(string source)
        => LowerModule(OresStructuredParser.Parse(source));

    internal static VerifiedCfgModule LowerModule(StructuredProgram program)
        var helperSignatures = new Dictionary<string, CfgCallSignature>(StringComparer.Ordinal);
        var methods = new List<VerifiedCfgFunction>();
        var declarations = program.Functions ??
            throw new FormatException("Missing structured function declaration table");
        var order = program.FunctionOrder ??
            throw new FormatException("Missing structured function order witness");
        if (order.Count != declarations.Count || order.Count > 64)
            throw new FormatException("Invalid structured function order witness");
        foreach (string name in order)
            if (!declarations.TryGetValue(name, out var helper) ||
                helper.Name != name || helperSignatures.ContainsKey(name))
                throw new FormatException("Invalid or duplicate ordered helper function");
            // The method order is the exact source order, NOT the incidental
            // iteration order of a dictionary. A helper may refer only to
            // earlier, already verified signatures.
            var verified = new Builder(emitCalls: true).LowerHelper(helper, helperSignatures);
            methods.Add(verified);
            helperSignatures.Add(name, new CfgCallSignature(helper.ResultType,
                Array.AsReadOnly(helper.Parameters.Select(p => p.Type).ToArray())));
        var main = new Builder(emitCalls: true).Lower(program, helperSignatures);
        return new VerifiedCfgModule(main, methods.AsReadOnly());

    // A closed Option<int> is represented by two separately verified SSA IDs.
    // Its tag occupies ValueId (Bool), payload occupies PayloadId (I64).
    private readonly record struct Binding(IrValueType Type, int ValueId, int? PayloadId = null);



    private sealed class Builder(bool emitCalls = false)
        private IReadOnlyDictionary<string, StructuredFunction> functions =
            new Dictionary<string, StructuredFunction>(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, CfgCallSignature>? callSignatures;

        public VerifiedCfgFunction LowerHelper(StructuredFunction helper,
            IReadOnlyDictionary<string, CfgCallSignature> signatures)
            functions = new Dictionary<string, StructuredFunction>(StringComparer.Ordinal);
            callSignatures = signatures;
            var parameters = new Dictionary<string, Binding>(StringComparer.Ordinal);
            for (int i = 0; i < helper.Parameters.Count; i++)
                var p = helper.Parameters[i];
                int id = Emit(entry, CfgOpcode.Argument, p.Type, value: i);
                parameters.Add(p.Name, new Binding(p.Type, id));
            // Parameter values are defined once by ldarg in the entry block.
            // Branch, loop and local scope semantics reuse the verified main
            // builder, but always retain a distinct MethodDef/SSA namespace.
            if (EmitStatements(helper.Body, entry, parameters) is not null)
                throw new FormatException("Typed helper has unterminated return path");
            return OresCfgVerifier.Verify(new CfgFunction(helper.Name, helper.ResultType,
                entry.Name, blocks.Select(b => b.Freeze()).ToArray(),
                helper.Parameters.Select(p => p.Type).ToArray()), signatures);

        public VerifiedCfgFunction Lower(StructuredProgram program,
            IReadOnlyDictionary<string, CfgCallSignature>? signatures = null)
            functions = program.Functions ??
                new Dictionary<string, StructuredFunction>(StringComparer.Ordinal);
            callSignatures = signatures;
                IrValueType.I64, entry.Name, blocks.Select(b => b.Freeze()).ToArray()),
                signatures);



            long? value = null, bool? boolean = null,
            string? target = null, IReadOnlyList<int>? arguments = null)
                Left: left, Right: right, I64Constant: value, BoolConstant: boolean,
                Target: target, Arguments: arguments));

        // Explicit tagged Option<int> representation: no all-bits-value
        // sentinel, nullable managed heap object or implicit flattening.
        private (int Tag, int Payload) OptionParts(StructuredExpr option, ref Block block,
            switch (option)
                case StructuredSome some:
                    int payload = Expression(some.Payload, ref block, variables);
                    int tag = Emit(block, CfgOpcode.ConstantBool, IrValueType.Bool,
                        boolean: true);
                    return (tag, payload);
                case StructuredNone:
                    int tag = Emit(block, CfgOpcode.ConstantBool, IrValueType.Bool,
                        boolean: false);
                    int payload = Emit(block, CfgOpcode.ConstantI64, IrValueType.I64,
                        value: 0);
                    return (tag, payload);
                case StructuredRead read when read.Type == IrValueType.OptionI64:
                    if (!variables.TryGetValue(read.Name, out var value) ||
                        value.Type != IrValueType.OptionI64 || !value.PayloadId.HasValue)
                        throw new FormatException("Unbound or unverified Option<int> local");
                    return (value.ValueId, value.PayloadId.Value);
                    throw new FormatException(
                        "E_OPTION_I64_ONLY: unverified Option<int> expression");
        private int Expression(StructuredExpr expression, ref Block block,
                        variable.Type != read.Type || variable.Type == IrValueType.OptionI64)
                        throw new FormatException("Unbound or unverified scalar CFG local");
                // Operands may short-circuit into new CFG blocks (ref block), so
                // evaluate them fully before emitting into the current block.
                case StructuredOptionPresent present:
                    return OptionParts(present.Option, ref block, variables).Tag;
                case StructuredOptionAbsent absent:
                    int tag = OptionParts(absent.Option, ref block, variables).Tag;
                    return Emit(block, CfgOpcode.BoolNot, IrValueType.Bool, left: tag);
                case StructuredOptionEqual comparison:
                    // Preserve left-to-right source evaluation exactly once.
                    var lhs = OptionParts(comparison.LeftOption, ref block, variables);
                    var rhs = OptionParts(comparison.RightOption, ref block, variables);
                    int equal = Emit(block, CfgOpcode.OptionEqualI64, IrValueType.Bool,
                        left: lhs.Tag, right: lhs.Payload,
                        arguments: new[] { rhs.Tag, rhs.Payload });
                    return comparison.Negated
                        ? Emit(block, CfgOpcode.BoolNot, IrValueType.Bool, left: equal)
                        : equal;
                case StructuredOptionOr selected:
                    var option = OptionParts(selected.Option, ref block, variables);
                    // Eager source evaluation: evaluate the fallback once even
                    // when the option is Some; only selection is conditional.
                    int fallback = Expression(selected.Fallback, ref block, variables);
                    return Emit(block, CfgOpcode.OptionOrI64, IrValueType.I64,
                        left: option.Tag, right: option.Payload,
                        arguments: new[] { fallback });
                case StructuredCall call:
                    if (emitCalls)
                        if (callSignatures is null ||
                            !callSignatures.TryGetValue(call.Name, out var signature) ||
                            signature.ParameterTypes.Count != call.Arguments.Count ||
                            signature.ResultType != call.Type)
                            throw new FormatException("Unverified structured ABI call");
                        var args = new int[call.Arguments.Count];
                        for (int i = 0; i < args.Length; i++)
                        {
                            if (call.Arguments[i].Type != signature.ParameterTypes[i])
                                throw new FormatException("Unverified structured ABI argument");
                            args[i] = Expression(call.Arguments[i], ref block, variables);
                        }
                        return Emit(block, CfgOpcode.Call, call.Type,
                            target: call.Name, arguments: args);
                    if (!functions.TryGetValue(call.Name, out var function) ||
                        function.ResultType != call.Type ||
                        function.Parameters.Count != call.Arguments.Count)
                        throw new FormatException("Unverified structured helper call");

                    // Evaluate each source argument once, left-to-right, and
                    // bind into an independent callee scope. No dynamic call
                    // target, implicit captures, stack frames or runtime JIT.
                    var parameters = new Dictionary<string, Binding>(StringComparer.Ordinal);
                    for (int i = 0; i < call.Arguments.Count; i++)
                        var parameter = function.Parameters[i];
                        if (parameter.Type != call.Arguments[i].Type)
                            throw new FormatException("Unverified structured call argument type");
                        int value = Expression(call.Arguments[i], ref block, variables);
                        parameters.Add(parameter.Name, new Binding(parameter.Type, value));
                    if (function.ReturnValue is null)
                        throw new FormatException(
                            "Statement-bodied helper requires the independent MethodDef ABI");
                    return Expression(function.ReturnValue, ref block, parameters);
                    int operand = Expression(negate.Value, ref block, variables);
                case StructuredConditional conditional:
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
                    if (binary.Operator is StructuredOperator.LogicalAnd or StructuredOperator.LogicalOr)
                        int condition = Expression(binary.Left, ref block, variables);
                        var rhs = NewBlock();
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
                    int left = Expression(binary.Left, ref block, variables);
                    int right = Expression(binary.Right, ref block, variables);
                    // Left then right are fully evaluated above (eager left-to-right);
                    // each relation maps to its own verified CFG opcode.
                        StructuredOperator.NotEqualI64 => CfgOpcode.NotEqualI64,
                        StructuredOperator.GreaterThanI64 => CfgOpcode.GreaterThanI64,
                        StructuredOperator.LessThanOrEqualI64 => CfgOpcode.LessThanOrEqualI64,
                        StructuredOperator.GreaterThanOrEqualI64 => CfgOpcode.GreaterThanOrEqualI64,
                        StructuredOperator.EqualBool => CfgOpcode.EqualBool,
                        StructuredOperator.NotEqualBool => CfgOpcode.NotEqualBool,
                        StructuredOperator.LogicalXor => CfgOpcode.XorBool,


                if (type == IrValueType.OptionI64)
                    int payloadId = NextValue();
                    destination.Parameters.Add(new CfgParameter(id, IrValueType.Bool));
                    destination.Parameters.Add(new CfgParameter(payloadId, IrValueType.I64));
                    result[name] = new Binding(type, id, payloadId);
                else
                    destination.Parameters.Add(new CfgParameter(id, type));
                    result[name] = new Binding(type, id);

            var ids = new List<int>(names.Length * 2);
                var value = variables[name];
                ids.Add(value.ValueId);
                if (value.Type == IrValueType.OptionI64)
                    if (!value.PayloadId.HasValue)
                        throw new FormatException("Missing Option<int> CFG payload");
                    ids.Add(value.PayloadId.Value);
            return ids.ToArray();

                    case StructuredLocalTypeDecl localType:
                        throw new FormatException(
                            $"Local {localType.Kind.ToString().ToLowerInvariant()} '{localType.Name}' requires verified value layout and CIL lowering at offset {localType.At}");
                        Block evalBlock = current;
                        if (declaration.ValueType == IrValueType.OptionI64)
                        {
                            var option = OptionParts(declaration.Initializer, ref evalBlock, variables);
                            current = evalBlock;
                            variables.Add(declaration.Name, new Binding(
                                IrValueType.OptionI64, option.Tag, option.Payload));
                        }
                        else
                        {
                            int value = Expression(declaration.Initializer, ref evalBlock, variables);
                            current = evalBlock;
                            variables.Add(declaration.Name, new Binding(declaration.ValueType, value));
                        }
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
                        Block evalBlock = current;
                        int result = Expression(returned.Value, ref evalBlock, variables);
                        evalBlock.Terminator = new CfgReturn(result);

            Block conditionBlock = origin;
            int condition = Expression(statement.Condition, ref conditionBlock, variables);


                conditionBlock.Terminator = new CfgBranch(condition, thenBlock.Name,

                conditionBlock.Terminator = new CfgBranch(condition, thenBlock.Name,
                conditionBlock.Terminator = new CfgBranch(condition, thenBlock.Name,


            Block conditionBlock = header;
            int condition = Expression(statement.Condition, ref conditionBlock, headerVariables);

            conditionBlock.Terminator = new CfgBranch(condition, body.Name,
