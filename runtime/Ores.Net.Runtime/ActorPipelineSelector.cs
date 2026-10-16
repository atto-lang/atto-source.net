using System.Text;

namespace Ores.Net.Runtime;

// Host-only selector contract v1. This is not an ActorRef, message transport,
// Sendable proof, compiled self.* intrinsic, or reply capability.
public enum PipelineRole { Readable, Transform, Writable, Duplex }
public sealed record PipelineStage(string Id, string ActorType, PipelineRole Role,
    string Inbound, string Outbound);
public sealed record PipelineEdge(string Source, string Target, string Payload);

public static class ActorPipelineKey
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Encode(string text, int maximum, bool optional = false,
        bool forbidNul = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        if ((!optional && text.Length == 0) || text.Length > maximum ||
            (forbidNul && text.Contains('\0')))
            throw new ArgumentException("Invalid routing component");
        try
        {
            var bytes = StrictUtf8.GetBytes(text);
            if (bytes.Length > maximum)
                throw new ArgumentException("Routing component byte limit");
            return bytes;
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("Malformed routing Unicode", error);
        }
    }

    // FNV-1a64 over EXACT strict UTF-8 bytes; never use this for authorization.
    public static int Slot(string key, int edgeCount)
    {
        if (edgeCount is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(edgeCount));
        var encoded = Encode(key, 4096, optional: true, forbidNul: false);
        ulong hash = 14695981039346656037UL;
        foreach (byte value in encoded)
            hash = unchecked((hash ^ value) * 1099511628211UL);
        return (int)(hash % (ulong)edgeCount);
    }
}

public sealed class ActorPipelineSelector
{
    private readonly Dictionary<string, PipelineEdge[]> _outgoing =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _cursors = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private ulong _generation;

    public ActorPipelineSelector(IReadOnlyList<PipelineStage> stages,
        IReadOnlyList<PipelineEdge> edges, ulong generation = 1)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(edges);
        if (stages.Count is < 2 or > 1024 || edges.Count is < 1 or > 4096 ||
            generation == 0)
            throw new ArgumentException("Invalid pipeline graph budget");
        _generation = generation;
        var ids = new Dictionary<string, PipelineStage>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        var draft = new Dictionary<string, List<PipelineEdge>>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            ActorPipelineKey.Encode(stage.Id, 256);
            ActorPipelineKey.Encode(stage.ActorType, 256);
            ActorPipelineKey.Encode(stage.Inbound, 256, optional: true);
            ActorPipelineKey.Encode(stage.Outbound, 256, optional: true);
            if (!ids.TryAdd(stage.Id, stage))
                throw new ArgumentException("Duplicate pipeline actor instance");
            bool hasIn = stage.Inbound.Length != 0, hasOut = stage.Outbound.Length != 0;
            bool roleValid = stage.Role switch
            {
                PipelineRole.Readable => !hasIn && hasOut,
                PipelineRole.Writable => hasIn && !hasOut,
                PipelineRole.Transform or PipelineRole.Duplex => hasIn && hasOut,
                _ => false
            };
            if (!roleValid) throw new ArgumentException("Invalid pipeline stage ports");
            incoming.Add(stage.Id, 0);
            draft.Add(stage.Id, new List<PipelineEdge>());
        }
        var pairs = new HashSet<(string, string)>();
        foreach (var edge in edges)
        {
            ArgumentNullException.ThrowIfNull(edge);
            ActorPipelineKey.Encode(edge.Source, 256);
            ActorPipelineKey.Encode(edge.Target, 256);
            ActorPipelineKey.Encode(edge.Payload, 256);
            if (!ids.TryGetValue(edge.Source, out var source) ||
                !ids.TryGetValue(edge.Target, out var target) ||
                edge.Source == edge.Target ||
                source.Role == PipelineRole.Writable ||
                target.Role == PipelineRole.Readable ||
                edge.Payload != source.Outbound || edge.Payload != target.Inbound)
                throw new ArgumentException("Invalid or mistyped pipeline edge");
            if (!pairs.Add((edge.Source, edge.Target)))
                throw new ArgumentException("Duplicate pipeline edge");
            draft[edge.Source].Add(edge);
            incoming[edge.Target]++;
        }
        var ready = new Queue<string>();
        foreach (var stage in stages)
        {
            int reads = incoming[stage.Id], writes = draft[stage.Id].Count;
            if (reads == 0 && writes == 0 ||
                stage.Role == PipelineRole.Readable && writes == 0 ||
                stage.Role == PipelineRole.Writable && reads == 0 ||
                stage.Role == PipelineRole.Transform && (reads == 0 || writes == 0))
                throw new ArgumentException("Pipeline stage missing required connection");
            if (reads == 0)
            {
                ready.Enqueue(stage.Id);
                if (writes != 0 && stage.Role is PipelineRole.Readable or PipelineRole.Duplex)
                    _roots.Add(stage.Id);
            }
        }
        if (_roots.Count == 0) throw new ArgumentException("Missing pipeline ingress root");
        int visited = 0;
        while (ready.Count != 0)
        {
            var id = ready.Dequeue();
            visited++;
            foreach (var edge in draft[id])
                if (--incoming[edge.Target] == 0) ready.Enqueue(edge.Target);
        }
        if (visited != stages.Count) throw new ArgumentException("Cyclic pipeline graph");
        foreach (var (id, selected) in draft)
            _outgoing.Add(id, selected.ToArray());
    }

    public ulong Generation
    {
        get { lock (_gate) return _generation; }
    }

    public void RequireIngressRoot(string source)
    {
        if (!_roots.Contains(source))
            throw new ArgumentException("Not an authorized graph ingress root");
    }

    private PipelineEdge[] Connected(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_outgoing.TryGetValue(source, out var edges) || edges.Length == 0)
            throw new ArgumentException("Source has no connected downstream");
        return edges;
    }

    public PipelineEdge Forward(string source)
    {
        var edges = Connected(source);
        if (edges.Length != 1) throw new ArgumentException("Ambiguous forward");
        return edges[0];
    }

    // Defensive copy: a caller must never rewrite the trusted supervisor plan.
    public IReadOnlyList<PipelineEdge> ForwardAll(string source) =>
        Array.AsReadOnly((PipelineEdge[])Connected(source).Clone());

    public PipelineEdge ForwardRoundRobin(string source)
    {
        lock (_gate)
        {
            var edges = Connected(source);
            _cursors.TryGetValue(source, out int cursor);
            var selected = edges[cursor];
            _cursors[source] = (cursor + 1) % edges.Length;
            return selected;
        }
    }

    public PipelineEdge ForwardTo(string source, string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var edge in Connected(source))
            if (edge.Target == target) return edge;
        throw new ArgumentException("Target is not connected to source");
    }

    public PipelineEdge ForwardByKey(string source, string key)
    {
        var edges = Connected(source);
        return edges[ActorPipelineKey.Slot(key, edges.Length)];
    }

    public void AdvanceGeneration(ulong successor, bool quiescent, bool aborted)
    {
        lock (_gate)
        {
            if (successor <= _generation || (!quiescent && !aborted))
                throw new ArgumentException("Pipeline generation barrier required");
            _generation = successor;
            _cursors.Clear();
        }
    }
}
