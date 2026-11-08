namespace Ores.Net.Runtime;

/// <summary>
/// Task-owned, non-atomic reduction counter for future compiler-injected
/// safepoints. A committed backedge only decrements a small integer; the
/// aggregate and optional slow scheduler polling run once every 128 ticks.
/// This does NOT itself interrupt arbitrary CIL/NativeAOT guest execution.
/// </summary>
public sealed class AmortizedCooperationQuantum
{
    public const int SlowPollInterval = 128;
    public const ulong FairnessBackedges = 512;
    private int _ticksRemaining = SlowPollInterval;
    private ulong _completedBatches;

    private static ulong SaturatingAdd(ulong a, ulong b) =>
        b > ulong.MaxValue - a ? ulong.MaxValue : a + b;

    public ulong CompletedThisDispatch =>
        SaturatingAdd(_completedBatches, (ulong)(SlowPollInterval - _ticksRemaining));
    public bool FairnessBudgetExpired => CompletedThisDispatch >= FairnessBackedges;

    public void BeginDispatch()
    {
        // Only the fairness count resets. A separately enforced hard tenant
        // execution quota must never be renewed by a dispatch handoff.
        _completedBatches = 0;
        _ticksRemaining = SlowPollInterval;
    }

    // True only after each 128 committed reductions, including when overdue.
    // No system clock, scheduler check, allocation or FFI runs per iteration.
    // Only a verified resumable frame may turn this into a cooperative yield.
    public bool CompletedBackedgeNeedsSlowPoll()
    {
        if (--_ticksRemaining != 0) return false;
        _ticksRemaining = SlowPollInterval;
        _completedBatches = SaturatingAdd(_completedBatches, SlowPollInterval);
        return true;
    }
}
