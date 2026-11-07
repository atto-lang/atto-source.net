using System.Buffers.Binary;
using Ores.Net.Compiler;
using Ores.Net.Runtime;

namespace Ores.Net.Compiler.ActorFrames;

/// <summary>
/// A closed, typed, source-verified scalar CFG executed iteratively inside
/// one real actor turn. All guest scalar SSA values and parameters reside in
/// actor-owned native frames, not on the CLR recursive call stack.
///
/// THIS IS NOT full actor declaration lowering or CIL code generation. The
/// verified source module is interpreted by a NativeAOT-compatible host
/// trampoline and rejects every unsupported call shape before executing.
/// </summary>
public sealed class ActorScalarFrameProgram
{
    private const int MaxValueSlotsPerFunction = 128;
    private const int MaxFunctions = 64;
    private const int MaxCallDepth = 32;
    private const int HeaderBytes = 16;
    private const int ScalarBytes = sizeof(long);
    private readonly IReadOnlyDictionary<string, FunctionPlan> _functions;
    private readonly FunctionPlan _main;

    private sealed class FunctionPlan
    {
        internal FunctionPlan(VerifiedCfgFunction function, int valueSlots)
        {
            Function = function;
            ValueSlots = valueSlots;
            Blocks = function.Blocks.ToDictionary(b => b.Name, StringComparer.Ordinal);
            FrameBytes = checked(HeaderBytes +
                (function.Parameters.Count + valueSlots) * ScalarBytes);
        }

        internal VerifiedCfgFunction Function { get; }
        internal IReadOnlyDictionary<string, CfgBlock> Blocks { get; }
        internal int ValueSlots { get; }
        internal int FrameBytes { get; }
        internal int ParameterOffset => HeaderBytes;
        internal int ValueOffset => HeaderBytes + Function.Parameters.Count * ScalarBytes;
    }

    private sealed class Activation
    {
        internal Activation(FunctionPlan plan, ActorFrame frame, int returnSlot,
            long[] arguments, ActorContext actor)
        {
            Plan = plan;
            Frame = frame;
            ReturnSlot = returnSlot;
            BlockName = plan.Function.Entry;
            Incoming = Array.Empty<long>();
            SetScalar(actor, 0, returnSlot);
            SetScalar(actor, ScalarBytes, 0); // reserved program-counter descriptor
            for (int i = 0; i < arguments.Length; i++)
                SetScalar(actor, plan.ParameterOffset + ScalarBytes * i, arguments[i]);
        }

        internal FunctionPlan Plan { get; }
        internal ActorFrame Frame { get; }
        internal int ReturnSlot { get; }
        internal string BlockName { get; set; }
        internal int InstructionIndex { get; set; }
        internal long[]? Incoming { get; set; }
        internal bool EntryPending { get; set; } = true;

        internal long ReadValue(ActorContext actor, int slot)
        {
            if ((uint)slot >= (uint)Plan.ValueSlots)
                throw new InvalidOperationException("Unverified scalar slot");
            return ReadScalar(actor, Plan.ValueOffset + ScalarBytes * slot);
        }

        internal void WriteValue(ActorContext actor, int slot, long value)
        {
            if ((uint)slot >= (uint)Plan.ValueSlots)
                throw new InvalidOperationException("Unverified scalar slot");
            SetScalar(actor, Plan.ValueOffset + ScalarBytes * slot, value);
        }

        internal long ReadScalar(ActorContext actor, int offset)
        {
            Span<byte> data = stackalloc byte[ScalarBytes];
            actor.ReadFrame(Frame, offset, data);
            return BinaryPrimitives.ReadInt64LittleEndian(data);
        }

        internal void SetScalar(ActorContext actor, int offset, long value)
        {
            Span<byte> data = stackalloc byte[ScalarBytes];
            BinaryPrimitives.WriteInt64LittleEndian(data, value);
            actor.WriteFrame(Frame, offset, data);
        }

        internal void BranchTo(string destination, long[] edge)
        {
            BlockName = destination;
            Incoming = edge;
            InstructionIndex = 0;
            EntryPending = true;
        }
    }

    private ActorScalarFrameProgram(
        IReadOnlyDictionary<string, FunctionPlan> functions, FunctionPlan main)
    {
        _functions = functions;
        _main = main;
    }

