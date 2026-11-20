using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Ores.Net.Compiler;

/// <summary>
/// Opt-in metadata-only CIL emitter for verified synchronous scalar CFG
/// methods. Unlike the normal scalar emitter, *every* SSA i64/bool value is
/// stored in an authenticated actor-local native frame, not a managed CLR
/// local. The CLR carrier stack remains in use for CIL calls and short-lived
/// branch-edge scratch; this is not suspension, native-stack containment or
/// canonical source "define actor" admission.
/// </summary>
internal static class CfgActorFrameCilEmitter
{
    public static void EmitLibrary(VerifiedCfgModule module, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        // The ordinary, non-actor CFG emitter never references actor runtime.
        CilEmitter.EmitLibraryModuleCore(outputFile, module,
            (graph, metadata, bodies, handles, actorRefs) =>
                WriteMethod(graph, metadata, bodies, handles,
                    actorRefs ?? throw new FormatException(
                        "Actor-frame CIL ABI references are missing")),
            actorFrameAbi: true);
    }

    private static (int BodyOffset, BlobBuilder Code, BlobBuilder? Locals)
        WriteMethod(VerifiedCfgFunction graph, MetadataBuilder metadata,
            MethodBodyStreamEncoder bodies,
            IReadOnlyDictionary<string, MethodDefinitionHandle> handles,
            ActorFrameCilReferences refs)
    {
        // A larger verified CFG is not silently accepted and then truncated
        // into the runtime's 64 KiB default per-actor frame slab.
        var layout = CfgActorFrameLayoutPlanner.Plan(graph);
        var blocks = graph.Blocks;
        bool isMain = graph.Name == "main";
        int scratchCount = blocks.Sum(b => b.Parameters.Count);
        if (scratchCount > OresCfgVerifier.MaxValues)
            throw new FormatException("Actor CIL branch-edge scratch budget exceeded");

        // Local 0: execution ticket (exact-issued, never wire).
        // Local 1: frame ticket (exact-issued, never wire).
        // Local 2: scalar return value before leaving the EH region.
        // Locals 3+: transient typed edge-scratch, never held across await.
        var localBlob = new BlobBuilder();
        var locals = new BlobEncoder(localBlob)
            .LocalVariableSignature(checked(3 + scratchCount));
        locals.AddVariable().Type().Type(refs.Execution, isValueType: false);
        locals.AddVariable().Type().Type(refs.Frame, isValueType: false);
        EmitScalar(locals.AddVariable().Type(), graph.ResultType);

        var scratch = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var parameterTypes =
            new Dictionary<string, IReadOnlyList<CfgParameter>>(StringComparer.Ordinal);
        int next = 3;
        foreach (var block in blocks)
        {
            var slots = new int[block.Parameters.Count];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = next++;
                EmitScalar(locals.AddVariable().Type(), block.Parameters[i].Type);
            }
            scratch.Add(block.Name, slots);
            parameterTypes.Add(block.Name, block.Parameters);
        }
        if (next != 3 + scratchCount)
            throw new FormatException("Actor CIL edge scratch layout mismatch");

        var signature = metadata.AddStandaloneSignature(
            metadata.GetOrAddBlob(localBlob));
        var code = new BlobBuilder();
        var flow = new ControlFlowBuilder();
        var il = new InstructionEncoder(code, flow);

        var entryLabels = new Dictionary<string, LabelHandle>(StringComparer.Ordinal);
        foreach (var block in blocks)
            entryLabels.Add(block.Name, il.DefineLabel());
        var tryStart = il.DefineLabel();
        var tryEnd = il.DefineLabel();
        var finallyStart = il.DefineLabel();
        var finallyEnd = il.DefineLabel();
        var leaveReturn = il.DefineLabel();
        flow.AddFinallyRegion(tryStart, tryEnd, finallyStart, finallyEnd);

