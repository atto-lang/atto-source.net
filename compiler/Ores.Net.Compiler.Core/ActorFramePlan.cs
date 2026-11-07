namespace Ores.Net.Compiler;

/// <summary>
/// Immutable compiler-owned scalar spill descriptors. These are derived only
/// after typed CFG admission; they describe a potential guest continuation
/// frame and do not by themselves lower .atto suspend/await to executable CIL.
/// </summary>
public readonly record struct ActorFrameSlotPlan(
    int ResultId, IrValueType Type, int ByteOffset);

public sealed class VerifiedActorFramePlan
{
    internal VerifiedActorFramePlan(string functionName,
        IReadOnlyList<ActorFrameSlotPlan> slots)
    {
        FunctionName = functionName;
        Slots = slots;
    }

    public string FunctionName { get; }
    public IReadOnlyList<ActorFrameSlotPlan> Slots { get; }
    public int ByteLength => checked(Slots.Count * 8);
}

/// <summary>
/// Conservative, bounded eight-byte-per-SSA-value layout derived from the
/// separately verified structured CFG. Future lowering must calculate actual
/// live-across-suspend sets and prove drop/borrow ownership before emitting
/// spill/restore machine instructions. Until then, this is metadata only.
/// </summary>
public static class OresActorFramePlanner
{
    public const int MaxSlots = 128;

    public static VerifiedActorFramePlan Plan(VerifiedCfgFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var definitions = new List<(int Id, IrValueType Type)>();

        void Include(int id, IrValueType type)
        {
            if (definitions.Count >= MaxSlots)
                throw new FormatException(
                    "Actor continuation frame exceeds the verified 128-slot budget");
            if (id < 0 || type is not (IrValueType.I64 or IrValueType.Bool))
                throw new FormatException(
                    "Unverified or unsupported actor continuation SSA slot");
            definitions.Add((id, type));
        }

        foreach (var block in function.Blocks)
        {
            foreach (var parameter in block.Parameters)
                Include(parameter.Id, parameter.Type);
            foreach (var instruction in block.Instructions)
                Include(instruction.ResultId, instruction.Type);
        }

        if (definitions.Count == 0)
            throw new FormatException("Actor continuation frame has no definitions");
        definitions.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        var slots = new ActorFrameSlotPlan[definitions.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            if (definitions[i].Id != i)
                throw new FormatException(
                    "Actor continuation SSA slots must be unique and dense");
            slots[i] = new ActorFrameSlotPlan(
                i, definitions[i].Type, checked(i * 8));
        }

        // Never expose mutable caller or builder storage as verified layout.
        return new VerifiedActorFramePlan(
            function.Name, Array.AsReadOnly(slots));
    }
}
