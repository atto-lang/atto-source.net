using System;
using System.Collections.Generic;

namespace Ores.Net.Runtime;

/// <summary>
/// V1 physical actor carrier policy. Target contract: atto-spec scheduling policy V1.
/// Host-only admission primitive; source-level .atto lowering is NOT implemented.
/// </summary>
public enum ActorElasticAffinityV1
{
    None,
    Sticky,
    Pinned,
    Dedicated
}

/// <summary>
/// Bounded, nonparking and NativeAOT-compatible per-actor execution-segment gate.
/// An await releases the physical Lease but NOT an independent logical receive
/// permit, Task identity, heap loan, or continuation generation.
///
/// A trusted pool manager MUST pass real healthy worker identities, pool ID and
/// generation; guest code must never be handed this authority or this class.
/// This does NOT reserve cross-actor/global workers, provide tenant isolation,
/// implement a worker pool, or turn on source actor concurrency by itself.
/// </summary>
public sealed class ActorElasticAdmissionV1 : IDisposable
{
    public const int PoolSize = -1;
    public const int MaxWorkerSlots = 256;

    private readonly object _sync = new();
    private readonly long _actorIncarnation;
    private readonly long _poolAuthority;
    private readonly long _poolGeneration;
    private readonly int _requested;
    private readonly int _actorLimit;
    private readonly int _tenantLimit;
    private readonly int _globalLimit;
    private readonly ActorElasticAffinityV1 _affinity;
    private readonly bool _dedicatedReservation;
    private readonly Dictionary<int, long> _healthy = new();
    private readonly HashSet<int> _busyLanes = new();
    private readonly HashSet<Lease> _live = new();
    private long _lastTicket;
    private int _peak;
    private int _homeLane = -1;
    private long _homeWorkerGeneration;
    private bool _stopped;

    public ActorElasticAdmissionV1(
        long actorIncarnation,
        long poolAuthority,
        long poolGeneration,
        int maxParallelism,
        bool serialActor,
        ActorElasticAffinityV1 affinity,
        bool dedicatedReservation,
        int actorLimit,
        int tenantLimit,
        int globalLimit)
    {
        if (actorIncarnation <= 0 || poolAuthority <= 0 || poolGeneration <= 0 ||
            (maxParallelism != PoolSize && maxParallelism <= 0) ||
            actorLimit <= 0 || tenantLimit <= 0 || globalLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxParallelism),
                "E_ACTOR_PARALLELISM_CAP");
        if (!Enum.IsDefined(affinity))
            throw new ArgumentOutOfRangeException(nameof(affinity), "E_ACTOR_AFFINITY");
        if (serialActor && maxParallelism != 1)
            throw new ArgumentException("E_SERIAL_PARALLELISM", nameof(maxParallelism));
        if ((affinity is ActorElasticAffinityV1.Pinned or ActorElasticAffinityV1.Dedicated)
            && maxParallelism != 1)
            throw new ArgumentException("E_AFFINITY_REQUIRES_ONE", nameof(maxParallelism));

