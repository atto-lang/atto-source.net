using System.Collections.ObjectModel;

namespace Atto.Cil.Compiler;

// Typed compiler pre-HIR only. This does NOT lower an actor, allocate a
// mailbox, grant an ActorRef, activate a PipeHandle or execute Atto guest code.
// These descriptors must be produced by a trusted nominal typechecker and
// authenticated TenantVM registry, never accepted as user-supplied authority.
internal enum DirectedPipeDirection { Pipe, Rpipe }
internal enum DirectedPipeEndpoint { Actor, Channel, StreamReadable, StreamSink, StreamDuplex }
internal sealed record DirectedPipeStage(string Id, string Tenant,
    DirectedPipeEndpoint Endpoint, string? Receives, string? Emits);
internal sealed record DirectedPipeEdge(int SourceIndex, int SinkIndex,
    string Source, string Sink, string Payload);
internal sealed record DirectedPipePlan(DirectedPipeDirection Direction,
    ReadOnlyCollection<DirectedPipeStage> Written,
    ReadOnlyCollection<int> FlowIndices, ReadOnlyCollection<DirectedPipeEdge> DataEdges,
    string Tenant);

internal static class DirectedPipePlanner
{
    private static bool Id(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 256 ||
            !(text[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
            return false;
        foreach (char c in text)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                or >= '0' and <= '9' or '_' or '-')) return false;
        return true;
    }
    private static bool Type(string text)
    {
        if (text.Length is 0 or > 256 ||
            !(text[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
            return false;
        foreach (char c in text)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                or >= '0' and <= '9' or '_' or '.' or '<' or '>')) return false;
        return true;
    }
    internal static DirectedPipePlan Plan(
        IReadOnlyList<DirectedPipeStage> written, DirectedPipeDirection direction)
    {
        ArgumentNullException.ThrowIfNull(written);
        if (written.Count is < 2 or > 16)
            throw new ArgumentException("E_PIPE_STAGE_LIMIT");
        var copy = new List<DirectedPipeStage>(written.Count);
        var tenant = written[0].Tenant;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < written.Count; i++)
        {
            var s = written[i] ?? throw new ArgumentException("E_PIPE_STAGE");
            if (!Id(s.Id) || !Id(s.Tenant)) throw new ArgumentException("E_PIPE_ID");
            if (tenant != s.Tenant) throw new ArgumentException("E_PIPE_TENANT_BOUNDARY");
            if (!seen.Add(s.Id)) throw new ArgumentException("E_PIPE_DUPLICATE_STAGE");
            if (s.Receives != null && !Type(s.Receives) ||
                s.Emits != null && !Type(s.Emits))
                throw new ArgumentException("E_PIPE_TYPE_LABEL");
            if (s.Endpoint is DirectedPipeEndpoint.Actor)
            {
                if (s.Receives == null && s.Emits == null)
                    throw new ArgumentException("E_PIPE_PORTS");
            }
            else if (s.Endpoint is DirectedPipeEndpoint.Channel
                or DirectedPipeEndpoint.StreamDuplex)
            {
                if (s.Receives == null || s.Receives != s.Emits)
                    throw new ArgumentException("E_PIPE_CHANNEL_PORTS");
            }
            else if (s.Endpoint is DirectedPipeEndpoint.StreamReadable)
            {
                if (s.Receives != null || s.Emits == null)
                    throw new ArgumentException("E_PIPE_READONLY_PORT");
            }
            else if (s.Endpoint is DirectedPipeEndpoint.StreamSink)
            {
                if (s.Receives == null || s.Emits != null)
                    throw new ArgumentException("E_PIPE_WRITEONLY_PORT");
            }
            copy.Add(s);
        }
        var flow = new List<int>(copy.Count);
        for (int i = 0; i < copy.Count; i++)
            flow.Add(direction == DirectedPipeDirection.Pipe ? i : copy.Count - i - 1);
        var edges = new List<DirectedPipeEdge>(copy.Count - 1);
        for (int i = 0; i + 1 < flow.Count; i++)
        {
            var src = copy[flow[i]];
            var dst = copy[flow[i + 1]];
            if (src.Emits == null) throw new ArgumentException("E_PIPE_MISSING_EMITS");
            if (dst.Receives == null) throw new ArgumentException("E_PIPE_MISSING_RECEIVES");
            if (src.Emits != dst.Receives) throw new ArgumentException("E_PIPE_TYPE_MISMATCH");
            edges.Add(new DirectedPipeEdge(flow[i], flow[i + 1], src.Id, dst.Id, src.Emits));
        }
        return new DirectedPipePlan(direction, copy.AsReadOnly(), flow.AsReadOnly(),
            edges.AsReadOnly(), tenant);
    }

    // Reject double registration; actual runtime must independently enforce
    // the same condition atomically against concurrent graph activation.
    internal static void CheckUnicast(IReadOnlyList<DirectedPipePlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Count > 64) throw new ArgumentException("E_PIPE_PLAN_LIMIT");
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in plans)
        {
            ArgumentNullException.ThrowIfNull(p);
            foreach (var edge in p.DataEdges)
            {
                var source = p.Tenant + "\0" + edge.Source;
                var pair = source + "\0" + edge.Sink;
                if (!pairs.Add(pair)) throw new ArgumentException("E_PIPE_DUPLICATE_EDGE");
                if (!occupied.Add(source)) throw new ArgumentException("E_PIPE_OUTPUT_OCCUPIED");
            }
        }
    }
}
