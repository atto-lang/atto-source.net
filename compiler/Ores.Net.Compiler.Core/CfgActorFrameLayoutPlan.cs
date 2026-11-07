namespace Ores.Net.Compiler;

/// <summary>
/// One typed SSA value's location in a future actor-owned continuation frame.
/// i64 and bool use distinct types but both occupy an aligned 8-byte slot.
/// This is a compiler layout description, not a CLR stack pointer or CIL emit.
/// </summary>
public readonly record struct CfgActorFrameSlot(
    int ValueId, IrValueType Type, int ByteOffset, int ByteLength);

/// <summary>
/// Immutable, fully bounded description for a verified CFG function's
/// actor-local scalar values. No dynamic evaluation or guest actor admission.
/// </summary>
public sealed class CfgActorFrameLayout
{
    internal CfgActorFrameLayout(CfgActorFrameSlot[] slots)
    {
        Slots = Array.AsReadOnly(slots);
        ByteLength = checked(slots.Length * CfgActorFrameLayoutPlanner.SlotBytes);
    }

    public IReadOnlyList<CfgActorFrameSlot> Slots { get; }
    public int ByteLength { get; }
}

/// <summary>
/// First compiler-side step toward typed .atto actor frame lowering.
/// A future CIL/AOT emitter must actually use these slots for typed spills,
/// restore, checked drops and unwind; building a plan proves NONE of that.
/// In particular, this API does not enable source define actor or await.
/// </summary>
public static class CfgActorFrameLayoutPlanner
{
    public const int SlotBytes = 8;
    public const int DefaultFrameBytes = 64 * 1024;
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    // Canonical runtime ActorFrameTypedLayout currently admits 1..128 slots.
    // Keep generic CFG layouts independently flexible for other consumers,
    // but fail closed before deriving a native typed-actor spill descriptor.
    public const int MaximumCanonicalTypedSlots = 128;
    public const int MaximumCanonicalTypedBytes = MaximumCanonicalTypedSlots * SlotBytes;

    public static CfgActorFrameLayout PlanCanonicalTyped(
        VerifiedCfgFunction verified,
        int maximumFrameBytes = MaximumCanonicalTypedBytes)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (maximumFrameBytes <= 0 || maximumFrameBytes > MaximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        return Plan(verified, Math.Min(maximumFrameBytes, MaximumCanonicalTypedBytes));
    }

    public static CfgActorFrameLayout Plan(
        VerifiedCfgFunction verified, int maximumFrameBytes = DefaultFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (maximumFrameBytes <= 0 || maximumFrameBytes > MaximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));

        long count = 0;
        foreach (var block in verified.Blocks)
        {
            count += block.Parameters.Count;
            count += block.Instructions.Count;
            if (count > OresCfgVerifier.MaxValues)
                throw new FormatException("Actor frame SSA slot count exceeds verifier limit");
        }
        if (count == 0 || count > maximumFrameBytes / SlotBytes)
            throw new FormatException("Actor frame typed scalar byte budget exceeded");

        var slots = new CfgActorFrameSlot[checked((int)count)];
        var present = new bool[slots.Length];

        void Add(int id, IrValueType kind)
        {
            if (id < 0 || id >= slots.Length || present[id] ||
                kind is not (IrValueType.I64 or IrValueType.Bool))
                throw new FormatException("Actor frame SSA identity or type invalid");
            present[id] = true;
            slots[id] = new CfgActorFrameSlot(
                id, kind, checked(id * SlotBytes), SlotBytes);
        }

        foreach (var block in verified.Blocks)
        {
            foreach (var parameter in block.Parameters)
                Add(parameter.Id, parameter.Type);
            foreach (var instruction in block.Instructions)
                Add(instruction.ResultId, instruction.Type);
        }
        if (Array.Exists(present, static available => !available))
            throw new FormatException("Actor frame SSA values are not dense");

        return new CfgActorFrameLayout(slots);
    }
}
