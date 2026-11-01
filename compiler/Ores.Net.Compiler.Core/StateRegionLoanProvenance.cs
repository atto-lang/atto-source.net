namespace Ores.Net.Compiler;

/// <summary>
/// Concrete runtime/materialized provenance for one state-region loan.
///
/// Compiler IR must use StateRegionLoanRequirement instead of fabricating
/// scheduler IDs or generations. Trusted lowering/runtime materializes this
/// value from the live region and authenticated logical execution. This is a
/// conformance/receipt value, not a guest-visible capability.
/// </summary>
public readonly record struct StateRegionLoanProvenance
{
    // The canonical v1 JSON/WASM identity domain is IEEE-754 exact integer.
    // Native ulong scheduler IDs outside this range must NOT be serialized as v1.
    public const ulong MaxJsonSafeIdentity = 9_007_199_254_740_991UL;

    public enum AccessKind { Read, Write }
    public enum LoanAction { Borrow, Release }

    public ulong RegionId { get; }
    public ulong RegionGeneration { get; }
    public ulong RuntimeDomain { get; }
    public ulong ExecutionId { get; }
    public ulong ExecutionSlot { get; }
    public ulong ExecutionGeneration { get; }
    public ulong LoanId { get; }
    public string Place { get; }
    public AccessKind Kind { get; }
    public LoanAction Action { get; }

    public StateRegionLoanProvenance(
        ulong regionId,
        ulong regionGeneration,
        ulong runtimeDomain,
        ulong executionId,
        ulong executionSlot,
        ulong executionGeneration,
        ulong loanId,
        string place,
        AccessKind kind,
        LoanAction action)
    {
        if (regionId == 0 || regionGeneration == 0 ||
            runtimeDomain == 0 || executionId == 0 ||
            executionSlot == 0 || executionGeneration == 0 ||
            loanId == 0 ||
            regionId > MaxJsonSafeIdentity ||
            regionGeneration > MaxJsonSafeIdentity ||
            runtimeDomain > MaxJsonSafeIdentity ||
            executionId > MaxJsonSafeIdentity ||
            executionSlot > MaxJsonSafeIdentity ||
            executionGeneration > MaxJsonSafeIdentity ||
            loanId > MaxJsonSafeIdentity)
            throw new ArgumentOutOfRangeException(
                nameof(regionId),
                "state-region provenance identity components must be nonzero");
        if (string.IsNullOrWhiteSpace(place))
            throw new ArgumentException(
                "state-region provenance requires a nonblank place", nameof(place));

        if (kind != AccessKind.Read && kind != AccessKind.Write)
            throw new ArgumentOutOfRangeException(nameof(kind),
                "invalid state-region access kind");
        if (action != LoanAction.Borrow && action != LoanAction.Release)
            throw new ArgumentOutOfRangeException(nameof(action),
                "invalid state-region loan action");

        RegionId = regionId;
        RegionGeneration = regionGeneration;
        RuntimeDomain = runtimeDomain;
        ExecutionId = executionId;
        ExecutionSlot = executionSlot;
        ExecutionGeneration = executionGeneration;
        LoanId = loanId;
        Place = place;
        Kind = kind;
        Action = action;
    }

    // Value types permit default initialization; a default or forged enum must
    // never acquire authority merely because two field values compare equal.
    private bool IsValid =>
        RegionId != 0 && RegionGeneration != 0 && RuntimeDomain != 0 &&
        ExecutionId != 0 && ExecutionSlot != 0 && ExecutionGeneration != 0 &&
        LoanId != 0 &&
        RegionId <= MaxJsonSafeIdentity && RegionGeneration <= MaxJsonSafeIdentity &&
        RuntimeDomain <= MaxJsonSafeIdentity && ExecutionId <= MaxJsonSafeIdentity &&
        ExecutionSlot <= MaxJsonSafeIdentity && ExecutionGeneration <= MaxJsonSafeIdentity &&
        LoanId <= MaxJsonSafeIdentity &&
        !string.IsNullOrWhiteSpace(Place) &&
        (Kind == AccessKind.Read || Kind == AccessKind.Write) &&
        (Action == LoanAction.Borrow || Action == LoanAction.Release);

    public bool AuthorizesRelease(in StateRegionLoanProvenance release) =>
        IsValid && release.IsValid &&
        Action == LoanAction.Borrow &&
        release.Action == LoanAction.Release &&
        RegionId == release.RegionId &&
        RegionGeneration == release.RegionGeneration &&
        RuntimeDomain == release.RuntimeDomain &&
        ExecutionId == release.ExecutionId &&
        ExecutionSlot == release.ExecutionSlot &&
        ExecutionGeneration == release.ExecutionGeneration &&
        LoanId == release.LoanId &&
        StringComparer.Ordinal.Equals(Place, release.Place) &&
        Kind == release.Kind;

    public StateRegionLoanProvenance ReleaseRecord()
    {
        if (!IsValid || Action != LoanAction.Borrow)
            throw new InvalidOperationException(
                "only a borrow provenance record can derive a release record");
        return new StateRegionLoanProvenance(
            RegionId, RegionGeneration, RuntimeDomain, ExecutionId,
            ExecutionSlot, ExecutionGeneration, LoanId, Place, Kind,
            LoanAction.Release);
    }
}
