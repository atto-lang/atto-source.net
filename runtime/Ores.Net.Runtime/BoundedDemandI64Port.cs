namespace Ores.Net.Runtime;

/// <summary>
/// Native-AOT-compatible bounded demand adapter over the REAL selectable i64
/// channel. A runtime-only scalar substrate, not compiled AttoLang Flow&lt;T&gt;.
/// No executor, callbacks, or dynamic waiter queue are created here.
/// </summary>
public sealed class BoundedDemandI64Port
{
    public enum OfferStatus { Accepted, NoSubscriber, NoDemand, Full, ByteBudget, Closed }
    public enum PollStatus { Item, NotReady, Complete, Cancelled }
    public readonly record struct Poll(PollStatus Status, long Value = 0, ulong Ticket = 0);

    private readonly object _gate = new();
    private readonly SelectableI64Channel _channel;
    private readonly ulong[] _tickets;
    private readonly long _maxBytes;
    private readonly long _maxDemand;
    private long _retainedBytes;
    private long _credits;
    private ulong _nextTicket = 1;
    private bool _subscribed;
    private bool _completed;
    private bool _cancelled;

    public BoundedDemandI64Port(int capacity, int maxInFlight, long maxBytes, long maxDemand)
    {
        if (capacity is < 1 or > 65536 || maxInFlight is < 1 or > 4096 ||
            maxBytes < sizeof(long) || maxDemand < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Demand budgets exceed fixed bounds");
        _channel = new SelectableI64Channel(capacity);
        _tickets = new ulong[maxInFlight];
        _maxBytes = maxBytes;
        _maxDemand = maxDemand;
    }

    public bool Subscribe()
    {
        lock (_gate)
        {
            if (_subscribed || _completed || _cancelled) return false;
            _subscribed = true;
            return true;
        }
    }

    public void Request(long count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate)
        {
            if (!_subscribed || _completed || _cancelled)
                throw new InvalidOperationException("No active demand subscriber");
            if (count > _maxDemand - _credits)
                throw new ArgumentOutOfRangeException(nameof(count), "Outstanding demand budget exceeded");
            _credits += count;
        }
    }

    public OfferStatus TryOffer(long value)
    {
        lock (_gate)
        {
            if (_completed || _cancelled) return OfferStatus.Closed;
            if (!_subscribed) return OfferStatus.NoSubscriber;
            if (_credits == 0) return OfferStatus.NoDemand;
            if (_retainedBytes > _maxBytes - sizeof(long)) return OfferStatus.ByteBudget;
            if (!_channel.TryWrite(value)) return OfferStatus.Full;
            _credits--;
            _retainedBytes += sizeof(long);
            return OfferStatus.Accepted;
        }
    }

    public Poll TryNext()
    {
        lock (_gate)
        {
            if (_cancelled) return new(PollStatus.Cancelled);
            if (!_subscribed) return new(PollStatus.NotReady);
            var free = Array.IndexOf(_tickets, 0UL);
            if (free < 0) return new(PollStatus.NotReady);
            if (_nextTicket == ulong.MaxValue)
                throw new OverflowException("Demand delivery ticket exhausted");
            var read = _channel.TryReadResult();
            if (read.Status == SelectableI64Channel.ReadStatus.Eof)
                return new(PollStatus.Complete);
            if (read.Status != SelectableI64Channel.ReadStatus.Value)
                return new(PollStatus.NotReady);
            var ticket = _nextTicket++;
            _tickets[free] = ticket;
            return new(PollStatus.Item, read.Value, ticket);
        }
    }

    public bool Ack(ulong ticket)
    {
        lock (_gate)
        {
            if (ticket == 0) return false;
            for (int i = 0; i < _tickets.Length; i++)
            {
                if (_tickets[i] != ticket) continue;
                _tickets[i] = 0;
                _retainedBytes -= sizeof(long);
                return true;
            }
            return false;
        }
    }

    public bool Complete()
    {
        lock (_gate)
        {
            if (_completed || _cancelled) return false;
            _completed = true;
            _credits = 0;
            _channel.Close();
            return true;
        }
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_completed || _cancelled) return false;
            _completed = true;
            _cancelled = true;
            _credits = 0;
            _channel.Close();
            while (_channel.TryRead(out _)) _retainedBytes -= sizeof(long);
            return true;
        }
    }

    public int Queued { get { lock (_gate) return _channel.Count; } }
    public long RetainedBytes { get { lock (_gate) return _retainedBytes; } }
    public long OutstandingDemand { get { lock (_gate) return _credits; } }
}
