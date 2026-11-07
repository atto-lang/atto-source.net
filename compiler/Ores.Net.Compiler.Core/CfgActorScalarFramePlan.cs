namespace Ores.Net.Compiler;

/// <summary>
/// Compiler-side layout for a bounded, already-verified CFG scalar activation.
/// This is an opt-in typed spill plan; no guest actor scheduling or await
/// lowering is enabled by producing the plan.
/// </summary>
public sealed class CfgActorScalarFramePlan
{
    // Must match ActorScalarFrameAbiV1; compiler does not depend on runtime.
    public const int HeaderBytes = 16;
    public const int SlotBytes = 16;
    public const int MaxValueSlots = 256;
    public const int MaxBlocks = 128;

    private readonly IReadOnlyList<IrValueType> _types;
    private readonly IReadOnlyList<string> _blocks;

    private CfgActorScalarFramePlan(IrValueType[] types, string[] blocks, int entry)
    {
        _types = Array.AsReadOnly(types);
        _blocks = Array.AsReadOnly(blocks);
        EntryBlockIndex = entry;
    }

    public int ValueSlots => _types.Count;
    public int FrameBytes => checked(HeaderBytes + ValueSlots * SlotBytes);
    public int BlockCount => _blocks.Count;
    public int EntryBlockIndex { get; }
    public IReadOnlyList<IrValueType> SlotTypes => _types;
    public IReadOnlyList<string> BlockNames => _blocks;

    // Stable wire-free ABI tags: 1=i64, 2=bool, copied so mutable caller
    // arrays cannot change the verified layout's type authority.
    public byte[] EncodeTypeTags()
    {
        var result = new byte[_types.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = _types[i] switch
            {
                IrValueType.I64 => 1,
                IrValueType.Bool => 2,
                _ => throw new FormatException("Unsupported actor scalar slot type")
            };
        return result;
    }

    public static CfgActorScalarFramePlan Create(VerifiedCfgFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (function.Parameters.Count != 0)
            throw new FormatException("Actor scalar frame ABI v1 cannot lower function parameters");
        if (function.Blocks.Count == 0 || function.Blocks.Count > MaxBlocks)
            throw new FormatException("Actor scalar frame ABI block budget exceeded");

        // Already verified values must be dense, unique, and scalar-only.
        // Check independently so the ABI never trusts a future verifier change.
        var definitions = new Dictionary<int, IrValueType>();
        var names = new string[function.Blocks.Count];
        int entry = -1;
        for (int blockIndex = 0; blockIndex < function.Blocks.Count; blockIndex++)
        {
            CfgBlock block = function.Blocks[blockIndex];
            names[blockIndex] = block.Name;
            if (block.Name == function.Entry) entry = blockIndex;
            foreach (var argument in block.Parameters)
                Add(argument.Id, argument.Type);
            foreach (CfgInstruction instruction in block.Instructions)
            {
                if (instruction.Opcode is CfgOpcode.Argument or CfgOpcode.Call)
                    throw new FormatException(
                        "Actor scalar frame ABI cannot lower calls or argument effects");
                Add(instruction.ResultId, instruction.Type);
            }
        }

        if (entry < 0 || definitions.Count == 0 || definitions.Count > MaxValueSlots)
            throw new FormatException("Actor scalar frame ABI slot or entry budget exceeded");
        var types = new IrValueType[definitions.Count];
        for (int i = 0; i < types.Length; i++)
        {
            if (!definitions.TryGetValue(i, out IrValueType type))
                throw new FormatException("Actor scalar frame ABI requires dense SSA IDs");
            types[i] = type;
        }
        return new CfgActorScalarFramePlan(types, names, entry);

        void Add(int id, IrValueType type)
        {
            if (id < 0 || id >= MaxValueSlots ||
                type is not (IrValueType.I64 or IrValueType.Bool) ||
                !definitions.TryAdd(id, type))
                throw new FormatException("Actor scalar frame ABI invalid SSA slot");
        }
    }

    public IrValueType SlotType(int id)
    {
        if (id < 0 || id >= _types.Count)
            throw new ArgumentOutOfRangeException(nameof(id));
        return _types[id];
    }

    public int BlockIndex(string blockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockName);
        for (int i = 0; i < _blocks.Count; i++)
            if (string.Equals(_blocks[i], blockName, StringComparison.Ordinal))
                return i;
        throw new FormatException("Unknown actor scalar frame CFG block");
    }
}
