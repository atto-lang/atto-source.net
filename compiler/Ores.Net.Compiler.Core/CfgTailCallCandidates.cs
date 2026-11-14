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

/**
 * A stricter inventory that preserves only returned direct calls whose caller
 * and callee have exactly the same scalar ABI signature. This is still
 * analysis-only: even an exact scalar signature never authorizes CIL tail.,
 * NativeAOT tail recursion, EH-region transfer, or ownership cleanup.
 */
public sealed record CfgSameSignatureTailCandidate(
    string Caller, string Target, string BlockName, int ResultId);

public static class CfgSameSignatureTailCandidates
{
    public static IReadOnlyList<CfgSameSignatureTailCandidate> Inspect(VerifiedCfgModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var declarations = new Dictionary<string, VerifiedCfgFunction>(StringComparer.Ordinal);
        foreach (VerifiedCfgFunction helper in module.Helpers)
        {
            if (!declarations.TryAdd(helper.Name, helper))
                throw new FormatException("Duplicate tail-call helper identity");
        }

        var results = new List<CfgSameSignatureTailCandidate>();
        void Examine(VerifiedCfgFunction caller)
        {
            foreach (CfgTailCallCandidate candidate in CfgTailCallCandidates.Inspect(caller))
            {
                if (!declarations.TryGetValue(candidate.Target, out var callee) ||
                    caller.ResultType != callee.ResultType ||
                    caller.Parameters.Count != callee.Parameters.Count)
                    continue;
                bool identical = true;
                for (int i = 0; i < caller.Parameters.Count; i++)
                {
                    if (caller.Parameters[i] != callee.Parameters[i])
                    {
                        identical = false;
                        break;
                    }
                }
                if (identical)
                    results.Add(new CfgSameSignatureTailCandidate(
                        caller.Name, candidate.Target, candidate.BlockName, candidate.ResultId));
            }
        }
        foreach (VerifiedCfgFunction helper in module.Helpers) Examine(helper);
        Examine(module.Main);
        return Array.AsReadOnly(results.ToArray());
    }
}
