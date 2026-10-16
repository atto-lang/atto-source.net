using System.Text;

namespace Ores.Net.Runtime;

/// <summary>
/// Host-only typed actor pipeline selector, NOT an ActorRef, a scheduler,
/// a mailbox sender or source-language self.* lowering.
/// </summary>
public sealed class ActorPipelinePolicy
{
    public sealed record Stage(string Id, string Type, string Role,
        string Inbound, string Outbound);
    public sealed record Edge(string Source, string Target, string Payload);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Dictionary<string, Edge[]> _outgoing;
    private readonly Dictionary<string, int> _cursor = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _closed;

    private static byte[] Utf8Bytes(string? value, int limit = 256,
        bool optional = false, bool allowNul = false)
    {
        if (value is null || (!optional && value.Length == 0) || (!allowNul && value.Contains('\0')))
            throw new ArgumentException("Invalid pipeline component");
        if (value.Length > limit) throw new ArgumentException("Pipeline component too large");
        try
        {
            var encoded = Utf8.GetBytes(value);
            if (encoded.Length > limit) throw new ArgumentException("Pipeline UTF-8 byte limit");
            return encoded;
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("Invalid pipeline Unicode", error);
        }
    }

    public static ulong HashKey(string key)
    {
        byte[] bytes = Utf8Bytes(key, 4096, optional: true, allowNul: true);
        ulong hash = 0xcbf29ce484222325UL;
        foreach (byte b in bytes)
            hash = unchecked((hash ^ b) * 0x100000001b3UL);
        return hash;
    }

    public ActorPipelinePolicy(IReadOnlyList<Stage> stages, IReadOnlyList<Edge> edges)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(edges);
        if (stages.Count is < 2 or > 1024 || edges.Count is < 1 or > 4096)
            throw new ArgumentException("Invalid pipeline graph budget");

        var byId = new Dictionary<string, Stage>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        var routes = new Dictionary<string, List<Edge>>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            Utf8Bytes(stage.Id); Utf8Bytes(stage.Type);
            Utf8Bytes(stage.Inbound, optional: true);
            Utf8Bytes(stage.Outbound, optional: true);
            if (!byId.TryAdd(stage.Id, stage))
                throw new ArgumentException("Duplicate pipeline actor instance");
            bool valid = stage.Role switch
            {
                "readable" => stage.Inbound.Length == 0 && stage.Outbound.Length != 0,
                "writable" => stage.Inbound.Length != 0 && stage.Outbound.Length == 0,
                "transform" or "duplex" => stage.Inbound.Length != 0 && stage.Outbound.Length != 0,
                _ => false
            };
            if (!valid) throw new ArgumentException("Invalid typed pipeline role");
            incoming[stage.Id] = 0;
        }
        var pairs = new HashSet<(string, string)>();
        foreach (var edge in edges)
        {
            Utf8Bytes(edge.Source); Utf8Bytes(edge.Target); Utf8Bytes(edge.Payload);
            if (!byId.TryGetValue(edge.Source, out var source) ||
                !byId.TryGetValue(edge.Target, out var target) ||
                edge.Source == edge.Target ||
                source.Role == "writable" || target.Role == "readable" ||
                edge.Payload != source.Outbound || edge.Payload != target.Inbound)
                throw new ArgumentException("Unknown or mistyped pipeline edge");
            if (!pairs.Add((edge.Source, edge.Target)))
                throw new ArgumentException("Duplicate pipeline edge");
            if (!routes.TryGetValue(edge.Source, out var list))
                routes[edge.Source] = list = new List<Edge>();
            list.Add(new Edge(edge.Source, edge.Target, edge.Payload));
            incoming[edge.Target]++;
        }
        var degrees = new Dictionary<string, int>(incoming, StringComparer.Ordinal);
        var ready = new Queue<string>();
        bool root = false;
        foreach (var stage in stages)
        {
            int n = incoming[stage.Id], m = routes.GetValueOrDefault(stage.Id)?.Count ?? 0;
            if ((n == 0 && m == 0) || (stage.Role == "readable" && m == 0) ||
                (stage.Role == "writable" && n == 0) ||
                (stage.Role == "transform" && (n == 0 || m == 0)))
                throw new ArgumentException("Pipeline actor missing required port");
            if (n == 0)
            {
                ready.Enqueue(stage.Id);
                if (stage.Role is "readable" or "duplex") root = true;
            }
        }
        if (!root) throw new ArgumentException("Pipeline missing ingress root");
        int visited = 0;
        while (ready.Count > 0)
        {
            string current = ready.Dequeue(); visited++;
            if (!routes.TryGetValue(current, out var outgoing)) continue;
            foreach (var edge in outgoing)
            {
                int remaining = degrees[edge.Target] - 1;
                degrees[edge.Target] = remaining;
                if (remaining == 0) ready.Enqueue(edge.Target);
            }
        }
        if (visited != stages.Count) throw new ArgumentException("Cyclic actor pipeline");
        _outgoing = routes.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private Edge[] Connected(string source)
    {
        if (_closed) throw new InvalidOperationException("Routing generation retired");
        if (!_outgoing.TryGetValue(source, out var edges) || edges.Length == 0)
            throw new ArgumentException("Source has no downstream route");
        return edges;
    }
    public IReadOnlyList<Edge> ForwardAll(string source)
    {
        lock (_gate) return Array.AsReadOnly((Edge[])Connected(source).Clone());
    }
    public Edge Forward(string source)
    {
        lock (_gate)
        {
            var choices = Connected(source);
            if (choices.Length != 1) throw new ArgumentException("Ambiguous forward");
            return choices[0];
        }
    }
    public Edge ForwardRoundRobin(string source)
    {
        lock (_gate)
        {
            var choices = Connected(source);
            int cursor = _cursor.GetValueOrDefault(source);
            _cursor[source] = (cursor + 1) % choices.Length;
            return choices[cursor];
        }
    }
    public Edge ForwardTo(string source, string target)
    {
        lock (_gate)
            return Connected(source).FirstOrDefault(x => x.Target == target)
                ?? throw new ArgumentException("Target is not connected");
    }
    public Edge ForwardByKey(string source, string key)
    {
        lock (_gate)
        {
            // Reject unknown/retired routes before parsing untrusted key bytes.
            var choices = Connected(source);
            ulong hash = HashKey(key);
            return choices[(int)(hash % (ulong)choices.Length)];
        }
    }
    public void Close()
    {
        lock (_gate) _closed = true;
    }
}
