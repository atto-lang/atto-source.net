namespace Ores.Net.Compiler;

/// <summary>
/// Compiler-owned, non-emitting readonly i64 shape projection witness.
/// This is NOT a CIL/NativeAOT guest-call ABI or permission to cast nominal
/// object pointers. Inputs must originate from verified type/layout metadata.
/// </summary>
public static class ReadonlyShapeProjectionV1
{
    public const int MaxFields = 32;
    public enum Origin { Class, Record, Struct, Map, Actor }
    public enum Scalar { I64, Bool, String }

    public sealed record Field(string Name, Scalar Type, int Slot,
        bool IsPublic, bool Immutable, bool EffectFree, bool SealedActorState);
    public sealed record RequiredField(string Name, Scalar Type);
    public sealed record Source(Origin Kind, int NominalId, long LayoutEpoch,
        long LoanId, bool SealedActorState, IReadOnlyList<Field> Fields);
    public sealed record FieldWitness(string Name, Scalar Type, int SourceSlot);
    public sealed record Plan(int SourceNominalId, long LayoutEpoch,
        long LoanId, IReadOnlyList<FieldWitness> Fields)
    {
        public FieldWitness GetField(string name) =>
            Fields.FirstOrDefault(f => f.Name == name)
                ?? throw new InvalidOperationException(
                    "field not in proven readonly projection: " + name);
    }

    public static Plan Prove(Source source, IReadOnlyList<RequiredField> required)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(source.Fields);
        if (source.Kind is not (Origin.Class or Origin.Record or Origin.Struct))
            throw new InvalidOperationException(
                "dynamic map/actor source has no proven immutable projection ABI");
        if (source.SealedActorState)
            throw new InvalidOperationException("cannot project sealed actor state");
        if (source.NominalId <= 0 || source.LayoutEpoch <= 0 || source.LoanId <= 0)
            throw new InvalidOperationException("source identity/layout/read loan not proven");
        if (required.Count == 0 || required.Count > MaxFields ||
            source.Fields.Count > MaxFields)
            throw new InvalidOperationException("empty or oversized shape");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var slots = new HashSet<int>();
        foreach (var f in source.Fields)
        {
            if (f is null || string.IsNullOrEmpty(f.Name) || !names.Add(f.Name))
                throw new InvalidOperationException("duplicate or invalid source field");
            if (f.Slot < 0 || !slots.Add(f.Slot))
                throw new InvalidOperationException("overlapping or invalid source offsets");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var witness = new List<FieldWitness>();
        foreach (var expected in required)
        {
            if (expected is null || string.IsNullOrEmpty(expected.Name) ||
                !seen.Add(expected.Name))
                throw new InvalidOperationException("duplicate or invalid required field");
            var found = source.Fields.FirstOrDefault(f => f.Name == expected.Name)
                ?? throw new InvalidOperationException("missing source field: " + expected.Name);
            if (found.SealedActorState)
                throw new InvalidOperationException("sealed actor field cannot be projected");
            if (expected.Type != Scalar.I64 || found.Type != Scalar.I64 ||
                !found.IsPublic || !found.Immutable || !found.EffectFree ||
                found.Type != expected.Type)
                throw new InvalidOperationException(
                    "field not exact-type public immutable i64: " + expected.Name);
            witness.Add(new FieldWitness(found.Name, found.Type, found.Slot));
        }
        witness.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return new Plan(source.NominalId, source.LayoutEpoch, source.LoanId,
            witness.AsReadOnly());
    }
}
