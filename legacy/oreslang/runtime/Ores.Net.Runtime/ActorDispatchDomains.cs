using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>
/// Runtime-wide actor execution class. CONTROL is deliberately not a fourth
/// actor execution pool; control work is coordinated and admitted onto one of
/// these existing domains.
/// </summary>
public enum ActorKind
{
    Private,
    Shared,
    Untrusted
}

/// <summary>
/// Parallelism and queue bounds for the three actor dispatch domains.
/// Structures are created eagerly, while carrier threads are created lazily.
/// </summary>
public sealed class ActorDispatcherOptions
{
    public const int MaxWorkersPerDomain = 256;
    public const int MaxQueueCapacityPerDomain = 65_536;

    public int PrivateWorkers { get; init; } = DefaultPrivateWorkers();
    public int SharedWorkers { get; init; } = DefaultSharedWorkers();
    public int UntrustedWorkers { get; init; } = DefaultUntrustedWorkers();
    public int QueueCapacityPerDomain { get; init; } = 16_384;

    /// <summary>
    /// How long a turn queued behind a busy lane waits for its home carrier
    /// before an idle lane may take it. Negative disables idle stealing.
    /// </summary>
    public int StealGraceMicroseconds { get; init; } = 50;

    internal void Validate()
    {
        if (PrivateWorkers <= 0 || PrivateWorkers > MaxWorkersPerDomain)
            throw new ArgumentOutOfRangeException(nameof(PrivateWorkers));
        if (SharedWorkers <= 0 || SharedWorkers > MaxWorkersPerDomain)
            throw new ArgumentOutOfRangeException(nameof(SharedWorkers));
        if (UntrustedWorkers <= 0 || UntrustedWorkers > MaxWorkersPerDomain)
            throw new ArgumentOutOfRangeException(nameof(UntrustedWorkers));
        if (QueueCapacityPerDomain <= 0 ||
            QueueCapacityPerDomain > MaxQueueCapacityPerDomain)
            throw new ArgumentOutOfRangeException(nameof(QueueCapacityPerDomain));
        if (StealGraceMicroseconds > 10_000)
            throw new ArgumentOutOfRangeException(nameof(StealGraceMicroseconds));
    }

    private static int DefaultPrivateWorkers()
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        return Math.Min(MaxWorkersPerDomain,
            cores == 1 ? 1 : Math.Max(1, cores / 2));
    }

    private static int DefaultSharedWorkers()
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        return Math.Min(MaxWorkersPerDomain,
            cores <= 2 ? 1 : Math.Max(1, (cores - DefaultPrivateWorkers()) / 2));
    }

    private static int DefaultUntrustedWorkers()
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        int used = DefaultPrivateWorkers() + DefaultSharedWorkers();
        return Math.Min(MaxWorkersPerDomain, Math.Max(1, cores - used));
    }
}

/// <summary>
/// Three runtime-wide, bounded dispatch domains. Pool objects are eagerly
/// allocated; no carrier thread exists until that domain receives work.
/// </summary>
public sealed class ActorDispatchDomains : IDisposable
{
    private readonly ActorDispatchPool _private;
    private readonly ActorDispatchPool _shared;
    private readonly ActorDispatchPool _untrusted;
    // A second Dispose must not return while another caller still joins live
    // carrier threads. Disposers serialize on this plain join gate rather
    // than blocking on an event, which the async-only runtime audit forbids.
    private readonly object _disposeGate = new();
    private ExceptionDispatchInfo? _disposeFailure;
    private int _closed;

    public ActorDispatchDomains(ActorDispatcherOptions? options = null)
    {
        options ??= new ActorDispatcherOptions();
        options.Validate();

        _private = new ActorDispatchPool(
            "ores-net-private-", options.PrivateWorkers,
            options.QueueCapacityPerDomain, options.StealGraceMicroseconds);
        _shared = new ActorDispatchPool(
            "ores-net-shared-", options.SharedWorkers,
            options.QueueCapacityPerDomain, options.StealGraceMicroseconds);
        _untrusted = new ActorDispatchPool(
            "ores-net-untrusted-", options.UntrustedWorkers,
            options.QueueCapacityPerDomain, options.StealGraceMicroseconds);
    }

