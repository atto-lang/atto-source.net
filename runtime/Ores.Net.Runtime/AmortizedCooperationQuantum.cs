namespace Ores.Net.Runtime;

/// <summary>
/// Task-owned, non-atomic reduction counter for future compiler-injected
/// safepoints. The hot path performs no clock read, scheduler lookup, allocation
/// or host callback. It does NOT by itself interrupt a running CIL method.
/// Hard execution fuel and a resumable continuation must be enforced separately.
/// </summary>
public sealed class AmortizedCooperationQuantum
{
    public const int SlowPollInterval = 128;
    public const ulong FairnessBackedges = 512;
    private int _window;
    private ulong _completed;

    public ulong CompletedThisDispatch => _completed;
    public bool FairnessBudgetExpired => _completed >= FairnessBackedges;

    public void BeginDispatch()
    {
        _completed = 0;
        _window = 0;
    }

    // Call after each committed reduction. True ONLY at exact 128 boundaries,
    // including after the fairness limit and during a no-yield critical scope.
    // The compiler may act on the result only with a proven resumable frame.
    public bool CompletedBackedgeNeedsSlowPoll()
    {
        if (_completed != ulong.MaxValue) _completed++;
        if (++_window != SlowPollInterval) return false;
        _window = 0;
        return true;
    }
}
