namespace Ores.Net.Runtime;

// Trusted NativeAOT host-only FIFO queue substrate; NOT .ores guest ABI,
// durable storage, OS sandbox, actor scheduler or authenticated network token.
public sealed class DurableLockQueues
{
    public readonly record struct ActorKey(ulong Id, ulong Generation);
    public sealed class Ticket
    {
        internal Ticket(ulong issuer, ulong lockId, ulong sequence, ulong operationId, ActorKey actor)
        {
            Issuer = issuer; LockId = lockId; Sequence = sequence;
            OperationId = operationId; Actor = actor;
        }
        internal ulong Issuer { get; }
        internal ulong LockId { get; }
        internal ulong OperationId { get; }
        internal ActorKey Actor { get; }
        public ulong Sequence { get; }
    }
    public readonly record struct Admission(Ticket Ticket, bool Head);
    public readonly record struct Snapshot(int Locks, int Entries, long Bytes, int RunningActors, bool Accepting);
    public sealed record Limits(int MaxLocks = 1024, int MaxEntries = 8192,
        int MaxPerLock = 128, long MaxBytes = 16 * 1024 * 1024);

    private sealed class Entry(Ticket ticket, long bytes)
    {
        internal Ticket Ticket = ticket;
        internal long Bytes = bytes;
        internal bool Claimed;
    }

    private readonly object _gate = new();
    private readonly ulong _generation;
    private readonly Limits _limits;
    private readonly Dictionary<ulong, LinkedList<Entry>> _lanes = new();
    // Ticket is a sealed reference-identity object, not deserialized from user data.
    private readonly Dictionary<Ticket, Entry> _issued = new();
    private readonly HashSet<ulong> _operations = new();
    private readonly HashSet<ActorKey> _running = new();
    private ulong _sequence = 1;
    private int _entries;
    private long _bytes;
    private bool _accepting = true;

    public DurableLockQueues(ulong generation, Limits? limits = null)
    {
        _limits = limits ?? new Limits();
        if (generation == 0 || _limits.MaxLocks <= 0 || _limits.MaxEntries <= 0 ||
            _limits.MaxPerLock <= 0 || _limits.MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "invalid durable queue limits");
        _generation = generation;
    }

    public Admission Enqueue(ActorKey actor, ulong lockId, ulong operationId, long bytes)
    {
        if (actor.Id == 0 || actor.Generation == 0 || lockId == 0 || operationId == 0 || bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes), "invalid durable queue input");
        lock (_gate)
        {
            if (!_accepting) throw new InvalidOperationException("tenant is quiescing");
            if (_operations.Contains(operationId)) throw new InvalidOperationException("duplicate operation");
            _lanes.TryGetValue(lockId, out var lane);
            if (_entries >= _limits.MaxEntries || bytes > _limits.MaxBytes - _bytes ||
                (lane == null && _lanes.Count >= _limits.MaxLocks) ||
                (lane != null && lane.Count >= _limits.MaxPerLock))
                throw new InvalidOperationException("durable queue budget exhausted");
            if (_sequence == ulong.MaxValue) throw new InvalidOperationException("ticket sequence exhausted");
            var ticket = new Ticket(_generation, lockId, _sequence, operationId, actor);
            var entry = new Entry(ticket, bytes);
            bool head = lane is null || lane.Count == 0;
            lane ??= new LinkedList<Entry>();
            lane.AddLast(entry);
            _lanes[lockId] = lane;
            _issued.Add(ticket, entry);
            _operations.Add(operationId);
            _sequence++;
            _entries++;
            _bytes += bytes;
            return new Admission(ticket, head);
        }
    }

    private Entry CheckHead(Ticket ticket)
    {
        if (ticket is null || ticket.Issuer != _generation ||
            !_issued.TryGetValue(ticket, out var entry))
            throw new InvalidOperationException("stale or foreign queue ticket");
        if (!_lanes.TryGetValue(ticket.LockId, out var lane) ||
            !ReferenceEquals(lane.First!.Value, entry))
            throw new InvalidOperationException("ticket is not FIFO head");
        return entry;
    }

    public bool Claim(Ticket ticket)
    {
        lock (_gate)
        {
            if (!_accepting) throw new InvalidOperationException("quiescing: claims closed");
            var entry = CheckHead(ticket);
            if (entry.Claimed) throw new InvalidOperationException("ticket already claimed");
            if (!_running.Add(ticket.Actor)) return false;
            entry.Claimed = true;
            return true;
        }
    }
    private Ticket? RemoveHead(Entry entry)
    {
        var ticket = entry.Ticket;
        var lane = _lanes[ticket.LockId];
        lane.RemoveFirst();
        if (lane.Count == 0) _lanes.Remove(ticket.LockId);
        _issued.Remove(ticket);
        _operations.Remove(ticket.OperationId);
        _entries--;
        _bytes -= entry.Bytes;
        return lane.First?.Value.Ticket;
    }
    public Ticket? Finish(Ticket ticket)
    {
        lock (_gate)
        {
            var entry = CheckHead(ticket);
            if (!entry.Claimed || !_running.Remove(ticket.Actor))
                throw new InvalidOperationException("only a claimed head can finish");
            return RemoveHead(entry);
        }
    }
    public Ticket? CancelHead(Ticket ticket)
    {
        lock (_gate)
        {
            var entry = CheckHead(ticket);
            if (entry.Claimed) throw new InvalidOperationException("cannot cancel a claimed head");
            return RemoveHead(entry);
        }
    }
    public void CancelWaiter(Ticket ticket)
    {
        lock (_gate)
        {
            if (ticket is null || ticket.Issuer != _generation ||
                !_issued.TryGetValue(ticket, out var entry))
                throw new InvalidOperationException("stale or foreign waiter");
            var lane = _lanes[ticket.LockId];
            if (ReferenceEquals(lane.First!.Value, entry) || !lane.Remove(entry))
                throw new InvalidOperationException("ticket is not a waiter");
            _issued.Remove(ticket); _operations.Remove(ticket.OperationId);
            _entries--; _bytes -= entry.Bytes;
        }
    }
    public bool HasActor(ActorKey actor)
    {
        lock (_gate)
        {
            foreach (var lane in _lanes.Values)
                foreach (var entry in lane)
                    if (entry.Ticket.Actor == actor) return true;
            return false;
        }
    }
    public void Quiesce() { lock (_gate) _accepting = false; }
    public bool Drained { get { lock (_gate) return !_accepting && _entries == 0 && _running.Count == 0; } }
    public Snapshot GetSnapshot()
    {
        lock (_gate) return new Snapshot(_lanes.Count, _entries, _bytes, _running.Count, _accepting);
    }
}
