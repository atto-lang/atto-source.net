namespace Ores.Net.Compiler;

/// <summary>
/// Fail-closed prototype admission for untrusted scalar CIL until the
/// 128-backedge checkpoint pass has been verified. Only an acyclic, call-free,
/// <=4096-instruction verified CFG can be emitted by this opt-in entry.
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
        int totalInstructions = 0;
        var edges = new int[blocks.Count][];
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            totalInstructions = checked(totalInstructions + block.Instructions.Count);
            if (totalInstructions > MaxInstructions)
                throw new FormatException("Untrusted CFG instruction budget exceeded");
            if (block.Instructions.Any(op => op.Opcode == CfgOpcode.Call))
                throw new FormatException("Untrusted CFG calls need hard guest fuel instrumentation");
            string[] targets = block.Terminator switch
            {
                CfgReturn => Array.Empty<string>(),
                CfgJump jump => new[] { jump.Target },
                CfgBranch branch => new[] { branch.TrueTarget, branch.FalseTarget },
                _ => throw new FormatException("Untrusted CFG terminator unsupported")
            };
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
