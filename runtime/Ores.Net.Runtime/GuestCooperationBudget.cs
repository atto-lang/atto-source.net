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

    public GuestCooperationBudget(ulong logicalFuel)
    {
        if (logicalFuel == 0 || logicalFuel > MaxLogicalFuel)
            throw new ArgumentOutOfRangeException(nameof(logicalFuel));
        _remainingFuel = logicalFuel;
    }

    public ulong RemainingFuel => _remainingFuel;

    // A resumed carrier only gets a fresh fairness slice, not new fuel.
    public void BeginDispatch() => _ticks = 0;

    // Hot path: no clock, timer, task await, allocation or scheduler callback.
    // Must be emitted after committing loop-carried state. Returning true only
    // authorizes a slow poll, never suspension under critical/borrow guards.
    public bool ChargeBackedge(ulong weight = 1)
    {
        ChargeOperation(weight);
        _ticks = (_ticks + 1) & (PollInterval - 1U);
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
