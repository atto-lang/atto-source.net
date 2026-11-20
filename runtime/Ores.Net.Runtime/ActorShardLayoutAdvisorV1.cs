namespace Ores.Net.Runtime;

/// <summary>
/// Trusted-host hints for the next actor-owned shard allocation. This is a
/// bounded, allocation-free-on-observation NativeAOT policy, not a JIT, a
/// guest-visible profiler, or a migration of an already-live shard.
/// Every access requires the exact live owner turn issued by ActorArena.
/// A serial actor turn owns the advisor; concurrent writers are forbidden.
/// </summary>
internal enum ActorShardAccessKindV1 { RowScan = 1, ColumnScan = 2, RandomLookup = 3 }

internal readonly record struct ActorShardLayoutChoiceV1(
    ActorDataLayout Layout, int Lanes, int Samples);

internal sealed class ActorShardLayoutAdvisorV1
{
    private readonly ActorArena arena;
    private readonly int fields;
    private readonly int sampleLimit;
    private int samples;
    private int rowSamples;
    private int columnSamples;
    private int randomSamples;
    private bool retired;

    internal ActorShardLayoutAdvisorV1(ActorArena arena, ActorArenaTurn turn,
        int fields, int sampleLimit = 4096)
    {
        ArgumentNullException.ThrowIfNull(arena);
        arena.ValidateOwnerTurn(turn);
        if (fields is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(fields));
        if (sampleLimit is < 64 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(sampleLimit));
        this.arena = arena;
        this.fields = fields;
        this.sampleLimit = sampleLimit;
    }

    internal int SamplesFor(ActorArenaTurn turn)
    {
        RequireLiveOwner(turn);
        return samples;
    }

    private void RequireLiveOwner(ActorArenaTurn turn)
    {
        if (retired)
            throw new InvalidOperationException("retired actor layout advisor");
        // Validate the real issued capability against arena identity, epoch
        // and carrier, including after a suspend/resume on a new actor turn.
        arena.ValidateOwnerTurn(turn);
    }

    /// <summary>
    /// Saturating trace collection. Each call requires an admitted actor turn
    /// and represents one observation or a bounded batch of identical observations.
    /// No heap objects, clock reads, callbacks, or dynamic code are created.
    /// </summary>
    internal bool Observe(ActorArenaTurn turn,
        ActorShardAccessKindV1 access, int count = 1)
    {
        RequireLiveOwner(turn);
        if (access is not (ActorShardAccessKindV1.RowScan or
            ActorShardAccessKindV1.ColumnScan or ActorShardAccessKindV1.RandomLookup))
            throw new ArgumentOutOfRangeException(nameof(access));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        int admitted = Math.Min(count, sampleLimit - samples);
        if (admitted == 0) return false;
        switch (access)
        {
            case ActorShardAccessKindV1.RowScan: rowSamples += admitted; break;
            case ActorShardAccessKindV1.ColumnScan: columnSamples += admitted; break;
            case ActorShardAccessKindV1.RandomLookup: randomSamples += admitted; break;
        }
        samples += admitted;
        return true;
    }

    /// <summary>
    /// Choose exactly one physical layout for a FUTURE shard allocation. An
    /// existing ActorShardColumns instance must never be reinterpreted or
    /// mutated in place because outstanding handles/loans retain its layout.
    /// These integer heuristics are deterministic, not performance guarantees.
    /// </summary>
    internal ActorShardLayoutChoiceV1 Recommend(ActorArenaTurn turn,
        int rows, int preferredLanes = 8)
    {
        RequireLiveOwner(turn);
        if (rows < 1) throw new ArgumentOutOfRangeException(nameof(rows));
        if (preferredLanes is < 1 or > ActorShardColumns.MaxAosoaLanes ||
            (preferredLanes & (preferredLanes - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(preferredLanes));
        long logicalBytes = (long)rows * fields * sizeof(double);
        if (logicalBytes > ActorShardColumns.MaxShardBytes)
            throw new ArgumentOutOfRangeException(nameof(rows),
                "profiled shard would exceed its actor-owned 16 MiB quota");

        // Cold profiles and random-heavy workloads keep the simple AoS ABI.
        // Require a meaningful, two-to-one nonrandom majority before choosing.
        if (samples >= 64 && columnSamples >= 64 &&
            (long)columnSamples >= 2L * rowSamples &&
            (long)columnSamples >= 2L * randomSamples)
            return new ActorShardLayoutChoiceV1(ActorDataLayout.Soa, 1, samples);

        long paddedRows = ((long)rows + preferredLanes - 1) /
            preferredLanes * preferredLanes;
        if (samples >= 64 && rowSamples >= 64 && fields >= 2 &&
            preferredLanes >= 2 && (long)rows >= 2L * preferredLanes &&
            (long)rowSamples >= 2L * columnSamples &&
            (long)rowSamples >= 2L * randomSamples &&
            paddedRows * fields * sizeof(double) <= ActorShardColumns.MaxShardBytes)
            return new ActorShardLayoutChoiceV1(
                ActorDataLayout.Aosoa, preferredLanes, samples);

        return new ActorShardLayoutChoiceV1(ActorDataLayout.Aos, 1, samples);
    }

    /// <summary>Revoke generation and clear all trace counters at retirement.</summary>
    internal void Retire(ActorArenaTurn turn)
    {
        RequireLiveOwner(turn);
        retired = true;
        samples = rowSamples = columnSamples = randomSamples = 0;
    }
}
