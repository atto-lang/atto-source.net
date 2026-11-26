namespace Ores.Net.Runtime;

/// <summary>
/// Actor-generation fence for queued select/await continuations.
/// Completion producers enqueue tickets; the actor lane consumes a ticket
/// immediately before executing the continuation. Generation advance
/// invalidates all outstanding tickets. Close permanently rejects new tickets.
/// TryClaim retains physical execution capacity until the lease is disposed.
/// Advance rejects while any lease lives. TryConsume is legacy logical-only;
/// the scheduler must migrate to TryClaim before claiming physical fencing.
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
    private readonly int[] _free;
    private int _freeCount;
    private ulong _generation;
    private ulong _nextId = 1;
    private bool _closed;
    private int _executing;

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
        _free = new int[maxTickets];
        _freeCount = maxTickets;
        for (var i = 0; i < maxTickets; i++)
        {
            _slots[i] = new Slot();
            _free[i] = maxTickets - 1 - i;
        }
    }

    public ulong Generation
    {
        get { lock (_gate) return _generation; }
    }

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
            ClearPendingLocked();
            return true;
        }
    }

    public int Executing { get { lock (_gate) return _executing; } }

    public bool RetirementReady
    {
        get { lock (_gate) return _closed && _executing == 0 && _freeCount == _slots.Length; }
    }

    public int PendingTickets
    {
        get
        {
            lock (_gate)
            {
                return _slots.Length - _freeCount;
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

            if (_slots.Length - _freeCount + _executing >= _slots.Length)
                throw new InvalidOperationException("Continuation physical capacity exhausted");
            if (_freeCount == 0)
                throw new InvalidOperationException("Continuation ticket capacity exhausted");
            var index = _free[_freeCount - 1];
            if (_nextId == ulong.MaxValue)
                throw new OverflowException(
                    "Continuation ticket identity exhausted");

            var id = _nextId;
            // Construct the ticket before changing quota or slot state.
            var ticket = new Ticket(this, index, id, _generation);
            _nextId++;
            _freeCount--;
            var slot = _slots[index];
            slot.Id = id;
            slot.Generation = _generation;
            slot.Active = true;
            return ticket;
        }
    }

    public bool TryConsume(Ticket? ticket)
    {
        // Missing completion handles are rejected without changing live slots.
        if (ticket is null) return false;
        lock (_gate)
        {
            if (!MatchesLocked(ticket, requireCurrent: true))
                return false;
            ClearLocked(ticket.Index);
            return true;
        }
    }

    /// <summary>Claim on the actor lane and dispose after callback return.</summary>
    public Execution? TryClaim(Ticket? ticket)
    {
        if (ticket is null) return null;
        lock (_gate)
        {
            if (_closed || !MatchesLocked(ticket, requireCurrent: true)) return null;
            // Allocate the lease before consuming the pending slot.
            var lease = new Execution(this, _generation);
            ClearLocked(ticket.Index);
            _executing++;
            return lease;
        }
    }

    public sealed class Execution : IDisposable
    {
        private readonly SelectContinuationFence _owner;
        private bool _active = true;
        internal Execution(SelectContinuationFence owner, ulong generation)
        {
            _owner = owner;
            Generation = generation;
        }
        public ulong Generation { get; }
        public bool Active { get { lock (_owner._gate) return _active; } }
        public bool Close()
        {
            lock (_owner._gate)
            {
                if (!_active) return false;
                _active = false;
                _owner._executing--;
                return true;
            }
        }
        public void Dispose() => Close();
    }

    public bool Advance(ulong expectedGeneration)
    {
        lock (_gate)
        {
            if (_closed || expectedGeneration != _generation || _executing != 0) return false;
            if (_generation == ulong.MaxValue)
                throw new OverflowException("Actor generation exhausted");

            _generation++;
            ClearPendingLocked();
            return true;
        }
    }

    private bool Close(Ticket ticket)
    {
        lock (_gate)
        {
            if (!MatchesLocked(ticket, requireCurrent: false))
                return false;
            ClearLocked(ticket.Index);
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

    private void ClearPendingLocked()
    {
        for (var index = 0; index < _slots.Length; index++)
            if (_slots[index].Active) ClearLocked(index);
    }

    private void ClearLocked(int index)
    {
        var slot = _slots[index];
        slot.Active = false;
        slot.Generation = 0;
        _free[_freeCount++] = index;
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
