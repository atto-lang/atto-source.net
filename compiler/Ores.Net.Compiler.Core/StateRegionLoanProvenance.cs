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
            loanId == 0)
            throw new ArgumentOutOfRangeException(
                nameof(regionId),
                "state-region provenance identity components must be nonzero");
        if (string.IsNullOrWhiteSpace(place))
            throw new ArgumentException(
                "state-region provenance requires a nonblank place", nameof(place));

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

    public bool AuthorizesRelease(in StateRegionLoanProvenance release) =>
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
        if (Action != LoanAction.Borrow)
            throw new InvalidOperationException(
                "only a borrow provenance record can derive a release record");
        return new StateRegionLoanProvenance(
            RegionId, RegionGeneration, RuntimeDomain, ExecutionId,
            ExecutionSlot, ExecutionGeneration, LoanId, Place, Kind,
            LoanAction.Release);
    }
}
