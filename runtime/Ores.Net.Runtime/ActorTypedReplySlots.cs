using System.Collections.Generic;

namespace Ores.Net.Runtime;

/// <summary>
/// HOST-ONLY, Native AOT-safe bounded local actor reply slot ledger.
/// This is neither a source-level AttoLang request/receive lowering nor a
/// guest-owned reply channel. No reflection, dynamic IL, background waiter,
/// CONTROL authority, or arbitrary object payload is admitted.
/// </summary>
public sealed class ActorTypedReplySlots<T> : IDisposable where T : unmanaged
{
    public enum EndpointRole { Actor, SupervisorApplication } // Never CONTROL.
    public enum ReplyMode { OneWay, Unary, Stream }
    public enum WriteState { Written, Full, Terminal, Stale }
    public enum ReadState { Item, Pending, End, Failed, Stale }

    public readonly record struct ReadResult(ReadState State, T Value);

    public sealed class Endpoint
    {
        private Endpoint(EndpointRole role) { Role = role; }
        public EndpointRole Role { get; }
        internal static Endpoint Create(EndpointRole role) => new(role);
    }

    /// <summary>VM-private call binding; never serialize or expose to guest mail.</summary>
    public readonly struct ReplyLease
    {
        internal ReplyLease(WeakReference<ActorTypedReplySlots<T>> ownerRef, Endpoint caller, Endpoint callee,
                            int index, uint generation, ReplyMode mode)
        {
            OwnerRef = ownerRef;
            Caller = caller;
            Callee = callee;
            Index = index;
            Generation = generation;
            Mode = mode;
        }
        internal WeakReference<ActorTypedReplySlots<T>>? OwnerRef { get; }
        internal Endpoint? Caller { get; }
        internal Endpoint? Callee { get; }
        internal int Index { get; }
        internal uint Generation { get; }
        public ReplyMode Mode { get; }
        // Lookup key only; authority additionally requires an authentic Endpoint.
        public ulong LocalHandle => OwnerRef is null ? 0UL :
            ((ulong)Generation << 32) | (uint)(Index + 1);
    }

    private struct Slot
    {
        public Endpoint? Caller;
        public Endpoint? Callee;
        public int Head;
        public int Count;
        public bool Active;
        public bool Terminal;
        public bool Failed;
        public ReplyMode Mode;
    }

    // A leaked lease cannot keep the entire VM reply slab alive.
    private readonly WeakReference<ActorTypedReplySlots<T>> _weakSelf;
    private readonly object _gate = new();
    private readonly Slot[] _slots;
    private readonly T[] _ring;
    private readonly uint[] _generation;
    private readonly int[] _next;
    private readonly HashSet<Endpoint> _endpoints = new();
    private readonly int _perStreamItems;
    private int _freeHead;
    private int _pending;
    private bool _closed;

