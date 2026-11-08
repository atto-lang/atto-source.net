namespace Ores.Net.Runtime;

/// <summary>
/// Host-owned metering primitive for future compiler-injected AttoLang CFG
/// backedges. This alone does not instrument arbitrary managed code, provide
/// guest actor execution, or constitute a tenant sandbox.
/// </summary>
public sealed class GuestCooperationBudget
{
    public const int PollInterval = 128;
    public const ulong MaxLogicalFuel = 100_000_000UL;

    private ulong _remainingFuel;
    private uint _ticks;
    private ulong _dispatchTicks;

    public GuestCooperationBudget(ulong logicalFuel)
    {
        if (logicalFuel == 0 || logicalFuel > MaxLogicalFuel)
            throw new ArgumentOutOfRangeException(nameof(logicalFuel));
        _remainingFuel = logicalFuel;
    }

    public ulong RemainingFuel => _remainingFuel;
    public ulong BackedgesThisDispatch => _dispatchTicks;

    // Reset only dispatch fairness; never restart the poll phase or fuel.
    // Otherwise repeated 127-step resumes could avoid every cold check.
    public void BeginDispatch() => _dispatchTicks = 0;

    // Hot path: no clock, timer, task await, allocation or scheduler callback.
    // Must be emitted after committing loop-carried state. Returning true only
    // authorizes a slow poll, never suspension under critical/borrow guards.
    public bool ChargeBackedge(ulong weight = 1)
    {
        ChargeOperation(weight);
        _dispatchTicks++;
        _ticks = (_ticks + 1U) & ((uint)PollInterval - 1U);
        return _ticks == 0;
    }

    public void ChargeOperation(ulong weight)
    {
        if (weight == 0 || weight > MaxLogicalFuel)
            throw new ArgumentOutOfRangeException(nameof(weight));
        if (weight > _remainingFuel)
            throw new InvalidOperationException("logical guest fuel exhausted");
        _remainingFuel -= weight;
    }
}
