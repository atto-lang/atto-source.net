namespace Ores.Net.Runtime;

/// <summary>
/// Scheduler-owned logical critical admission for source critical bodies.
/// This is a runtime ticket substrate only: the source compiler does not yet
/// emit critical {} or critical fnc. A ticket is NOT a physical OS lock.
/// </summary>
/// <remarks>
/// Request() never waits for the active critical body. Queued continuations
/// must be parked by the scheduler. Release() returns the next FIFO ticket,
/// but never calls guest code. Recheck Holds() and actor/tenant/generation
/// authorization before scheduling or executing the resumed body.
/// </remarks>
public sealed class CriticalGate
{
    public const int MaxWaiters = 8192;

    public sealed class Ticket
    {
        internal Ticket(CriticalGate gate, long sequence)
        {
            Gate = gate;
            Sequence = sequence;
        }

        internal CriticalGate Gate { get; }
        public long Sequence { get; }
    }

    public enum AdmissionKind { Acquired, Queued }

    public readonly record struct AdmissionResult(Ticket Ticket, AdmissionKind Kind);

    private readonly object _metadata = new();
    private readonly LinkedList<Ticket> _waiting = new();
    private Ticket? _owner;
    private long _issued;
    private bool _retired;

    /// <summary>
    /// Immediate try-critical acquisition. Never enqueues or waits even on
    /// the brief metadata monitor. No handler or callback runs in this method.
    /// </summary>
    public Ticket? TryEnter()
    {
        if (!Monitor.TryEnter(_metadata)) return null;
        try
        {
            if (_retired || _owner is not null || _waiting.Count != 0) return null;
            _owner = NextTicket();
            return _owner;
        }
        finally { Monitor.Exit(_metadata); }
    }

    /// <summary>
    /// Allocates one logical ticket. Contention returns Queued rather than
    /// parking an OS carrier; the owning scheduler suspends the continuation.
    /// </summary>
    public AdmissionResult Request()
    {
        lock (_metadata)
        {
            if (_retired) throw new InvalidOperationException("critical gate retired");
            bool immediate = _owner is null && _waiting.Count == 0;
            if (!immediate && _waiting.Count >= MaxWaiters)
                throw new InvalidOperationException("critical waiter limit exceeded");
            var ticket = NextTicket();
            if (immediate)
            {
                _owner = ticket;
                return new AdmissionResult(ticket, AdmissionKind.Acquired);
            }
            _waiting.AddLast(ticket);
            return new AdmissionResult(ticket, AdmissionKind.Queued);
        }
    }

    /// <summary>
    /// Owner release produces next FIFO ticket. Release of a queued ticket
    /// retracts it. Duplicate/stale releases cannot revoke a different owner.
    /// Scheduler notifications must be performed outside this method.
    /// </summary>
    public Ticket? Release(Ticket ticket)
    {
        lock (_metadata)
        {
            ArgumentNullException.ThrowIfNull(ticket);
            if (!ReferenceEquals(ticket.Gate, this) ||
                ticket.Sequence <= 0 || ticket.Sequence > _issued)
                throw new ArgumentException("foreign or invalid critical ticket", nameof(ticket));
            if (_retired) return null;
            if (!ReferenceEquals(_owner, ticket))
            {
                _waiting.Remove(ticket);
                return null;
            }
            if (_waiting.First is null)
            {
                _owner = null;
                return null;
            }
            _owner = _waiting.First.Value;
            _waiting.RemoveFirst();
            return _owner;
        }
    }

    public bool Holds(Ticket? ticket)
    {
        lock (_metadata)
            return !_retired && ticket is not null &&
                   ReferenceEquals(ticket.Gate, this) &&
                   ReferenceEquals(_owner, ticket);
    }

    public int Waiting
    {
        get { lock (_metadata) return _waiting.Count; }
    }

    public bool Retired
    {
        get { lock (_metadata) return _retired; }
    }

    /// <summary>
    /// Revoke old-generation queue tickets after the currently executing
    /// critical body is physically quiesced. Does not preempt or roll back
    /// a running guest mutation.
    /// </summary>
    public void Retire()
    {
        lock (_metadata)
        {
            _retired = true;
            _owner = null;
            _waiting.Clear();
        }
    }

    private Ticket NextTicket()
    {
        if (_issued == long.MaxValue)
            throw new InvalidOperationException("critical ticket sequence exhausted");
        return new Ticket(this, ++_issued);
    }
}
