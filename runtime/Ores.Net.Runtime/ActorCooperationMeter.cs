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
    private bool _servicingCheckpoint;

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
        if (_servicingCheckpoint)
            throw new InvalidOperationException("Reentrant cooperation dispatch reset");
        _phase = 0;
        _completed = 0;
    }

    /// <summary>
    /// Account for bounded, committed units. The callback is called only
    /// on a 128/512 boundary, never once per ordinary iteration, even after
    /// exceeding the dispatch budget. At most one boundary can be crossed.
    /// </summary>
    public bool CommitBatch(int work, Action<ulong, bool> onCheckpoint)
    {
        if (_servicingCheckpoint)
            throw new InvalidOperationException("Reentrant cooperation charge");
        if (work <= 0 || work > _interval)
            throw new ArgumentOutOfRangeException(nameof(work));
        ArgumentNullException.ThrowIfNull(onCheckpoint);
        int next = _phase + work;
        bool due = next >= _interval;
        _phase = next % _interval;
        _completed = ulong.MaxValue - _completed < (ulong)work
            ? ulong.MaxValue : _completed + (ulong)work;
        if (due)
        {
            // Prevent an accidental scheduler callback from restarting or
            // recursively charging the actor. The charge is committed even
            // if the trusted slow path throws; no duplicate checkpoint replay.
            _servicingCheckpoint = true;
            try
            {
                onCheckpoint(_completed, _completed >= DefaultDispatchBudget);
            }
            finally
            {
                _servicingCheckpoint = false;
            }
        }
        return due;
    }
}
