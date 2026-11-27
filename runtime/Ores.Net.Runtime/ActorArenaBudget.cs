namespace Ores.Net.Runtime;

/// <summary>
/// Aggregate reservation budget for actor-owned unmanaged arenas. Capacity is
/// charged before native allocation and refunded only after physical reclaim.
/// Multiple runtimes/tenants may provide distinct budget instances.
/// </summary>
public sealed class ActorArenaBudget
{
    public const long DefaultProcessLimitBytes = 1024L * 1024 * 1024;

    public static ActorArenaBudget Process { get; } =
        new(DefaultProcessLimitBytes);

    private readonly object _gate = new();
    private long _reservedBytes;
    private bool _closed;
    private readonly bool _child;

    public ActorArenaBudget(long limitBytes) : this(limitBytes, false) { }

    private ActorArenaBudget(long limitBytes, bool child)
    {
        _child = child;
        if (limitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limitBytes));
        LimitBytes = limitBytes;
    }

    public long LimitBytes { get; }

    public long ReservedBytes
    {
        get { lock (_gate) return _reservedBytes; }
    }

    /// <summary>Host-only tenant promise, including physical relocation peak.
    /// The process-wide budget continues charging actual native bytes separately.
    /// </summary>
    public CapacityReservation ReserveCapacity(int bytes)
    {
        if (ReferenceEquals(this, Process) || _child)
            throw new InvalidOperationException("Only a root tenant budget may reserve capacity");
        var reservation = new CapacityReservation(this, bytes);
        Reserve(bytes);
        return reservation;
    }

    public sealed class CapacityReservation : IDisposable
    {
        private readonly ActorArenaBudget _parent;
        private readonly int _bytes;
        public ActorArenaBudget Budget { get; }
        internal CapacityReservation(ActorArenaBudget parent, int bytes)
        {
            _parent = parent;
            _bytes = bytes;
            Budget = new ActorArenaBudget(bytes, true);
        }
        // Trusted caller must dispose every associated arena and establish
        // scheduler/FFI quiescence before releasing the promise.
        public void Dispose()
        {
            lock (Budget._gate)
            {
                if (Budget._closed) return;
                if (Budget._reservedBytes != 0)
                    throw new InvalidOperationException("Actor arena reservation still owns native backing");
                _parent.Refund(_bytes);
                Budget._closed = true;
            }
        }
    }

    internal void Reserve(int bytes)
    {
        if (bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("Actor arena reservation retired");
            if (_reservedBytes > LimitBytes - bytes)
                throw new InvalidOperationException(
                    "Aggregate actor arena native-memory budget exhausted");
            _reservedBytes += bytes;
        }
    }

    internal void Refund(int bytes)
    {
        if (bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        lock (_gate)
        {
            if (_reservedBytes < bytes)
                throw new InvalidOperationException(
                    "Actor arena budget refund would underflow");
            _reservedBytes -= bytes;
        }
    }
}
