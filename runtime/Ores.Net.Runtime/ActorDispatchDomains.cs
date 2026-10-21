using System.Collections.Concurrent;
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
    private int _closed;

    public ActorDispatchDomains(ActorDispatcherOptions? options = null)
    {
        options ??= new ActorDispatcherOptions();
        options.Validate();

        _private = new ActorDispatchPool(
            "ores-net-private-", options.PrivateWorkers,
            options.QueueCapacityPerDomain);
        _shared = new ActorDispatchPool(
            "ores-net-shared-", options.SharedWorkers,
            options.QueueCapacityPerDomain);
        _untrusted = new ActorDispatchPool(
            "ores-net-untrusted-", options.UntrustedWorkers,
            options.QueueCapacityPerDomain);
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

    public bool TryDispatch(ActorKind kind, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Volatile.Read(ref _closed) != 0)
            return false;

        return Pool(kind).TryEnqueue(work);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _closed) != 0)
            return;
        if (_private.IsCurrentWorker ||
            _shared.IsCurrentWorker ||
            _untrusted.IsCurrentWorker)
            throw new InvalidOperationException(
                "ActorDispatchDomains.Dispose cannot join its current carrier");

        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        _private.Dispose();
        _shared.Dispose();
        _untrusted.Dispose();
    }

    private ActorDispatchPool Pool(ActorKind kind) => kind switch
    {
        ActorKind.Private => _private,
        ActorKind.Shared => _shared,
        ActorKind.Untrusted => _untrusted,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>
    /// Bounded work queue with lazy persistent carriers. Start of the first
    /// worker occurs before the first item is admitted, so a failed Thread.Start
    /// cannot strand accepted work with no carrier.
    /// </summary>
    private sealed class ActorDispatchPool : IDisposable
    {
        private readonly object _gate = new();
        private readonly BlockingCollection<Action> _queue;
        private readonly List<Thread> _workers = new();
        private readonly int _maxWorkers;
        private readonly string _threadPrefix;

        private int _workerSequence;
        private bool _closed;
        private long _completed;
        private long _faulted;
        private int _workerStartFailures;

        internal ActorDispatchPool(string threadPrefix, int maxWorkers,
            int queueCapacity)
        {
            _threadPrefix = threadPrefix;
            _maxWorkers = maxWorkers;
            _queue = new BlockingCollection<Action>(
                new ConcurrentQueue<Action>(), queueCapacity);
        }

        internal int StartedWorkers
        {
            get { lock (_gate) return _workers.Count; }
        }

        internal bool IsCurrentWorker
        {
            get
            {
                lock (_gate)
                    return _workers.Contains(Thread.CurrentThread);
            }
        }

        internal int WorkerStartFailures =>
            Volatile.Read(ref _workerStartFailures);

        internal long CompletedWorkItems =>
            Interlocked.Read(ref _completed);

        internal long FaultedWorkItems =>
            Interlocked.Read(ref _faulted);

        internal bool TryEnqueue(Action work)
        {
            lock (_gate)
            {
                if (_closed)
                    return false;

                // Failure to create the first carrier must happen before work
                // is published into the queue.
                if (_workers.Count == 0)
                    StartWorkerLocked();

                if (!_queue.TryAdd(work))
                    return false;

                // Additional carrier creation is an optimization after the
                // work item has a guaranteed existing carrier. A failure here
                // cannot strand accepted work.
                if (_workers.Count < _maxWorkers &&
                    _queue.Count > _workers.Count)
                {
                    try
                    {
                        StartWorkerLocked();
                    }
                    catch (Exception) when (_workers.Count != 0)
                    {
                        Interlocked.Increment(ref _workerStartFailures);
                    }
                }

                return true;
            }
        }

        public void Dispose()
        {
            Thread[] workers;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                _queue.CompleteAdding();
                workers = _workers.ToArray();
            }

            foreach (Thread worker in workers)
                worker.Join();

            _queue.Dispose();
        }

        private void StartWorkerLocked()
        {
            if (_workers.Count >= _maxWorkers)
                return;

            int sequence = checked(++_workerSequence);
            var worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = _threadPrefix + sequence
            };
            _workers.Add(worker);
            try
            {
                worker.Start();
            }
            catch
            {
                _workers.Remove(worker);
                throw;
            }
        }

        private void WorkerLoop()
        {
            foreach (Action work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch
                {
                    Interlocked.Increment(ref _faulted);
                }
                finally
                {
                    Interlocked.Increment(ref _completed);
                }
            }
        }
    }
}
