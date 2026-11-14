namespace Ores.Net.Runtime;

/// <summary>
/// Runtime-only, single-assignment Future&lt;i64&gt; readiness for static select.
/// A callback signals readiness but cannot win on its own; use TryCommitReady
/// inside SelectArbiter.Registration.TryWin. No guest Future&lt;T&gt; lowering yet.
/// </summary>
public sealed class SelectableI64Future
{
    public enum FutureStatus { Pending, Resolved, Rejected, Cancelled }

    public readonly record struct Receipt(
        FutureStatus Status, long Value, Exception? Error);

    private sealed class Waiter
    {
        public ulong Id;
        public bool Active;
        public Action<Subscription>? Notify;
    }

    private readonly object _gate = new();
    private readonly Waiter[] _waiters;
    private ulong _nextId = 1;
    private FutureStatus _status;
    private long _value;
    private Exception? _error;
    private Exception? _notificationFailure;

    public SelectableI64Future(int maxWaiters = 64)
    {
        if (maxWaiters <= 0 || maxWaiters > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxWaiters));
        _waiters = new Waiter[maxWaiters];
        for (var i = 0; i < _waiters.Length; i++)
            _waiters[i] = new Waiter();
    }

    public FutureStatus Status
    {
        get { lock (_gate) return _status; }
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
        Subscription token;
        var immediate = false;
        lock (_gate)
        {
            var slot = -1;
            for (var i = 0; i < _waiters.Length; i++)
                if (!_waiters[i].Active) { slot = i; break; }
            if (slot < 0)
                throw new InvalidOperationException("Future waiter capacity exhausted");
            if (_nextId == ulong.MaxValue)
                throw new OverflowException("Future waiter identity exhausted");
            token = new Subscription(this, slot, _nextId++);
            var waiter = _waiters[slot];
            waiter.Id = token.Id;
            waiter.Notify = notify;
            waiter.Active = true;
            immediate = _status != FutureStatus.Pending;
        }
        if (immediate)
        {
            try { notify(token); }
            catch
            {
                _ = token.Close();
                throw;
            }
        }
        return token;
    }

    /// <summary>
    /// Commit the terminal future outcome and revoke the selected
    /// subscription. Must run inside arbiter TryWin conditional callback.
    /// </summary>
    public bool TryCommitReady(Subscription token, out Receipt receipt)
    {
        lock (_gate)
        {
            receipt = default;
            if (!Matches(token) || _status == FutureStatus.Pending)
                return false;
            receipt = new Receipt(_status, _value, _error);
            var waiter = _waiters[token.Index];
            waiter.Active = false;
            waiter.Notify = null;
            return true;
        }
    }

    public bool Resolve(long value) =>
        Settle(FutureStatus.Resolved, value, null);

    public bool Reject(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Settle(FutureStatus.Rejected, 0, error);
    }

    public bool Cancel() =>
        Settle(FutureStatus.Cancelled, 0, null);

    private bool Settle(FutureStatus target, long value, Exception? error)
    {
        // Allocate before publishing a terminal result.
        var callbacks = new Action<Subscription>?[_waiters.Length];
        var subscriptions = new Subscription?[_waiters.Length];
        var count = 0;
        lock (_gate)
        {
            if (_status != FutureStatus.Pending) return false;
            for (var i = 0; i < _waiters.Length; i++)
            {
                var waiter = _waiters[i];
                if (!waiter.Active || waiter.Notify is null) continue;
                callbacks[count] = waiter.Notify;
                subscriptions[count] = new Subscription(this, i, waiter.Id);
                count++;
            }
            _status = target;
            _value = value;
            _error = error;
        }
        Exception? failure = null;
        for (var i = 0; i < count; i++)
        {
            try { callbacks[i]?.Invoke(subscriptions[i]!); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is not null)
        {
            lock (_gate) _notificationFailure ??= failure;
        }
        return true;
    }

    private bool Matches(Subscription? token)
    {
        if (token is null || !ReferenceEquals(token.Owner, this) ||
            (uint)token.Index >= (uint)_waiters.Length)
            return false;
        var waiter = _waiters[token.Index];
        return waiter.Active && waiter.Id == token.Id;
    }

    private bool Close(Subscription token)
    {
        lock (_gate)
        {
            if (!Matches(token)) return false;
            var waiter = _waiters[token.Index];
            waiter.Active = false;
            waiter.Notify = null;
            return true;
        }
    }

    public sealed class Subscription
    {
        private readonly SelectableI64Future _owner;
        internal Subscription(SelectableI64Future owner, int index, ulong id)
        {
            _owner = owner;
            Index = index;
            Id = id;
        }
        internal SelectableI64Future Owner => _owner;
        internal int Index { get; }
        internal ulong Id { get; }
        public bool Active
        {
            get { lock (_owner._gate) return _owner.Matches(this); }
        }
        public bool Close() => _owner.Close(this);
    }
}

/// <summary>
/// Generation-fenced actor continuation admission. Retire makes prior
/// tokens stale immediately. Physical retirement is deferred until all
/// already-running synchronous continuation leases have returned.
/// </summary>
public sealed class ActorGenerationFence
{
    private readonly object _gate = new();
    private ulong _generation = 1;
    private bool _live = true;
    private int _executing;

    public bool PhysicallyRetired
    {
        get { lock (_gate) return !_live && _executing == 0; }
    }