    public int PrivateStartedWorkers => _private.StartedWorkers;
    public int SharedStartedWorkers => _shared.StartedWorkers;
    public int UntrustedStartedWorkers => _untrusted.StartedWorkers;
    public int TotalStartedWorkers =>
        PrivateStartedWorkers + SharedStartedWorkers + UntrustedStartedWorkers;

    public long CompletedWorkItems =>
        _private.CompletedWorkItems +
        _shared.CompletedWorkItems +
        _untrusted.CompletedWorkItems;

    public long FaultedWorkItems =>
        _private.FaultedWorkItems +
        _shared.FaultedWorkItems +
        _untrusted.FaultedWorkItems;

    public bool TryDispatch(ActorKind kind, Action work) =>
        TryDispatch(kind, work, -1, out _);

    /// <summary>
    /// Dispatch with a preferred lane (the lane that last ran this actor;
    /// -1 for none). <paramref name="placedLane"/> is where the turn was
    /// queued; an idle lane may still take it after the steal grace.
    /// </summary>
    public bool TryDispatch(ActorKind kind, Action work, int preferredLane, out int placedLane)
    {
        ArgumentNullException.ThrowIfNull(work);
        placedLane = -1;
        if (Volatile.Read(ref _closed) != 0)
            return false;

        return Pool(kind).TryEnqueue(work, preferredLane, out placedLane);
    }

    /// <summary>The calling carrier's lane in its domain, or -1 off-carrier.</summary>
    public static int CurrentLane => ActorDispatchPool.CurrentLaneIndex;

    public long Steals => _private.Steals + _shared.Steals + _untrusted.Steals;

    /// <summary>Accepted turns not yet claimed, across all domains.</summary>
    public int QueuedTurns => _private.QueuedTurns + _shared.QueuedTurns + _untrusted.QueuedTurns;

