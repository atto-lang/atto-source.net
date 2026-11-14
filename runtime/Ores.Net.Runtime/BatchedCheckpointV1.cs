namespace Ores.Net.Runtime;

/// <summary>
/// Host-only reference for a compiler-proven bounded AttoLang work unit.
/// Not an untrusted sandbox or proof of guest preemption. Preserve this object
/// for the entire logical task; cooperation/await must never refill hard fuel.
/// </summary>
public sealed class BatchedCheckpointV1
{
    private readonly ulong _interval;
    private readonly Action<ulong, ulong> _poll;
    private ulong _fuel;
    private ulong _pending;
    private ulong _next;
    private bool _inPoll;
    private bool _terminal;

    public BatchedCheckpointV1(ulong hardFuel, Action<ulong, ulong> trustedPoll,
        int interval = 128)
    {
        if (interval is not (64 or 128 or 256 or 512))
            throw new ArgumentOutOfRangeException(nameof(interval));
        _poll = trustedPoll ?? throw new ArgumentNullException(nameof(trustedPoll));
        _interval = (ulong)interval;
        _fuel = hardFuel;
        _next = Math.Min(_interval, _fuel);
    }

    /// <summary>
    /// Called after one verified bounded work unit is committed. No clock,
    /// scheduler or poll delegate is invoked on ordinary fast-path units.
    /// </summary>
    public bool CompletedWorkUnit()
    {
        if (_terminal || _inPoll)
            throw new InvalidOperationException("Checkpoint is terminal or reentrant");
        if (_fuel == _pending)
        {
            _terminal = true;
            throw new InvalidOperationException("Hard execution fuel exhausted");
        }

        ++_pending; // Never exceeds 512, even when hardFuel is ulong.MaxValue.
        if (_pending < _next) return false;

        ulong charged = _pending;
        _fuel -= charged;
        _pending = 0;
        _next = Math.Min(_interval, _fuel);
        _inPoll = true;
        try
        {
            _poll(charged, _fuel);
        }
        catch
        {
            _terminal = true;
            throw;
        }
        finally
        {
            _inPoll = false;
        }
        return true;
    }

    public ulong RemainingFuel => _fuel - _pending;
}