    public Token Capture()
    {
        lock (_gate) return new Token(this, _generation);
    }

    public bool Retire()
    {
        lock (_gate)
        {
            if (!_live) return false;
            _live = false;
            _generation++;
            return true;
        }
    }

    private bool IsCurrent(ulong generation)
    {
        lock (_gate) return _live && _generation == generation;
    }

    private bool RunIfCurrent(ulong generation, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (!_live || _generation != generation) return false;
            checked { _executing++; }
        }
        try { callback(); }
        finally
        {
            lock (_gate) _executing--;
        }
        return true;
    }

    public sealed class Token
    {
        private readonly ActorGenerationFence _owner;
        private readonly ulong _generation;
        internal Token(ActorGenerationFence owner, ulong generation)
        {
            _owner = owner;
            _generation = generation;
        }
        public bool Current => _owner.IsCurrent(_generation);
        public bool RunIfCurrent(Action callback) =>
            _owner.RunIfCurrent(_generation, callback);
    }
}
namespace Ores.Net.Runtime;

/// <summary>
/// Runtime-only selectable signed-i64 Future. Completion is immutable and
/// non-consuming: independent selects may observe the same terminal result.
/// Readiness callbacks are advisory and must revalidate their subscription
/// inside SelectArbiter.Registration.TryWin.
/// </summary>
public sealed class SelectableI64Future
{
    private const int MaxWaiterLimit = 4096;

    public enum FutureStatus
    {
        Pending,
        Value,
        Failed,
        Cancelled
    }

    public readonly struct Result
    {
        internal Result(FutureStatus status, long value, Exception? error)
        {
            Status = status;
            Value = value;
            Error = error;
        }

        public FutureStatus Status { get; }
        public long Value { get; }
        public Exception? Error { get; }
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
    private FutureStatus _status;
    private long _value;
    private Exception? _error;
    private Exception? _notificationFailure;

    public SelectableI64Future(int maxWaiters = 64)
    {
        if (maxWaiters <= 0 || maxWaiters > MaxWaiterLimit)
            throw new ArgumentOutOfRangeException(nameof(maxWaiters),
                $"Future maxWaiters must be in 1..{MaxWaiterLimit}");

        _waiters = new Waiter[maxWaiters];
        for (var i = 0; i < maxWaiters; i++)
            _waiters[i] = new Waiter();
    }

    public FutureStatus Status
    {
        get { lock (_gate) return _status; }
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
        var immediate = false;
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
            immediate = _status != FutureStatus.Pending;
        }

        if (immediate)
        {
            try
            {
                notify(subscription);
            }
            catch
            {
                _ = subscription.Close();
                throw;
            }
        }

        return subscription;
    }

    public bool TryCommit(Subscription subscription, out Result result)
    {
        lock (_gate)
        {
            if (!MatchesLocked(subscription) ||
                _status == FutureStatus.Pending)
            {
                result = new Result(FutureStatus.Pending, default, null);
                return false;
            }

            CloseLocked(_waiters[subscription.Index]);
            result = new Result(_status, _value, _error);
            return true;
        }
    }

    public bool Resolve(long value) =>
        Complete(FutureStatus.Value, value, null);

    public bool Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Complete(FutureStatus.Failed, default, error);
    }

    public bool Cancel() =>
        Complete(FutureStatus.Cancelled, default, null);

    private bool Complete(
        FutureStatus status,
        long value,
        Exception? error)
    {
        var callbacks = new Action<Subscription>?[_waiters.Length];
        var subscriptions = new Subscription?[_waiters.Length];
        var count = 0;

        lock (_gate)
        {
            if (_status != FutureStatus.Pending) return false;

            for (var i = 0; i < _waiters.Length; i++)
            {
                var waiter = _waiters[i];
                if (!waiter.Active || waiter.Notify is null) continue;
                callbacks[count] = waiter.Notify;
                subscriptions[count] = new Subscription(this, i, waiter.Id);
                count++;
            }

            _status = status;
            _value = value;
            _error = error;
        }

        Exception? first = null;
        for (var i = 0; i < count; i++)
        {
            try
            {
                callbacks[i]!(subscriptions[i]!);
            }
            catch (Exception notificationError)
            {
                first ??= notificationError;
            }
        }

        if (first is not null)
        {
            lock (_gate)
                _notificationFailure ??= first;
        }

        return true;
    }

    private bool MatchesLocked(Subscription subscription)
    {
        if (!ReferenceEquals(subscription.Owner, this) ||
            (uint)subscription.Index >= (uint)_waiters.Length)
            return false;
        var waiter = _waiters[subscription.Index];
        return waiter.Active && waiter.Id == subscription.Id;
    }

    private bool Close(Subscription subscription)
    {
        lock (_gate)
        {
            if (!MatchesLocked(subscription)) return false;
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
        private readonly SelectableI64Future _owner;
        private readonly int _index;
        private readonly ulong _id;

        internal Subscription(
            SelectableI64Future owner,
            int index,
            ulong id)
        {
            _owner = owner;
            _index = index;
            _id = id;
        }

        internal SelectableI64Future Owner => _owner;
        internal int Index => _index;
        internal ulong Id => _id;

        public bool Active
        {
            get
            {
                lock (_owner._gate)
                    return _owner.MatchesLocked(this);
            }
        }

        public bool Close() => _owner.Close(this);
    }
}