        static void EmitScalar(SignatureTypeEncoder target, IrValueType type)
        {
            if (type == IrValueType.I64) target.Int64();
            else if (type == IrValueType.Bool) target.Boolean();
            else throw new FormatException("Unsupported actor frame scalar type");
        }

        int OffsetOf(int valueId)
        {
            if (valueId < 0 || valueId >= layout.Slots.Count ||
                layout.Slots[valueId].ValueId != valueId)
                throw new FormatException("Invalid verified actor SSA value");
            return layout.Slots[valueId].ByteOffset;
        }

        void ReadValue(int valueId)
        {
            il.LoadArgument(0);
            il.LoadLocal(0);
            il.LoadLocal(1);
            il.LoadConstantI4(OffsetOf(valueId));
            il.Call(layout.Slots[valueId].Type == IrValueType.Bool
                ? refs.ReadBool : refs.ReadI64);
        }

        void WriteValue(int valueId, Action expression)
        {
            il.LoadArgument(0);
            il.LoadLocal(0);
            il.LoadLocal(1);
            il.LoadConstantI4(OffsetOf(valueId));
            expression();
            il.Call(layout.Slots[valueId].Type == IrValueType.Bool
                ? refs.WriteBool : refs.WriteI64);
        }

        void Transfer(string destination, IReadOnlyList<int> arguments)
        {
            var scratchLocals = scratch[destination];
            var targets = parameterTypes[destination];
            if (scratchLocals.Length != arguments.Count)
                throw new FormatException("Invalid verified actor CFG edge arity");
            // All edge sources are read *before* any destination is changed,
            // preserving parallel phi/branch parameter semantics even in swaps.
            for (int i = 0; i < arguments.Count; i++)
            {
                ReadValue(arguments[i]);
                il.StoreLocal(scratchLocals[i]);
            }
            for (int i = 0; i < arguments.Count; i++)
            {
                int iCopy = i;
                WriteValue(targets[i].Id, () => il.LoadLocal(scratchLocals[iCopy]));
            }
            il.Branch(ILOpCode.Br, entryLabels[destination]);
        }

        if (isMain)
        {
            // Begin binds the logical execution exactly to the current
            // physical turn; admission failure cannot leak frame bytes.
            il.LoadArgument(0);
            il.Call(refs.Begin);
            il.StoreLocal(0);
        }
        else
        {
            // Helpers inherit the caller's authenticated logical execution.
            il.LoadArgument(1);
            il.StoreLocal(0);
        }

        // Push is outside the try region: on quota exhaustion it poisons the
        // actor stack and the actor runtime terminates its physical turn.
        // Once Push succeeds, all guest CFG exits (including CLR throws)
        // execute Pop; entry additionally Finish-es its logical execution.
        il.LoadArgument(0);
        il.LoadLocal(0);
        il.LoadConstantI4(layout.ByteLength);
        il.Call(refs.Push);
        il.StoreLocal(1);

