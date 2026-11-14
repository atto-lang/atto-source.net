namespace Ores.Net.Runtime;

/// <summary>
/// Per-logical-actor reduction metering for compiler-proven bounded work.
/// Pure Native AOT-compatible host primitive: does not capture/resume guest
/// stack frames and is not a security boundary for an uninstrumented handler.
/// </summary>
public sealed class ActorCooperationMeter
{
    public const ulong DefaultDispatchBudget = 512;
    private readonly int _interval;
    private int _phase;
    private ulong _completed;
    // Fail closed if two carrier threads concurrently enter this task-owned
    // meter. A scheduler handoff may migrate ownership only after return.
    private int _mutating;

    public ActorCooperationMeter(int interval = 128)
    {
        if (interval is not (128 or 512))
            throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval;
    }

    public ulong Completed => _completed;
    public int Phase => _phase;
    public int Interval => _interval;

    // Call on real actor dispatch only. Do not reset inside lexical critical
    // sections, recursive calls, or nested user loops.
    public void BeginDispatch()
    {
        if (Interlocked.CompareExchange(ref _mutating, 1, 0) != 0)
            throw new InvalidOperationException("Reentrant or concurrent cooperation dispatch reset");
        try
        {
            _phase = 0;
            _completed = 0;
        }
        finally { Volatile.Write(ref _mutating, 0); }
    }

    /// <summary>
    /// Account for bounded, committed units. The callback is called only
    /// on a 128/512 boundary, never once per ordinary iteration, even after
    /// exceeding the dispatch budget. At most one boundary can be crossed.
    /// </summary>
    public bool CommitBatch(int work, Action<ulong, bool> onCheckpoint)
    {
        if (work <= 0 || work > _interval)
            throw new ArgumentOutOfRangeException(nameof(work));
        ArgumentNullException.ThrowIfNull(onCheckpoint);
        if (Interlocked.CompareExchange(ref _mutating, 1, 0) != 0)
            throw new InvalidOperationException("Reentrant or concurrent cooperation charge");
        try
        {
            int next = _phase + work; // <= 1024, no signed overflow
            bool due = next >= _interval;
            _phase = next % _interval;
            _completed = ulong.MaxValue - _completed < (ulong)work
                ? ulong.MaxValue : _completed + (ulong)work;
            if (due)
            {
                // The callback is synchronous; the atomic owner fence spans
                // the callback so another carrier cannot reset or double
                // charge this logical actor during a scheduler handoff.
                onCheckpoint(_completed, _completed >= DefaultDispatchBudget);
            }
            return due;
        }
        finally { Volatile.Write(ref _mutating, 0); }
    }
}
