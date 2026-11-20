namespace Ores.Net.Compiler;

/// <summary>
/// Immutable compiler-only transfer edge. The source values are read before
/// the destination's block parameters are written; this prevents a loop-edge
/// swap from destroying one of its still-live inputs.
/// </summary>
public sealed class CfgActorScalarBoundaryEdge
{
    internal CfgActorScalarBoundaryEdge(string kind, string target,
        int[] sourceIds, int[] destinationIds)
    {
        Kind = kind;
        Target = target;
        SourceValueIds = Array.AsReadOnly((int[])sourceIds.Clone());
        DestinationParameterIds = Array.AsReadOnly((int[])destinationIds.Clone());
    }

    public string Kind { get; }
    public string Target { get; }
    public IReadOnlyList<int> SourceValueIds { get; }
    public IReadOnlyList<int> DestinationParameterIds { get; }
}

/// <summary>
/// Values needed on entry, and those that must survive until a successor
/// transfer, for one verified CFG block. LiveOut is the conservative union
/// across outgoing edges; it is not a resume ticket or emitted CIL.
/// </summary>
public sealed class CfgActorScalarBoundary
{
    internal CfgActorScalarBoundary(string name, int[] liveIn, int[] liveOut,
        CfgActorScalarBoundaryEdge[] edges)
    {
        Name = name;
        LiveInValueIds = Array.AsReadOnly((int[])liveIn.Clone());
        LiveOutValueIds = Array.AsReadOnly((int[])liveOut.Clone());
        Edges = Array.AsReadOnly((CfgActorScalarBoundaryEdge[])edges.Clone());
    }

    public string Name { get; }
    public IReadOnlyList<int> LiveInValueIds { get; }
    public IReadOnlyList<int> LiveOutValueIds { get; }
    public IReadOnlyList<CfgActorScalarBoundaryEdge> Edges { get; }
}

/// <summary>
/// Bounded compiler-owned scalar spill boundary analysis. Built only from a
/// deep-frozen VerifiedCfgFunction and the admitted ActorScalarFrameAbiV1
/// layout. This is the missing liveness and simultaneous block-parameter
/// transfer contract for future generated NativeAOT-compatible CIL.
/// It does NOT generate runtime suspension, guest actor execution, or cleanup.
/// </summary>
public sealed class CfgActorScalarBoundaryPlan
{
    private readonly Dictionary<string, CfgActorScalarBoundary> _byBlock;

    private CfgActorScalarBoundaryPlan(CfgActorScalarFramePlan frame,
        CfgActorScalarBoundary[] blocks)
    {
        Frame = frame;
        Blocks = Array.AsReadOnly((CfgActorScalarBoundary[])blocks.Clone());
        _byBlock = blocks.ToDictionary(b => b.Name, StringComparer.Ordinal);
    }

    public CfgActorScalarFramePlan Frame { get; }
    public IReadOnlyList<CfgActorScalarBoundary> Blocks { get; }

    public CfgActorScalarBoundary Block(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _byBlock.TryGetValue(name, out var result)
            ? result
            : throw new FormatException("Unknown actor scalar boundary block");
    }

