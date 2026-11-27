using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Atto.MachineInspection;

// Graph-local metadata only, never a runtime authority or persistent identity.
public sealed record Edge(string Event, int Target);
public sealed record Node(string Name, bool Terminal, IReadOnlyList<Edge> Edges);

public sealed class FlatMachineTable
{
    private const int Limit = 256;
    private readonly (int Slot, int Target)[][] rows;
    public int Initial { get; }
    public ReadOnlyCollection<string> Names { get; }
    public ReadOnlyCollection<string> Events { get; }

    private FlatMachineTable(int initial, string[] names, string[] events,
                             (int Slot, int Target)[][] rows)
    {
        Initial = initial;
        Names = Array.AsReadOnly(names);
        Events = Array.AsReadOnly(events);
        this.rows = rows;
    }

    private static void Require(bool ok)
    {
        if (!ok) throw new ArgumentException("E_MACHINE_TABLE");
    }

    private static bool ValidName(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 1024) return false;
        static bool Letter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
        return Letter(s[0]) && s.All(c => Letter(c) || c is >= '0' and <= '9');
    }

    // Input is trusted compiler-owned metadata, not an arbitrary external collection.
    public static FlatMachineTable Verify(int initial, IReadOnlyList<Node> nodes)
    {
        Require(nodes.Count is > 0 and <= Limit && initial >= 0 && initial < nodes.Count);
        var names = new List<string>();
        var events = new SortedSet<string>(StringComparer.Ordinal);
        var graph = new List<Edge[]>();
        int count = 0;
        foreach (var node in nodes)
        {
            Require(ValidName(node.Name) &&
                (names.Count == 0 || StringComparer.Ordinal.Compare(names[^1], node.Name) < 0));
            Require(node.Edges.Count <= Limit - count && (!node.Terminal || node.Edges.Count == 0));
            names.Add(node.Name);
            var row = node.Edges.ToArray();
            string previous = "";
            foreach (var edge in row)
            {
                Require(ValidName(edge.Event) && StringComparer.Ordinal.Compare(previous, edge.Event) < 0 &&
                    edge.Target >= 0 && edge.Target < nodes.Count);
                previous = edge.Event;
                events.Add(edge.Event);
                ++count;
            }
            graph.Add(row);
        }
        var pending = new Stack<int>();
        var seen = new bool[names.Count];
        pending.Push(initial);
        while (pending.TryPop(out int node))
        {
            if (seen[node]) continue;
            seen[node] = true;
            foreach (var edge in graph[node]) pending.Push(edge.Target);
        }
        Require(seen.All(v => v));
        string[] ordered = events.ToArray();
        var slots = ordered.Select((e, i) => (Event: e, Slot: i))
            .ToDictionary(x => x.Event, x => x.Slot, StringComparer.Ordinal);
        var rows = graph.Select(row => row.Select(e => (slots[e.Event], e.Target)).ToArray()).ToArray();
        return new FlatMachineTable(initial, names.ToArray(), ordered, rows);
    }

    public int? Successor(int node, int slot)
    {
        Require(node >= 0 && node < rows.Length && slot >= 0 && slot < Events.Count);
        foreach (var edge in rows[node]) if (edge.Slot == slot) return edge.Target;
        return null;
    }
}
