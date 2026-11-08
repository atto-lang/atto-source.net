namespace Ores.Net.Compiler;

/// <summary>
/// Fail-closed prototype admission for untrusted scalar CIL until the
/// 128-backedge checkpoint pass has been verified. Only an acyclic, call-free,
/// <=4096-weighted-operation verified CFG can be emitted by this opt-in entry.
/// This is NOT an OS security boundary or a substitute for a tenant watchdog.
/// </summary>
internal static class UntrustedLoopFreeCfgAdmission
{
    internal const int MaxInstructions = 4096;

    internal static void Require(VerifiedCfgFunction graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var blocks = graph.Blocks;
        if (blocks.Count == 0 || blocks.Count > OresCfgVerifier.MaxBlocks)
            throw new FormatException("Untrusted CFG requires bounded blocks");
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
            if (!positions.TryAdd(blocks[i].Name, i))
                throw new FormatException("Duplicate untrusted CFG block");
        int totalWeightedOperations = 0;
        var edges = new int[blocks.Count][];
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            // Account conservatively for *emitted* work, not only SSA ops.
            // Each CFG edge copies arguments into scratch slots and then into
            // target locals (four CIL accesses each). Both branch alternatives
            // are included so any executed path costs <= the admitted sum.
            foreach (var op in block.Instructions)
            {
                // Real CfgCilEmitter opcode counts: loads + arithmetic/compare
                // + final result store. Do not count an expensive i64 operation
                // as just one instruction when claiming a bounded CIL path.
                int cilCost = op.Opcode switch
                {
                    CfgOpcode.Argument or CfgOpcode.ConstantI64 or
                    CfgOpcode.ConstantBool => 2,
                    CfgOpcode.CheckedAdd or CfgOpcode.CheckedSubtract or
                    CfgOpcode.CheckedMultiply or CfgOpcode.CheckedDivide or
                    CfgOpcode.EqualI64 or CfgOpcode.LessThanI64 => 4,
                    CfgOpcode.NotEqualI64 => 5,
                    CfgOpcode.Call => throw new FormatException(
                        "Untrusted CFG calls need hard guest fuel instrumentation"),
                    _ => throw new FormatException("Untrusted CFG opcode unsupported")
                };
                totalWeightedOperations = checked(totalWeightedOperations + cilCost);
            }
            string[] targets;
            IReadOnlyList<int>[] edgeArguments;
            switch (block.Terminator)
            {
                case CfgReturn:
                    totalWeightedOperations = checked(totalWeightedOperations + 2);
                    targets = Array.Empty<string>();
                    edgeArguments = Array.Empty<IReadOnlyList<int>>();
                    break;
                case CfgJump jump:
                    totalWeightedOperations = checked(totalWeightedOperations + 1);
                    targets = new[] { jump.Target };
                    edgeArguments = new[] { jump.Arguments };
                    break;
                case CfgBranch branch:
                    totalWeightedOperations = checked(totalWeightedOperations + 2);
                    targets = new[] { branch.TrueTarget, branch.FalseTarget };
                    edgeArguments = new[] { branch.TrueArguments, branch.FalseArguments };
                    break;
                default:
                    throw new FormatException("Untrusted CFG terminator unsupported");
            }
            foreach (var arguments in edgeArguments)
                totalWeightedOperations = checked(
                    totalWeightedOperations + 4 * arguments.Count + 1);
            if (totalWeightedOperations > MaxInstructions)
                throw new FormatException("Untrusted CFG instruction budget exceeded");
            edges[i] = targets.Select(target => positions.TryGetValue(target, out var position)
                ? position : throw new FormatException("Untrusted CFG target not verified")).ToArray();
        }

        // DFS accepts diamonds and backward textual edges that are acyclic,
        // but rejects real control-flow cycles regardless of block ordering.
        var state = new byte[blocks.Count];
        void Visit(int index)
        {
            if (state[index] == 1)
                throw new FormatException("Untrusted CFG loop requires mandatory checkpoints");
            if (state[index] == 2) return;
            state[index] = 1;
            foreach (int next in edges[index]) Visit(next);
            state[index] = 2;
        }
        Visit(positions[graph.Entry]);
        if (state.Any(value => value == 0))
            throw new FormatException("Untrusted CFG contains unreachable blocks");
    }
}
