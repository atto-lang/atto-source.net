namespace Ores.Net.Runtime;

/// <summary>
/// Actor-generation fence for queued select/await continuations.
/// Completion producers enqueue tickets; the actor lane consumes a ticket
/// immediately before executing the continuation. Generation advance
/// invalidates all outstanding tickets. Close permanently rejects new tickets.
/// The scheduler must serialize consume-and-invoke with lifecycle changes on
/// the actor lane; this fence does not join already-running native workers.
/// </summary>
public sealed class SelectContinuationFence
{
    private const int MaxTicketLimit = 4096;

    private sealed class Slot
    {
        public ulong Id;
        public ulong Generation;
        public bool Active;
    }

    private readonly object _gate = new();
    private readonly Slot[] _slots;
    private ulong _generation;
    private ulong _nextId = 1;
    private bool _closed;

    public bool Closed
    {
        get { lock (_gate) return _closed; }
    }

    // Terminal shutdown cancels pending delivery, but does not join workers.
    public bool Close()
    {
        lock (_gate)
        {
            if (_closed) return false;
            _closed = true;
            foreach (var slot in _slots) ClearLocked(slot);
            return true;
        }
    }

    public SelectContinuationFence(
        ulong generation = 1,
        int maxTickets = 64)
    {
        if (generation == 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        if (maxTickets <= 0 || maxTickets > MaxTicketLimit)
            throw new ArgumentOutOfRangeException(nameof(maxTickets));

        _generation = generation;
        _slots = new Slot[maxTickets];
        for (var i = 0; i < maxTickets; i++)
            _slots[i] = new Slot();
    }

    public ulong Generation
    {
        get { lock (_gate) return _generation; }
    }

    public int PendingTickets
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var slot in _slots)
                    if (slot.Active) count++;
                return count;
            }
        }
    }

    public Ticket Register(ulong expectedGeneration)
    {
        lock (_gate)
        {
            if (_closed)
                throw new InvalidOperationException("Continuation fence is closed");
            if (expectedGeneration != _generation)
                throw new InvalidOperationException("stale actor generation");

            var index = -1;
            for (var i = 0; i < _slots.Length; i++)
            {
                if (!_slots[i].Active)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                throw new InvalidOperationException(
                    "Continuation ticket capacity exhausted");
            if (_nextId == ulong.MaxValue)
                throw new OverflowException(
                    "Continuation ticket identity exhausted");

            var id = _nextId++;
            var slot = _slots[index];
            slot.Id = id;
            slot.Generation = _generation;
            slot.Active = true;
            return new Ticket(this, index, id, _generation);
        }
    }

    public bool TryConsume(Ticket ticket)
    {
        // Missing completion handles are rejected without changing live slots.
        if (ticket is null) return false;
        lock (_gate)
        {
            if (!MatchesLocked(ticket, requireCurrent: true))
                return false;
            ClearLocked(_slots[ticket.Index]);
            return true;
        }
    }

    public bool Advance(ulong expectedGeneration)
    {
        lock (_gate)
        {
            if (_closed || expectedGeneration != _generation) return false;
            if (_generation == ulong.MaxValue)
                throw new OverflowException("Actor generation exhausted");

            _generation++;
            foreach (var slot in _slots)
                ClearLocked(slot);
            return true;
        }
    }

    private bool Close(Ticket ticket)
    {
        lock (_gate)
        {
            if (!MatchesLocked(ticket, requireCurrent: false))
                return false;
            ClearLocked(_slots[ticket.Index]);
            return true;
        }
    }

    private bool MatchesLocked(Ticket ticket, bool requireCurrent)
    {
        if (!ReferenceEquals(ticket.Owner, this) ||
            (uint)ticket.Index >= (uint)_slots.Length)
            return false;

        var slot = _slots[ticket.Index];
        if (!slot.Active ||
            slot.Id != ticket.Id ||
            slot.Generation != ticket.Generation)
            return false;
        return !requireCurrent || ticket.Generation == _generation;
    }

    private static void ClearLocked(Slot slot)
    {
        slot.Active = false;
        slot.Generation = 0;
    }

    public sealed class Ticket
    {
        private readonly SelectContinuationFence _owner;
        private readonly int _index;
        private readonly ulong _id;
        private readonly ulong _generation;

        internal Ticket(
            SelectContinuationFence owner,
            int index,
            ulong id,
            ulong generation)
        {
            _owner = owner;
            _index = index;
            _id = id;
            _generation = generation;
        }

        internal SelectContinuationFence Owner => _owner;
        internal int Index => _index;
        internal ulong Id => _id;
        public ulong Generation => _generation;

        public bool Active
        {
            get
            {
                lock (_owner._gate)
                    return _owner.MatchesLocked(this, requireCurrent: false);
            }
        }

        public bool Close() => _owner.Close(this);
    }
}













    public bool TryConsume(Ticket? ticket)