    public void Dispose()
    {
        // Even if another thread is already disposing, an active worker must
        // never wait on its own termination: that would deadlock shutdown.
        if (_private.IsCurrentWorker ||
            _shared.IsCurrentWorker ||
            _untrusted.IsCurrentWorker)
            throw new InvalidOperationException(
                "ActorDispatchDomains.Dispose cannot join its current carrier");

        // The first disposer holds the gate until every carrier join has
        // finished; later callers enter only afterwards and then replay the
        // original shutdown failure, if any, instead of reporting success.
        lock (_disposeGate)
        {
            if (Interlocked.CompareExchange(ref _closed, 1, 0) != 0)
            {
                _disposeFailure?.Throw();
                return;
            }

            try
            {
                _private.Dispose();
                _shared.Dispose();
                _untrusted.Dispose();
            }
            catch (Exception error)
            {
                _disposeFailure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }
    }

    private ActorDispatchPool Pool(ActorKind kind) => kind switch
    {
        ActorKind.Private => _private,
        ActorKind.Shared => _shared,
        ActorKind.Untrusted => _untrusted,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>
    /// Bounded lanes with lazy persistent carriers (one carrier per lane).
    /// Start of the first carrier occurs before the first item is admitted, so
    /// a failed Thread.Start cannot strand accepted work with no carrier.
    ///
    /// <para>Locality (CONTINUATION_LOCALITY_V1 rule 5): an actor turn is placed
    /// on the lane that last ran the actor while that lane's backlog is within
    /// <see cref="LocalSlack"/>. A turn queued behind other work also sends a
    /// steal probe to one idle lane; the probe waits out the steal grace and
    /// then claims the turn if it is still pending, so an idle carrier is never
    /// reserved for a busy one. The turn's claim word makes it run exactly once.
    /// The actor's own Scheduled flag still serializes its turns.</para>
    /// </summary>
    private sealed class ActorDispatchPool : IDisposable
    {
        private const int LocalSlack = 2;

        [ThreadStatic] private static Lane? t_currentLane;

        private readonly object _gate = new();
        private readonly Lane[] _lanes;
        private readonly int _capacity;
        private readonly string _threadPrefix;
        private readonly long _stealGraceTicks; // Stopwatch ticks; negative disables

        private int _startedLanes;
        private int _queued;
        private bool _closed;
        private long _completed;
        private long _faulted;
        private long _steals;
        private int _workerSequence;
        private int _workerStartFailures;

        internal ActorDispatchPool(string threadPrefix, int maxWorkers,
            int queueCapacity, int stealGraceMicroseconds)
        {
            _threadPrefix = threadPrefix;
            _capacity = queueCapacity;
            _stealGraceTicks = stealGraceMicroseconds < 0
                ? -1
                : (long)stealGraceMicroseconds * System.Diagnostics.Stopwatch.Frequency / 1_000_000;
            _lanes = new Lane[maxWorkers];
            for (int i = 0; i < maxWorkers; ++i)
                _lanes[i] = new Lane(this, i);
        }

        internal static int CurrentLaneIndex => t_currentLane?.Index ?? -1;

        internal int StartedWorkers
        {
            get { lock (_gate) return _startedLanes; }
        }

        internal bool IsCurrentWorker => t_currentLane?.Pool == this;

        internal int WorkerStartFailures =>
            Volatile.Read(ref _workerStartFailures);

        internal long CompletedWorkItems => Interlocked.Read(ref _completed);

        internal long FaultedWorkItems => Interlocked.Read(ref _faulted);

        internal long Steals => Interlocked.Read(ref _steals);

        /// <summary>Accepted turns not yet claimed by a carrier.</summary>
        internal int QueuedTurns => Volatile.Read(ref _queued);

        internal bool TryEnqueue(Action work, int preferredLane, out int placedLane)
        {
            placedLane = -1;
            Lane? probeLane = null;
            Turn turn;
            lock (_gate)
            {
                if (_closed || Volatile.Read(ref _queued) >= _capacity)
                    return false;

                // Failure to create the first carrier must happen before work
                // is published into any lane.
                if (_startedLanes == 0)
                    StartLaneLocked();

                Lane target = SelectLaneLocked(preferredLane);
                bool behindOtherWork = target.Queued > 0 || target.Running > 0;
                turn = new Turn(this, work, target);
                // Decrements happen outside _gate (on claim), so this must be atomic too.
                Interlocked.Increment(ref _queued);
                Interlocked.Increment(ref target.Queued);
                target.Queue.Add(turn);
                placedLane = target.Index;
                if (behindOtherWork && _stealGraceTicks >= 0)
                    probeLane = ClaimIdleProbeLaneLocked(target);
                if (probeLane is not null)
                    probeLane.Queue.Add(new StealProbe(turn));
            }
            return true;
        }

        private Lane SelectLaneLocked(int preferredLane)
        {
            // Soft affinity: the lane that last ran this actor, while its
            // backlog is small.
            if (preferredLane >= 0 && preferredLane < _startedLanes
                    && _lanes[preferredLane].Queued < LocalSlack)
                return _lanes[preferredLane];
            for (int i = 0; i < _startedLanes; ++i)
            {
                Lane lane = _lanes[i];
                if (lane.Queued == 0 && Volatile.Read(ref lane.Running) == 0)
                    return lane;
            }
            // Every started lane is busy: start one more carrier when allowed.
            // A failure here cannot strand work, since a carrier exists.
            if (_startedLanes < _lanes.Length)
            {
                try
                {
                    return StartLaneLocked();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _workerStartFailures);
                }
            }
            Lane best = preferredLane >= 0 && preferredLane < _startedLanes
                ? _lanes[preferredLane] : _lanes[0];
            for (int i = 0; i < _startedLanes; ++i)
                if (_lanes[i].Queued < best.Queued) best = _lanes[i];
            return best;
        }

        private Lane? ClaimIdleProbeLaneLocked(Lane home)
        {
            for (int i = 0; i < _startedLanes; ++i)
            {
                Lane lane = _lanes[i];
                if (lane == home) continue;
                if (lane.Queued == 0 && Volatile.Read(ref lane.Running) == 0
                        && Interlocked.CompareExchange(ref lane.Probe, 1, 0) == 0)
                    return lane;
            }
            return null;
        }

        public void Dispose()
        {
            Thread?[] workers;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                workers = new Thread?[_startedLanes];
                for (int i = 0; i < _startedLanes; ++i)
                {
                    _lanes[i].Queue.CompleteAdding();
                    workers[i] = _lanes[i].Worker;
                }
            }

            foreach (Thread? worker in workers)
                worker?.Join();

            foreach (Lane lane in _lanes)
                lane.Queue.Dispose();
        }

        private Lane StartLaneLocked()
        {
            Lane lane = _lanes[_startedLanes];
            int sequence = checked(++_workerSequence);
            var worker = new Thread(() => WorkerLoop(lane))
            {
                IsBackground = true,
                Name = _threadPrefix + sequence
            };
            lane.Worker = worker;
            worker.Start(); // throws before the lane counts as started
            ++_startedLanes;
            return lane;
        }

        private void WorkerLoop(Lane lane)
        {
            t_currentLane = lane;
            foreach (WorkItem item in lane.Queue.GetConsumingEnumerable())
                item.Execute(lane);
        }

        private bool Closed
        {
            get { lock (_gate) return _closed; }
        }

        private sealed class Lane
        {
            internal readonly ActorDispatchPool Pool;
            internal readonly int Index;
            internal readonly BlockingCollection<WorkItem> Queue =
                new(new ConcurrentQueue<WorkItem>());
            internal Thread? Worker;
            internal int Queued;  // turns placed here and not yet claimed
            internal int Running; // turns executing on this lane's carrier
            internal int Probe;   // 1 while a steal probe is queued or waiting

            internal Lane(ActorDispatchPool pool, int index)
            {
                Pool = pool;
                Index = index;
            }
        }

        private abstract class WorkItem
        {
            internal abstract void Execute(Lane lane);
        }

        private sealed class Turn : WorkItem
        {
            private readonly ActorDispatchPool _pool;
            private readonly Action _work;
            private readonly Lane _assigned;
            private int _claim; // 0 pending, 1 claimed
            internal readonly long EnqueuedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            internal Turn(ActorDispatchPool pool, Action work, Lane assigned)
            {
                _pool = pool;
                _work = work;
                _assigned = assigned;
            }

            internal bool Pending => Volatile.Read(ref _claim) == 0;

            internal override void Execute(Lane lane) => TryRunOn(lane);

            internal bool TryRunOn(Lane lane)
            {
                if (Interlocked.CompareExchange(ref _claim, 1, 0) != 0)
                    return false;
                Interlocked.Increment(ref lane.Running);
                Interlocked.Decrement(ref _assigned.Queued);
                Interlocked.Decrement(ref _pool._queued);
                try
                {
                    _work();
                }
                catch
                {
                    Interlocked.Increment(ref _pool._faulted);
                }
                finally
                {
                    Interlocked.Increment(ref _pool._completed);
                    Interlocked.Decrement(ref lane.Running);
                }
                return true;
            }
        }

        private sealed class StealProbe : WorkItem
        {
            private readonly Turn _target;

            internal StealProbe(Turn target) => _target = target;

            internal override void Execute(Lane lane)
            {
                ActorDispatchPool pool = lane.Pool;
                bool stillIdle;
                try
                {
                    // Short bounded spin (default 50 us): only an idle lane
                    // steals, and it stops waiting once it has work of its own.
                    var spin = new SpinWait();
                    while (_target.Pending
                            && Volatile.Read(ref lane.Queued) == 0
                            && System.Diagnostics.Stopwatch.GetTimestamp() - _target.EnqueuedTicks
                                < pool._stealGraceTicks
                            && !pool.Closed)
                        spin.SpinOnce(-1);
                    stillIdle = Volatile.Read(ref lane.Queued) == 0 && !pool.Closed;
                }
                finally
                {
                    Volatile.Write(ref lane.Probe, 0);
                }
                if (stillIdle && _target.TryRunOn(lane))
                    Interlocked.Increment(ref pool._steals);
            }
        }
    }
}