    public static CfgActorScalarBoundaryPlan Create(VerifiedCfgFunction verified)
    {
        // Fail closed on arguments, host calls, unsupported scalar types,
        // sparse SSA IDs and the native frame/block limits BEFORE liveness.
        CfgActorScalarFramePlan frame = CfgActorScalarFramePlan.Create(verified);
        int count = verified.Blocks.Count;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
            if (!indexes.TryAdd(verified.Blocks[i].Name, i))
                throw new FormatException("Duplicate actor scalar boundary block");

        var uses = new HashSet<int>[count];
        var definitions = new HashSet<int>[count];
        var successors = new List<CfgActorScalarBoundaryEdge>[count];
        var liveIn = new HashSet<int>[count];
        var liveOut = new HashSet<int>[count];

        for (int i = 0; i < count; i++)
        {
            uses[i] = new HashSet<int>();
            definitions[i] = new HashSet<int>();
            successors[i] = new List<CfgActorScalarBoundaryEdge>();
            liveIn[i] = new HashSet<int>();
            liveOut[i] = new HashSet<int>();

            var block = verified.Blocks[i];
            foreach (var parameter in block.Parameters)
                definitions[i].Add(parameter.Id);

            void Use(int id)
            {
                // The verified SSA graph permits a dominating block's value
                // to flow to a successor without a block-parameter edge.
                // Those values must remain live across the physical boundary.
                _ = frame.SlotType(id);
                if (!definitions[i].Contains(id))
                    uses[i].Add(id);
            }

            foreach (var operation in block.Instructions)
            {
                switch (operation.Opcode)
                {
                    case CfgOpcode.ConstantI64:
                    case CfgOpcode.ConstantBool:
                        break;
                    case CfgOpcode.CheckedAdd:
                    case CfgOpcode.CheckedSubtract:
                    case CfgOpcode.CheckedMultiply:
                    case CfgOpcode.CheckedDivide:
                    case CfgOpcode.EqualI64:
                    case CfgOpcode.NotEqualI64:
                    case CfgOpcode.LessThanI64:
                        Use(operation.Left ?? throw new FormatException(
                            "Missing verified actor scalar left operand"));
                        Use(operation.Right ?? throw new FormatException(
                            "Missing verified actor scalar right operand"));
                        break;
                    default:
                        throw new FormatException(
                            "Actor scalar continuation cannot lower host calls or effects");
                }
                definitions[i].Add(operation.ResultId);
            }

            void Edge(string kind, string target, IReadOnlyList<int> inputs)
            {
                if (!indexes.TryGetValue(target, out int targetIndex))
                    throw new FormatException("Unknown actor scalar successor");
                var parameters = verified.Blocks[targetIndex].Parameters;
                if (inputs.Count != parameters.Count)
                    throw new FormatException("Actor scalar boundary argument mismatch");
                var incoming = new int[inputs.Count];
                var targetIds = new int[inputs.Count];
                for (int j = 0; j < inputs.Count; j++)
                {
                    incoming[j] = inputs[j];
                    targetIds[j] = parameters[j].Id;
                    if (frame.SlotType(incoming[j]) != frame.SlotType(targetIds[j]))
                        throw new FormatException("Actor scalar boundary type mismatch");
                    Use(incoming[j]);
                }
                successors[i].Add(new CfgActorScalarBoundaryEdge(
                    kind, target, incoming, targetIds));
            }

            switch (block.Terminator)
            {
                case CfgReturn returned:
                    Use(returned.ValueId);
                    break;
                case CfgJump jump:
                    Edge("jump", jump.Target, jump.Arguments);
                    break;
                case CfgBranch branch:
                    Use(branch.Condition);
                    Edge("true", branch.TrueTarget, branch.TrueArguments);
                    Edge("false", branch.FalseTarget, branch.FalseArguments);
                    break;
                default:
                    throw new FormatException("Unsupported actor scalar CFG terminator");
            }
        }

        // Backward monotone dataflow: in = localUses + (out - localDefs);
        // out = successor in + the source operands of this edge's parallel
        // parameter assignments. The finite 128-block/256-value universe
        // guarantees termination even for loops and self-edges.
        bool changed;
        do
        {
            changed = false;
            for (int i = count - 1; i >= 0; i--)
            {
                var nextOut = new HashSet<int>();
                foreach (var edge in successors[i])
                {
                    nextOut.UnionWith(liveIn[indexes[edge.Target]]);
                    nextOut.UnionWith(edge.SourceValueIds);
                }
                var nextIn = new HashSet<int>(nextOut);
                nextIn.ExceptWith(definitions[i]);
                nextIn.UnionWith(uses[i]);
                if (!nextOut.SetEquals(liveOut[i]))
                {
                    liveOut[i] = nextOut;
                    changed = true;
                }
                if (!nextIn.SetEquals(liveIn[i]))
                {
                    liveIn[i] = nextIn;
                    changed = true;
                }
            }
        } while (changed);

        // Entry cannot depend on any uninitialized SSA value. This also
        // independently catches accidental future weakening of CFG dominance.
        int entry = indexes[verified.Entry];
        if (liveIn[entry].Count != 0)
            throw new FormatException("Actor scalar entry reads uninitialized spill");

        var result = new CfgActorScalarBoundary[count];
        for (int i = 0; i < count; i++)
            result[i] = new CfgActorScalarBoundary(verified.Blocks[i].Name,
                liveIn[i].OrderBy(id => id).ToArray(),
                liveOut[i].OrderBy(id => id).ToArray(),
                successors[i].ToArray());
        return new CfgActorScalarBoundaryPlan(frame, result);
    }
}