    public ActorTypedReplySlots(int capacity, int perStreamItems = 16)
    {
        unsafe
        {
            if (sizeof(T) > 256)
                throw new ArgumentOutOfRangeException(nameof(T),
                    "inline reply values over 256 bytes require explicit byte-budget lowering");
        }
        if (capacity <= 0 || capacity > 65_536 ||
            perStreamItems <= 0 || perStreamItems > 256 ||
            capacity > 65_536 / perStreamItems)
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "reply ledger must have bounded slots and inline payload credits");
        _weakSelf = new WeakReference<ActorTypedReplySlots<T>>(this);
        _slots = new Slot[capacity];
        _ring = new T[checked(capacity * perStreamItems)];
        _generation = new uint[capacity];
        _next = new int[capacity];
        _perStreamItems = perStreamItems;
        for (int i = 0; i < capacity; ++i)
            _next[i] = i + 1 == capacity ? -1 : i + 1;
    }

    public Endpoint CreateEndpoint(EndpointRole role)
    {
        if (role is not (EndpointRole.Actor or EndpointRole.SupervisorApplication))
            throw new ArgumentOutOfRangeException(nameof(role), "CONTROL cannot be a reply endpoint");
        lock (_gate)
        {
            CheckOpen();
            var endpoint = Endpoint.Create(role);
            _endpoints.Add(endpoint);
            return endpoint;
        }
    }

    public ReplyLease Reserve(Endpoint caller, Endpoint callee, ReplyMode mode)
    {
        if (mode == ReplyMode.OneWay)
            throw new InvalidOperationException("one-way messages have no reply capability");
        if (mode is not (ReplyMode.Unary or ReplyMode.Stream))
            throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_gate)
        {
            CheckOpen();
            if (!_endpoints.Contains(caller) || !_endpoints.Contains(callee) ||
                ReferenceEquals(caller, callee))
                throw new InvalidOperationException("foreign, stale or self-dependent reply endpoint");
            while (_freeHead >= 0 && _generation[_freeHead] == uint.MaxValue)
                _freeHead = _next[_freeHead]; // permanent retirement, no ABA wrap
            if (_freeHead < 0) throw new InvalidOperationException("reply slots exhausted");
            int index = _freeHead;
            _freeHead = _next[index];
            ref var slot = ref _slots[index];
            slot = new Slot {
                Caller = caller, Callee = callee, Active = true, Mode = mode
            };
            ++_pending;
            return new ReplyLease(_weakSelf, caller, callee, index, ++_generation[index], mode);
        }
    }

    public WriteState TryWrite(ReplyLease lease, Endpoint producer, T value)
    {
        RequireCapability(lease, producer, producerSide: true);
        lock (_gate)
        {
            if (!IsLive(lease, producer)) return WriteState.Stale;
            ref var slot = ref _slots[lease.Index];
            if (slot.Terminal) return WriteState.Terminal;
            if (slot.Count == _perStreamItems) return WriteState.Full;
            _ring[lease.Index * _perStreamItems +
                  (slot.Head + slot.Count) % _perStreamItems] = value;
            ++slot.Count;
            if (slot.Mode == ReplyMode.Unary) slot.Terminal = true;
            return WriteState.Written;
        }
    }

    public WriteState TryCloseStream(ReplyLease lease, Endpoint producer) =>
        Finish(lease, producer, failed: false, requireStream: true);

    public WriteState TryFail(ReplyLease lease, Endpoint producer) =>
        Finish(lease, producer, failed: true, requireStream: false);

    private WriteState Finish(ReplyLease lease, Endpoint producer, bool failed, bool requireStream)
    {
        RequireCapability(lease, producer, producerSide: true);
        lock (_gate)
        {
            if (!IsLive(lease, producer)) return WriteState.Stale;
            ref var slot = ref _slots[lease.Index];
            if (requireStream && slot.Mode != ReplyMode.Stream)
                throw new InvalidOperationException("unary reply is not a stream");
            if (slot.Terminal) return WriteState.Terminal;
            slot.Terminal = true;
            slot.Failed = failed;
            return WriteState.Written;
        }
    }

    public ReadResult Poll(ReplyLease lease, Endpoint caller)
    {
        RequireCapability(lease, caller, producerSide: false);
        lock (_gate)
        {
            if (!IsLive(lease, caller)) return new(ReadState.Stale, default);
            ref var slot = ref _slots[lease.Index];
            if (slot.Count != 0)
            {
                int index = lease.Index * _perStreamItems + slot.Head;
                T value = _ring[index];
                _ring[index] = default;
                slot.Head = (slot.Head + 1) % _perStreamItems;
                --slot.Count;
                if (slot.Mode == ReplyMode.Unary) Release(lease.Index);
                return new(ReadState.Item, value);
            }
            if (!slot.Terminal) return new(ReadState.Pending, default);
            ReadState status = slot.Failed ? ReadState.Failed : ReadState.End;
            Release(lease.Index);
            return new(status, default);
        }
    }

    public bool Cancel(ReplyLease lease, Endpoint caller)
    {
        RequireCapability(lease, caller, producerSide: false);
        lock (_gate)
        {
            if (!IsLive(lease, caller)) return false;
            Release(lease.Index);
            return true;
        }
    }

    public int PendingCount { get { lock (_gate) return _pending; } }

    public void RetireEndpoint(Endpoint endpoint)
    {
        lock (_gate)
        {
            if (!_endpoints.Remove(endpoint))
                throw new InvalidOperationException("foreign or retired reply endpoint");
            for (int i = 0; i < _slots.Length; ++i)
                if (_slots[i].Active &&
                    (ReferenceEquals(_slots[i].Caller, endpoint) ||
                     ReferenceEquals(_slots[i].Callee, endpoint)))
                    Release(i);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _endpoints.Clear();
            for (int i = 0; i < _slots.Length; ++i)
                if (_slots[i].Active) Release(i);
            Array.Clear(_ring); // clear retained inline replies on disposal
        }
    }

    private void CheckOpen()
    {
        if (_closed) throw new ObjectDisposedException(nameof(ActorTypedReplySlots<T>));
    }

    private void RequireCapability(ReplyLease lease, Endpoint endpoint, bool producerSide)
    {
        if (!ReferenceEquals(lease.OwnerRef, _weakSelf) ||
            !ReferenceEquals(endpoint, producerSide ? lease.Callee : lease.Caller))
            throw new UnauthorizedAccessException("reply endpoint authority mismatch");
    }

    private bool IsLive(ReplyLease lease, Endpoint endpoint) =>
        !_closed && _endpoints.Contains(endpoint) &&
        lease.Index >= 0 && lease.Index < _slots.Length &&
        _generation[lease.Index] == lease.Generation &&
        _slots[lease.Index].Active &&
        ReferenceEquals(_slots[lease.Index].Caller, lease.Caller) &&
        ReferenceEquals(_slots[lease.Index].Callee, lease.Callee);

    private void Release(int index)
    {
        Array.Clear(_ring, index * _perStreamItems, _perStreamItems);
        _slots[index] = default;
        _next[index] = _freeHead;
        _freeHead = index;
        --_pending;
    }
}