    /// <summary>
    /// Parse, typecheck and verify the entire reachable closed helper graph
    /// before returning an executable plan. The structured frontend rejects
    /// define actor/class, arbitrary effects, reflection and recursive calls.
    /// </summary>
    public static ActorScalarFrameProgram Compile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var verified = OresStructuredCfgLowering.LowerModule(source);
        if (verified.Helpers.Count > MaxFunctions)
            throw new FormatException("Actor scalar helper function budget exceeded");

        var functions = new Dictionary<string, FunctionPlan>(StringComparer.Ordinal);
        foreach (var fn in verified.Helpers)
        {
            var plan = Admit(fn);
            if (!functions.TryAdd(fn.Name, plan))
                throw new FormatException("Duplicate actor scalar helper");
        }
        var main = Admit(verified.Main);
        if (main.Function.Name != "main" ||
            main.Function.Parameters.Count != 0 ||
            main.Function.ResultType != IrValueType.I64)
            throw new FormatException("Actor scalar entry must be main():int");

        foreach (var function in functions.Values.Append(main))
        {
            foreach (var block in function.Function.Blocks)
            {
                foreach (var op in block.Instructions)
                {
                    if (op.Opcode != CfgOpcode.Call) continue;
                    if (op.Target is null || op.Arguments is null ||
                        !functions.TryGetValue(op.Target, out var callee) ||
                        op.Arguments.Count != callee.Function.Parameters.Count ||
                        op.Type != callee.Function.ResultType)
                        throw new FormatException("Actor scalar call graph is not closed");
                }
            }
        }
        return new ActorScalarFrameProgram(functions, main);
    }

    private static FunctionPlan Admit(VerifiedCfgFunction fn)
    {
        if (fn.ResultType is not (IrValueType.I64 or IrValueType.Bool))
            throw new FormatException("Unsupported actor scalar return type");
        if (fn.Parameters.Count > 16 ||
            fn.Parameters.Any(type => type is not (IrValueType.I64 or IrValueType.Bool)))
            throw new FormatException("Unsupported actor scalar parameter type");
        int maxSlot = -1;
        foreach (var block in fn.Blocks)
        {
            foreach (var parameter in block.Parameters)
                maxSlot = Math.Max(maxSlot, parameter.Id);
            foreach (var op in block.Instructions)
            {
                if (!Enum.IsDefined(op.Opcode))
                    throw new FormatException("Unverified actor scalar opcode");
                maxSlot = Math.Max(maxSlot, op.ResultId);
            }
        }
        if (maxSlot < 0 || maxSlot >= MaxValueSlotsPerFunction)
            throw new FormatException("Actor scalar frame value quota exceeded");
        return new FunctionPlan(fn, maxSlot + 1);
    }

    /// <summary>
    /// Execute without recursive managed calls. This method may only be
    /// invoked from an admitted actor handler. Every non-tail function call
    /// pushes its own actor-owned native frame; the handler owns an additional
    /// root frame. The finite step budget rejects unbounded CFG loops.
    /// </summary>
    public long Execute(ActorContext actor, int maxSteps = 100_000)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (maxSteps <= 0 || maxSteps > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(maxSteps));

        var stack = new List<Activation>(8);
        try
        {
            Push(_main, Array.Empty<long>(), -1);
            for (int step = 0; step < maxSteps; step++)
            {
                Activation current = stack[^1];
                CfgBlock block = current.Plan.Blocks[current.BlockName];
                if (current.EntryPending)
                {
                    if (current.Incoming is null ||
                        current.Incoming.Length != block.Parameters.Count)
                        throw new InvalidOperationException("Invalid actor CFG branch arguments");
                    for (int i = 0; i < block.Parameters.Count; i++)
                        current.WriteValue(actor, block.Parameters[i].Id, current.Incoming[i]);
                    current.Incoming = null;
                    current.EntryPending = false;
                }

                if (current.InstructionIndex < block.Instructions.Count)
                {
                    var op = block.Instructions[current.InstructionIndex++];
                    // Keep a native descriptor of the next instruction offset.
                    current.SetScalar(actor, ScalarBytes, current.InstructionIndex);
                    if (op.Opcode == CfgOpcode.Call)
                    {
                        var target = _functions[op.Target!];
                        var inputs = op.Arguments!.Select(id =>
                            current.ReadValue(actor, id)).ToArray();
                        Push(target, inputs, op.ResultId);
                    }
                    else
                    {
                        long value = op.Opcode switch
                        {
                            CfgOpcode.ConstantI64 => op.I64Constant!.Value,
                            CfgOpcode.ConstantBool => op.BoolConstant!.Value ? 1 : 0,
                            CfgOpcode.Argument => current.ReadScalar(actor,
                                current.Plan.ParameterOffset +
                                checked((int)op.I64Constant!.Value * ScalarBytes)),
                            CfgOpcode.CheckedAdd => checked(
                                current.ReadValue(actor, op.Left!.Value) +
                                current.ReadValue(actor, op.Right!.Value)),
                            CfgOpcode.CheckedSubtract => checked(
                                current.ReadValue(actor, op.Left!.Value) -
                                current.ReadValue(actor, op.Right!.Value)),
                            CfgOpcode.CheckedMultiply => checked(
                                current.ReadValue(actor, op.Left!.Value) *
                                current.ReadValue(actor, op.Right!.Value)),
                            CfgOpcode.CheckedDivide => DivideChecked(
                                current.ReadValue(actor, op.Left!.Value),
                                current.ReadValue(actor, op.Right!.Value)),
                            CfgOpcode.EqualI64 =>
                                current.ReadValue(actor, op.Left!.Value) ==
                                current.ReadValue(actor, op.Right!.Value) ? 1 : 0,
                            CfgOpcode.NotEqualI64 =>
                                current.ReadValue(actor, op.Left!.Value) !=
                                current.ReadValue(actor, op.Right!.Value) ? 1 : 0,
                            CfgOpcode.LessThanI64 =>
                                current.ReadValue(actor, op.Left!.Value) <
                                current.ReadValue(actor, op.Right!.Value) ? 1 : 0,
                            _ => throw new InvalidOperationException(
                                "Unverified actor scalar instruction")
                        };
                        current.WriteValue(actor, op.ResultId, value);
                    }
                    continue;
                }

                switch (block.Terminator)
                {
                    case CfgReturn returned:
                        long value = current.ReadValue(actor, returned.ValueId);
                        current.Frame.Dispose();
                        stack.RemoveAt(stack.Count - 1);
                        if (stack.Count == 0) return value;
                        stack[^1].WriteValue(actor, current.ReturnSlot, value);
                        break;
                    case CfgJump jump:
                        current.BranchTo(jump.Target,
                            jump.Arguments.Select(id =>
                                current.ReadValue(actor, id)).ToArray());
                        break;
                    case CfgBranch branch:
                        bool chooseTrue = current.ReadValue(actor, branch.Condition) != 0;
                        var edge = chooseTrue ? branch.TrueArguments : branch.FalseArguments;
                        current.BranchTo(chooseTrue ? branch.TrueTarget : branch.FalseTarget,
                            edge.Select(id => current.ReadValue(actor, id)).ToArray());
                        break;
                    default:
                        throw new InvalidOperationException(
                            "Unverified actor scalar CFG terminator");
                }
            }
            throw new InvalidOperationException("Actor scalar execution step budget exhausted");
        }
        finally
        {
            // Stackless iterative unwind also handles checked arithmetic traps,
            // abnormal interpreter exits and failed native-frame admission.
            for (int i = stack.Count - 1; i >= 0; i--)
                stack[i].Frame.Dispose();
        }

        void Push(FunctionPlan plan, long[] args, int returnSlot)
        {
            if (stack.Count >= MaxCallDepth)
                throw new InvalidOperationException("Actor scalar call depth exhausted");
            if (args.Length != plan.Function.Parameters.Count)
                throw new InvalidOperationException("Actor scalar call signature mismatch");
            var frame = actor.PushFrame(plan.FrameBytes);
            try
            {
                stack.Add(new Activation(plan, frame, returnSlot, args, actor));
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }
    }

    private static long DivideChecked(long a, long b)
    {
        if (b == 0) throw new DivideByZeroException();
        if (a == long.MinValue && b == -1) throw new OverflowException();
        return a / b;
    }
}
