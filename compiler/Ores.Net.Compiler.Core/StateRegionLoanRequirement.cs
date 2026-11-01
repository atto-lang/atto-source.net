namespace Ores.Net.Compiler;

/// <summary>
/// Symbolic compiler requirement for a state-region loan.
/// Concrete runtime execution and region generation IDs are intentionally absent.
/// </summary>
public readonly record struct StateRegionLoanRequirement
{
    public enum ExecutionBinding
    {
        CurrentLogicalExecution
    }

    public string RegionBinding { get; }
    public ExecutionBinding Execution { get; }
    public bool GenerationFence { get; }
    public ulong LoanId { get; }
    public string Place { get; }
    public StateRegionLoanProvenance.AccessKind Kind { get; }
    public StateRegionLoanProvenance.LoanAction Action { get; }

    public StateRegionLoanRequirement(
        string regionBinding,
        ExecutionBinding execution,
        bool generationFence,
        ulong loanId,
        string place,
        StateRegionLoanProvenance.AccessKind kind,
        StateRegionLoanProvenance.LoanAction action)
    {
        if (string.IsNullOrWhiteSpace(regionBinding))
            throw new ArgumentException(
                "state-region requirement needs a region binding", nameof(regionBinding));
        if (execution != ExecutionBinding.CurrentLogicalExecution)
            throw new ArgumentOutOfRangeException(
                nameof(execution), "only current logical execution may be bound");
        if (!generationFence)
            throw new ArgumentException(
                "state-region requirement must be generation fenced",
                nameof(generationFence));
        if (loanId == 0)
            throw new ArgumentOutOfRangeException(nameof(loanId));
        if (string.IsNullOrWhiteSpace(place))
            throw new ArgumentException(
                "state-region requirement needs a place", nameof(place));

        if (kind != StateRegionLoanProvenance.AccessKind.Read &&
            kind != StateRegionLoanProvenance.AccessKind.Write)
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (action != StateRegionLoanProvenance.LoanAction.Borrow &&
            action != StateRegionLoanProvenance.LoanAction.Release)
            throw new ArgumentOutOfRangeException(nameof(action));

        RegionBinding = regionBinding;
        Execution = execution;
        GenerationFence = generationFence;
        LoanId = loanId;
        Place = place;
        Kind = kind;
        Action = action;
    }

    private bool IsValid =>
        !string.IsNullOrWhiteSpace(RegionBinding) &&
        Execution == ExecutionBinding.CurrentLogicalExecution &&
        GenerationFence && LoanId != 0 &&
        !string.IsNullOrWhiteSpace(Place) &&
        (Kind == StateRegionLoanProvenance.AccessKind.Read ||
         Kind == StateRegionLoanProvenance.AccessKind.Write) &&
        (Action == StateRegionLoanProvenance.LoanAction.Borrow ||
         Action == StateRegionLoanProvenance.LoanAction.Release);

    public bool MatchesRelease(in StateRegionLoanRequirement release) =>
        IsValid && release.IsValid &&
        Action == StateRegionLoanProvenance.LoanAction.Borrow &&
        release.Action == StateRegionLoanProvenance.LoanAction.Release &&
        StringComparer.Ordinal.Equals(RegionBinding, release.RegionBinding) &&
        Execution == release.Execution &&
        GenerationFence == release.GenerationFence &&
        LoanId == release.LoanId &&
        StringComparer.Ordinal.Equals(Place, release.Place) &&
        Kind == release.Kind;

    public StateRegionLoanRequirement ReleaseRequirement()
    {
        if (!IsValid || Action != StateRegionLoanProvenance.LoanAction.Borrow)
            throw new InvalidOperationException(
                "only a borrow requirement can derive a release requirement");
        return new StateRegionLoanRequirement(
            RegionBinding, Execution, GenerationFence, LoanId, Place, Kind,
            StateRegionLoanProvenance.LoanAction.Release);
    }
}