        _actorIncarnation = actorIncarnation;
        _poolAuthority = poolAuthority;
        _poolGeneration = poolGeneration;
        _requested = maxParallelism;
        _actorLimit = actorLimit;
        _tenantLimit = tenantLimit;
        _globalLimit = globalLimit;
        _affinity = affinity;
        _dedicatedReservation = dedicatedReservation;
    }

    /// <summary>
    /// Atomically replace the trusted healthy-worker inventory. Workers that
    /// disappear retain their outstanding physical leases until retirement.
    /// An empty set is valid (lazy worker pool or drained pool).
    /// </summary>
    public void ReplaceHealthyWorkers(IReadOnlyDictionary<int, long> workers)
    {
        ArgumentNullException.ThrowIfNull(workers);
        // Validate/copy before mutating the running policy (failure atomicity).
        if (workers.Count > MaxWorkerSlots)
            throw new ArgumentOutOfRangeException(nameof(workers), "E_WORKER_LIMIT");
        var replacement = new Dictionary<int, long>(workers.Count);
        foreach (var worker in workers)
        {
            if (worker.Key < 0 || worker.Key >= MaxWorkerSlots || worker.Value <= 0 ||
                !replacement.TryAdd(worker.Key, worker.Value))
                throw new ArgumentException("E_INVALID_WORKER_GENERATION", nameof(workers));
        }
        lock (_sync)
        {
            if (_stopped) throw new InvalidOperationException("E_ACTOR_STOPPED");
            _healthy.Clear();
            foreach (var worker in replacement)
                _healthy.Add(worker.Key, worker.Value);
            ResetRetiredHomeIfQuiescent();
        }
    }

    private int CapacityLocked()
    {
        int cap = _requested == PoolSize ? _healthy.Count : _requested;
        return Math.Min(Math.Min(cap, _healthy.Count),
            Math.Min(_actorLimit, Math.Min(_tenantLimit, _globalLimit)));
    }

    public int CurrentCapacity
    {
        get { lock (_sync) return CapacityLocked(); }
    }

    public int Running
    {
        get { lock (_sync) return _live.Count; }
    }

    public int PeakRunning
    {
        get { lock (_sync) return _peak; }
    }

    public int HomeLane
    {
        get { lock (_sync) return _homeLane; }
    }

    /// <summary>
    /// Try to take one *actual running* physical carrier on a trusted worker.
    /// Returns null rather than parking a caller when saturated.
    /// This worker lease must be composed with the VM-global budget and the
    /// existing ActorRuntime/ActorDispatchDomains authority at real dispatch.
    /// </summary>
    public Lease? TryEnter(long actorIncarnation, long poolAuthority,
        long poolGeneration, int lane, long workerGeneration)
    {
        if (actorIncarnation != _actorIncarnation ||
            poolAuthority != _poolAuthority || poolGeneration != _poolGeneration)
            throw new System.Security.SecurityException("E_STALE_ACTOR_OR_POOL");
        lock (_sync)
        {
            if (_stopped || workerGeneration <= 0 ||
                !_healthy.TryGetValue(lane, out long currentGeneration) ||
                currentGeneration != workerGeneration ||
                _live.Count >= CapacityLocked() || _busyLanes.Contains(lane))
                return null;
            if (_affinity == ActorElasticAffinityV1.Dedicated && !_dedicatedReservation)
                return null;
            if ((_affinity is ActorElasticAffinityV1.Pinned or ActorElasticAffinityV1.Dedicated)
                && _homeLane >= 0 &&
                (_homeLane != lane || _homeWorkerGeneration != workerGeneration) &&
                (_live.Count != 0 || _healthy.TryGetValue(_homeLane, out long homeGeneration) &&
                    homeGeneration == _homeWorkerGeneration))
                return null;
            if (_lastTicket == long.MaxValue)
                throw new InvalidOperationException("E_ACTOR_LEASE_EXHAUSTED");

            var lease = new Lease(this, ++_lastTicket, lane, workerGeneration);
            if (!_busyLanes.Add(lane) || !_live.Add(lease))
                throw new InvalidOperationException("E_DOUBLE_ADMISSION");
            _peak = Math.Max(_peak, _live.Count);
            if (_homeLane < 0 ||
                (_affinity is ActorElasticAffinityV1.Pinned or ActorElasticAffinityV1.Dedicated))
            {
                _homeLane = lane;
                _homeWorkerGeneration = workerGeneration;
            }
            return lease;
        }
    }

    private void ResetRetiredHomeIfQuiescent()
    {
        if (_live.Count == 0 && _homeLane >= 0 &&
            (!_healthy.TryGetValue(_homeLane, out long generation) ||
             generation != _homeWorkerGeneration))
        {
            _homeLane = -1;
            _homeWorkerGeneration = 0;
        }
    }

    private void Release(Lease lease)
    {
        lock (_sync)
        {
            if (!_live.Remove(lease))
                throw new InvalidOperationException("E_FOREIGN_OR_DUPLICATE_LEASE");
            if (!_busyLanes.Remove(lease.Lane))
                throw new InvalidOperationException("E_PHYSICAL_LANE_UNDERFLOW");
            ResetRetiredHomeIfQuiescent();
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly ActorElasticAdmissionV1 _owner;
        private bool _released;

        internal Lease(ActorElasticAdmissionV1 owner, long ticket, int lane, long workerGeneration)
        {
            _owner = owner;
            Ticket = ticket;
            Lane = lane;
            WorkerGeneration = workerGeneration;
        }

        public long Ticket { get; }
        public int Lane { get; }
        public long WorkerGeneration { get; }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (_released) return;
                _owner.Release(this);
                _released = true;
            }
        }
    }

    /// <summary>
    /// Fence new segments now. Outstanding leases remain charged until their
    /// physical callback finally blocks execute. No unsafe eager reclamation.
    /// </summary>
    public void Stop()
    {
        lock (_sync) _stopped = true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _stopped = true;
            if (_live.Count != 0)
                throw new InvalidOperationException("E_PHYSICAL_LEASES_OUTSTANDING");
            _healthy.Clear();
        }
    }
}
