namespace Atto.Net.ActorRuntime;

/// <summary>
/// A poll is an accounting signal only, not permission to suspend while a
/// borrow, lock, critical section, or native frame is live.
/// </summary>
public readonly record struct GuestChargeResult(bool PollDue, bool DispatchFairnessExhausted);

public readonly record struct GuestMeterSnapshot(
    ulong RemainingFuel, uint PollPhase, ulong BackedgesThisDispatch);

/// <summary>Host-owned metering, serialized by the owning actor arena.</summary>
internal sealed class GuestCooperationBudget
{
    internal const uint PollInterval = 128;
    internal const ulong MaximumLogicalFuel = 100_000_000;
    internal const ulong MaximumCharge = PollInterval;
    internal const ulong DispatchFairnessBackedges = 512;

    private ulong _fuel;
    private uint _phase;
    private ulong _backedgesThisDispatch;

    internal GuestCooperationBudget(ulong fuel)
    {
        if (fuel is 0 or > MaximumLogicalFuel)
            throw new ArgumentOutOfRangeException(nameof(fuel));
        _fuel = fuel;
    }

    // Preserve both phase and hard fuel across all physical turns.
    internal void BeginDispatch() => _backedgesThisDispatch = 0;

    internal GuestMeterSnapshot Snapshot() =>
        new(_fuel, _phase, _backedgesThisDispatch);

    internal GuestChargeResult Charge(ulong weight, bool backedge)
    {
        // Invalid compiler-supplied weights must not debit any state.
        if (weight is 0 or > MaximumCharge)
            throw new ArgumentOutOfRangeException(nameof(weight));
        if (weight > _fuel)
            throw new ActorResourceExhaustedException("Actor logical fuel exhausted");

        _fuel -= weight;
        ulong total = (ulong)_phase + weight; // bounded by 255
        _phase = (uint)(total % PollInterval);
        if (backedge) _backedgesThisDispatch++;
        return new GuestChargeResult(
            PollDue: total >= PollInterval,
            DispatchFairnessExhausted: _backedgesThisDispatch >= DispatchFairnessBackedges);
    }
}