        il.MarkLabel(tryStart);
        il.Branch(ILOpCode.Br, entryLabels[graph.Entry]);
        foreach (var block in blocks)
        {
            il.MarkLabel(entryLabels[block.Name]);
            foreach (var op in block.Instructions)
            {
                WriteValue(op.ResultId, () =>
                {
                    switch (op.Opcode)
                    {
                        case CfgOpcode.Argument:
                            // Helpers reserve arg0=context and arg1=execution.
                            if (isMain)
                                throw new FormatException("Actor main cannot take CFG arguments");
                            il.LoadArgument(checked((int)op.I64Constant!.Value) + 2);
                            break;
                        case CfgOpcode.Call:
                            if (op.Target is null || op.Arguments is null ||
                                !handles.TryGetValue(op.Target, out var callee))
                                throw new FormatException("Unlinked actor CFG call");
                            il.LoadArgument(0);
                            il.LoadLocal(0);
                            foreach (int id in op.Arguments)
                                ReadValue(id);
                            il.Call(callee);
                            break;
                        case CfgOpcode.ConstantI64:
                            il.LoadConstantI8(op.I64Constant!.Value);
                            break;
                        case CfgOpcode.ConstantBool:
                            il.LoadConstantI4(op.BoolConstant!.Value ? 1 : 0);
                            break;
                        case CfgOpcode.CheckedAdd:
                        case CfgOpcode.CheckedSubtract:
                        case CfgOpcode.CheckedMultiply:
                        case CfgOpcode.CheckedDivide:
                        case CfgOpcode.EqualI64:
                        case CfgOpcode.NotEqualI64:
                        case CfgOpcode.LessThanI64:
                            ReadValue(op.Left!.Value);
                            ReadValue(op.Right!.Value);
                            il.OpCode(op.Opcode switch
                            {
                                CfgOpcode.CheckedAdd => ILOpCode.Add_ovf,
                                CfgOpcode.CheckedSubtract => ILOpCode.Sub_ovf,
                                CfgOpcode.CheckedMultiply => ILOpCode.Mul_ovf,
                                CfgOpcode.CheckedDivide => ILOpCode.Div,
                                CfgOpcode.EqualI64 => ILOpCode.Ceq,
                                CfgOpcode.NotEqualI64 => ILOpCode.Ceq,
                                CfgOpcode.LessThanI64 => ILOpCode.Clt,
                                _ => throw new FormatException("Invalid actor CFG binary opcode")
                            });
                            if (op.Opcode == CfgOpcode.NotEqualI64)
                            {
                                il.LoadConstantI4(0);
                                il.OpCode(ILOpCode.Ceq);
                            }
                            break;
                        default:
                            throw new FormatException("Unsupported actor CFG opcode");
                    }
                });
            }

            switch (block.Terminator)
            {
                case CfgReturn result:
                    ReadValue(result.ValueId);
                    il.StoreLocal(2);
                    il.Branch(ILOpCode.Leave, leaveReturn);
                    break;
                case CfgJump jump:
                    Transfer(jump.Target, jump.Arguments);
                    break;
                case CfgBranch condition:
                    var onTrue = il.DefineLabel();
                    ReadValue(condition.Condition);
                    il.Branch(ILOpCode.Brtrue, onTrue);
                    Transfer(condition.FalseTarget, condition.FalseArguments);
                    il.MarkLabel(onTrue);
                    Transfer(condition.TrueTarget, condition.TrueArguments);
                    break;
                default:
                    throw new FormatException("Unsupported actor CFG terminator");
            }
        }

        il.MarkLabel(tryEnd);
        il.MarkLabel(finallyStart);
        il.LoadArgument(0);
        il.LoadLocal(0);
        il.LoadLocal(1);
        il.Call(refs.Pop);
        if (isMain)
        {
            il.LoadArgument(0);
            il.LoadLocal(0);
            il.Call(refs.Finish);
        }
        il.OpCode(ILOpCode.Endfinally);
        il.MarkLabel(finallyEnd);
        il.MarkLabel(leaveReturn);
        il.LoadLocal(2);
        il.OpCode(ILOpCode.Ret);

        if (code.Count > 4 * 1024 * 1024)
            throw new FormatException("Actor CFG CIL method byte budget exceeded");

        // The output WriteFrame* call holds four arguments while its
        // expression is evaluated. In a nested helper call, source operands
        // already on the stack coexist with four temporary ReadFrame*
        // arguments. Bound that *transient peak*, not just final call arity.
        int mostArguments = blocks.SelectMany(b => b.Instructions)
            .Where(op => op.Opcode == CfgOpcode.Call)
            .Select(op => op.Arguments!.Count)
            .DefaultIfEmpty(0).Max();
        int maxStack = checked(12 + mostArguments);
        int offset = bodies.AddMethodBody(il, maxStack: maxStack,
            localVariablesSignature: signature,
            attributes: MethodBodyAttributes.InitLocals);
        return (offset, code, localBlob);
    }
}
