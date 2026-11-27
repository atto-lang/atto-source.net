using System;
using System.Collections.Generic;

namespace Ores.Net.Compiler;

// Internal compiler metadata only. No source modifier or runtime authority.
public readonly record struct RuntimeParkingEffects(bool ExplicitSuspend, bool RuntimePark, bool Unresolved)
{
    public bool RequiresResumableFrame => ExplicitSuspend || RuntimePark || Unresolved;
    public bool RequiresSuspendAnnotation => ExplicitSuspend;
    public bool SafeWithExclusiveLoan => !RequiresResumableFrame;
    internal RuntimeParkingEffects Union(RuntimeParkingEffects other) => new(
        ExplicitSuspend || other.ExplicitSuspend, RuntimePark || other.RuntimePark,
        Unresolved || other.Unresolved);
}

public static class RuntimeParkingGraph
{
    public const int MaxCallables = 1024;
    public const int MaxEdges = 16384;
    public const int MaxEdgesPerCallable = 1024;
    // null means dynamic/foreign/unresolved. IDs must come from checked IR.
    public sealed record Callable(RuntimeParkingEffects Direct, int?[] Calls);

    public static RuntimeParkingEffects[] Infer(Callable[] nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Length > MaxCallables) throw new FormatException("parking callable budget");
        // Freeze bounded input before solving; no caller-owned graph references
        // survive into the result. Compiler input is trusted, not a security token.
        var snapshot = (Callable[])nodes.Clone();
        var rows = new RuntimeParkingEffects[snapshot.Length];
        var reverse = new List<int>[snapshot.Length];
        for (int i = 0; i < reverse.Length; i++) reverse[i] = new();
        int edges = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            var node = snapshot[i] ?? throw new FormatException("null parking callable");
            var calls = node.Calls ?? throw new FormatException("null parking calls");
            if (calls.Length > MaxEdgesPerCallable || calls.Length > MaxEdges - edges)
                throw new FormatException("parking edge budget");
            edges += calls.Length;
            rows[i] = node.Direct;
            foreach (int? target in (int?[])calls.Clone())
            {
                if (target is null) rows[i] = rows[i] with { Unresolved = true };
                else if (target.Value < 0 || target.Value >= snapshot.Length)
                    throw new FormatException("invalid resolved parking target");
                else reverse[target.Value].Add(i);
            }
        }
        var work = new Queue<int>();
        var queued = new bool[rows.Length];
        for (int i = 0; i < rows.Length; i++) { work.Enqueue(i); queued[i] = true; }
        while (work.Count != 0)
        {
            int callee = work.Dequeue(); queued[callee] = false;
            foreach (int caller in reverse[callee])
            {
                var next = rows[caller].Union(rows[callee]);
                if (next == rows[caller]) continue;
                rows[caller] = next;
                if (!queued[caller]) { work.Enqueue(caller); queued[caller] = true; }
            }
        }
        return rows;
    }
}
