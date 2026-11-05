namespace Ores.Net.Compiler;

/// <summary>
/// Read-only syntax/SSA audit of verified scalar CFG return/call adjacency.
/// This intentionally DOES NOT authorize the CIL tail. prefix or NativeAOT TCO:
/// exception regions, ABI identity, ownership cleanup and backend lowering
/// must be proven separately before this can become a code-generation input.
/// </summary>
public sealed record CfgTailCallCandidate(string BlockName, string Target, int ResultId);

public static class CfgTailCallCandidates
{
    public static IReadOnlyList<CfgTailCallCandidate> Inspect(VerifiedCfgFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var candidates = new List<CfgTailCallCandidate>();
        foreach (CfgBlock block in function.Blocks)
        {
            if (block.Terminator is not CfgReturn returned || block.Instructions.Count == 0)
                continue;
            CfgInstruction last = block.Instructions[^1];
            // Reject all results that undergo even one intervening operation.
            // This probe is restricted to already verified, direct scalar calls.
            if (last.Opcode != CfgOpcode.Call ||
                last.ResultId != returned.ValueId ||
                last.Type != function.ResultType ||
                string.IsNullOrWhiteSpace(last.Target))
                continue;
            candidates.Add(new CfgTailCallCandidate(block.Name, last.Target, last.ResultId));
        }
        return Array.AsReadOnly(candidates.ToArray());
    }
}
