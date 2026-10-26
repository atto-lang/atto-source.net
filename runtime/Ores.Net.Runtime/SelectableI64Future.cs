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
