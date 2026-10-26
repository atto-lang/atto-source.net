namespace Ores.Net.Runtime;

/// <summary>
/// Runtime-only one-shot signed-i64 Future readiness source for static select.
/// Observation is non-consuming; readiness subscriptions remain independently
/// revocable and identity checked.
/// </summary>
public sealed class SelectableFutureI64
{
    private const int MaxWaiterLimit = 4096;

    public enum OutcomeKind
    {
        Value,
        Cancelled
    }

    public readonly struct Outcome
    {
        internal Outcome(OutcomeKind kind, long value = default)
        {
            Kind = kind;
            Value = value;
        }

        public OutcomeKind Kind { get; }
        public long Value { get; }
    }

    private sealed class Waiter
    {
        public ulong Id;
        public bool Active;
        public Action<Subscription>? Notify;
    }

    private readonly object _gate = new();
    private readonly Waiter[] _waiters;
    private ulong _nextWaiterId = 1;
    private bool _settled;
    private Outcome _outcome = new(OutcomeKind.Cancelled);
    private Exception? _notificationFailure;

    public SelectableFutureI64(int maxWaiters = 64)
    {
        if (maxWaiters <= 0 || maxWaiters > MaxWaiterLimit)
            throw new ArgumentOutOfRangeException(nameof(maxWaiters),
                $"Future maxWaiters must be in 1..{MaxWaiterLimit}");

        _waiters = new Waiter[maxWaiters];
        for (var i = 0; i < _waiters.Length; i++)
            _waiters[i] = new Waiter();
    }

    public bool Settled
    {
        get { lock (_gate) return _settled; }
    }

    public int PendingWaiters
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var waiter in _waiters)
                    if (waiter.Active) count++;
                return count;
            }
        }
    }

    public Exception? NotificationFailure
    {
        get { lock (_gate) return _notificationFailure; }
    }

    public Subscription SubscribeReady(Action<Subscription> notify)
    {
        ArgumentNullException.ThrowIfNull(notify);

        Subscription subscription;
        Action<Subscription>? immediate = null;
        lock (_gate)
        {
            var index = -1;
            for (var i = 0; i < _waiters.Length; i++)
            {
                if (!_waiters[i].Active)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                throw new InvalidOperationException(
                    "Future select waiter capacity exhausted");
            if (_nextWaiterId == ulong.MaxValue)
                throw new OverflowException(
                    "Future select waiter identity exhausted");

            var id = _nextWaiterId++;
            subscription = new Subscription(this, index, id);
            var waiter = _waiters[index];
            waiter.Id = id;
            waiter.Active = true;
            waiter.Notify = notify;
            if (_settled) immediate = notify;
        }

        if (immediate is not null)
        {
            try
            {
                immediate(subscription);
            }
            catch
            {
                _ = subscription.Close();
                throw;
            }
        }
        return subscription;
    }

    public bool TryCommit(Subscription subscription, out Outcome outcome)
    {
        lock (_gate)
        {
            if (!Matches(subscription) || !_settled)
            {
                outcome = default;
                return false;
            }

            outcome = _outcome;
            CloseLocked(_waiters[subscription.Index]);
            return true;
        }
    }

    public bool Resolve(long value) =>
        Settle(new Outcome(OutcomeKind.Value, value));

    public bool Cancel() =>
        Settle(new Outcome(OutcomeKind.Cancelled));

    private bool Settle(Outcome outcome)
    {
        // Allocate capture arrays before the terminal transition.
        var callbacks = new Action<Subscription>?[_waiters.Length];
        var subscriptions = new Subscription?[_waiters.Length];
        var count = 0;

        lock (_gate)
        {
            if (_settled) return false;

            // Construct every notification token before publishing settlement.
            // If allocation fails, the Future remains pending and retryable.
            for (var i = 0; i < _waiters.Length; i++)
            {
                var waiter = _waiters[i];
                if (!waiter.Active || waiter.Notify is null) continue;
                callbacks[count] = waiter.Notify;
                subscriptions[count] =
                    new Subscription(this, i, waiter.Id);
                count++;
            }

            _settled = true;
            _outcome = outcome;
        }

        Exception? first = null;
        for (var i = 0; i < count; i++)
        {
            try
            {
                callbacks[i]!(subscriptions[i]!);
            }
            catch (Exception error)
            {
                first ??= error;
            }
        }

        if (first is not null)
        {
            lock (_gate)
                _notificationFailure ??= first;
        }
        return true;
    }

    private bool Matches(Subscription subscription)
    {
        if (!ReferenceEquals(subscription.Owner, this)) return false;
        if ((uint)subscription.Index >= (uint)_waiters.Length) return false;
        var waiter = _waiters[subscription.Index];
        return waiter.Active && waiter.Id == subscription.Id;
    }

    private bool Close(Subscription subscription)
    {
        lock (_gate)
        {
            if (!Matches(subscription)) return false;
            CloseLocked(_waiters[subscription.Index]);
            return true;
        }
    }

    private static void CloseLocked(Waiter waiter)
    {
        waiter.Active = false;
        waiter.Notify = null;
    }

    public sealed class Subscription
    {
        private readonly SelectableFutureI64 _owner;
        private readonly int _index;
        private readonly ulong _id;

        internal Subscription(
            SelectableFutureI64 owner,
            int index,
            ulong id)
        {
            _owner = owner;
            _index = index;
            _id = id;
        }

        internal SelectableFutureI64 Owner => _owner;
        internal int Index => _index;
        internal ulong Id => _id;

        public bool Active
        {
            get
            {
                lock (_owner._gate)
                    return _owner.Matches(this);
            }
        }

        public bool Close() => _owner.Close(this);
    }
}
