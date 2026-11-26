using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>
/// Internal NativeAOT-safe supervisor policy, matching the Java tracing mask.
/// This is NOT a JIT compiler, CIL rewriter, Truffle instrumenter or OTLP exporter.
/// Never expose this class through guest/FFI capabilities.
/// </summary>
internal sealed class NativeTraceInstrumentationV1
{
    internal const int Boundary = 1;
    internal const int Call = 2;
    internal const int Diagnostic = 4;
    internal const int All = Boundary | Call | Diagnostic;

    internal enum BuildMode { None, Boundaries, Calls, Full, Adaptive }

    private readonly object gate = new();
    private readonly int permitted;
    private int activeMask;
    private long version;

    internal NativeTraceInstrumentationV1(BuildMode build)
    {
        Build = build;
        permitted = build switch
        {
            BuildMode.None => 0,
            BuildMode.Boundaries => Boundary,
            BuildMode.Calls => Boundary | Call,
            BuildMode.Full or BuildMode.Adaptive => All,
            _ => throw new ArgumentOutOfRangeException(nameof(build))
        };
    }

    internal BuildMode Build { get; }
    internal long Version { get { lock (gate) return version; } }
    internal int ActiveMask => Volatile.Read(ref activeMask);
    // A single locked observation prevents policy-version/mask tearing across
    // an OFF->ON transition. Callers must not compose two separate reads.
    internal (long Version, int Mask) Snapshot()
    {
        lock (gate) return (version, activeMask);
    }
    internal bool CanRecompile => Build == BuildMode.Adaptive;

    // No allocation, clock, dynamic call or I/O on the disabled probe path.
    internal bool Enabled(int kind) =>
        (kind == Boundary || kind == Call || kind == Diagnostic) &&
        (Volatile.Read(ref activeMask) & kind) != 0;

    internal bool HasStaticSite(int kind) =>
        Build != BuildMode.Adaptive &&
        (kind == Boundary || kind == Call || kind == Diagnostic) &&
        (permitted & kind) != 0;

    internal long Configure(int next, bool trustedHost)
    {
        if (!trustedHost) throw new UnauthorizedAccessException("host telemetry authority required");
        lock (gate)
        {
            if (next < 0 || (next & ~permitted) != 0)
                throw new ArgumentOutOfRangeException(nameof(next), "outside immutable AOT build ceiling");
            if (version == long.MaxValue)
                throw new InvalidOperationException("telemetry epoch exhausted");
            ++version;
            Volatile.Write(ref activeMask, next);
            return version;
        }
    }
}

/// <summary>
/// A pinned immutable function identity: all hashes are lowercase SHA-256, and
/// every candidate must preserve effects/ABI/ownership/continuation identity.
/// This is metadata validation, NOT an authenticated compiler signature.
/// </summary>
internal sealed record NativeTraceCodeIdentityV1(
    string Tenant, string Symbol, string TypedIrSha256, string AbiSha256,
    string EffectsSha256, string OwnershipSha256, string ContinuationSha256)
{
    private static bool Sha256(string? s) =>
        s is { Length: 64 } && s.All(c =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    internal bool Valid() =>
        !string.IsNullOrWhiteSpace(Tenant) && !string.IsNullOrWhiteSpace(Symbol) &&
        Sha256(TypedIrSha256) && Sha256(AbiSha256) && Sha256(EffectsSha256) &&
        Sha256(OwnershipSha256) && Sha256(ContinuationSha256);
}

/// <summary>
/// NativeAOT-safe publication of externally VERIFIED native code variants.
/// Does not emit new IL, load a CLR assembly or compile JIT code. A separate
/// trusted native backend must supply executable delegates and verify signatures.
/// A lease pins the initial variant over logical await/suspend boundaries.
/// </summary>
internal sealed class NativeTraceVersionedDispatchV1<T> where T : class
{
    internal enum PublishResult
    {
        Accepted, Unauthorized, Unsupported, Incompatible, StaleGeneration,
        InvalidVariant, ResourceLimit, Retired
    }

    internal sealed record Candidate(
        NativeTraceCodeIdentityV1 Identity, ulong Generation,
        int ProbeMask, T Executable, bool IsPrecompiledAot);

    internal sealed class Generation
    {
        internal readonly ulong Id;
        internal readonly T Target;
        internal readonly int ProbeMask;
        // A prior publication cannot become executable merely because an
        // administrator later re-enables the same mask after revocation.
        internal readonly long PolicyVersion;
        internal int Pins;
        internal Generation(ulong id, T target, int probeMask = 0, long policyVersion = 0)
        { Id = id; Target = target; ProbeMask = probeMask; PolicyVersion = policyVersion; }
    }

    internal sealed class Lease : IDisposable
    {
        private NativeTraceVersionedDispatchV1<T>? owner;
        private Generation? version;
        internal Lease(NativeTraceVersionedDispatchV1<T> owner, Generation version)
        { this.owner = owner; this.version = version; }

        internal T Executable => version?.Target ??
            throw new ObjectDisposedException(nameof(Lease));
        internal ulong Generation => version?.Id ??
            throw new ObjectDisposedException(nameof(Lease));

        // Explicit continuation ownership: do NOT dispose on await/suspend.
        public void Dispose()
        {
            var formerOwner = Interlocked.Exchange(ref owner, null);
            if (formerOwner is not null)
            {
                var old = Interlocked.Exchange(ref version, null)!;
                formerOwner.Release(old);
            }
        }
    }

    private readonly object gate = new();
    private readonly NativeTraceInstrumentationV1 policy;
    private readonly NativeTraceCodeIdentityV1 identity;
    private readonly ulong supervisorEpoch;
    private readonly Generation baseline;
    private readonly bool hasPrecompiledAotProbes;
    private readonly bool hasNativeJitProvider;
    private readonly bool hardRealtime;
    private readonly int maximumRetained;
    private readonly int publicationBudget;
    private readonly List<Generation> retained = new();
    private Generation active;
    private ulong highestGeneration;
    private int publications;
    private bool retired;

    internal NativeTraceVersionedDispatchV1(
        NativeTraceInstrumentationV1 policy, NativeTraceCodeIdentityV1 identity,
        ulong supervisorEpoch, T baselineExecutable, bool hasPrecompiledAotProbes,
        bool hasNativeJitProvider, bool hardRealtime, int maxLiveGenerations = 8,
        int maxPublications = 64)
    {
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ArgumentNullException.ThrowIfNull(baselineExecutable);
        if (!identity.Valid() || supervisorEpoch == 0 ||
            maxLiveGenerations is < 2 or > 256 || maxPublications is < 1 or > 4096)
            throw new ArgumentException("invalid authenticated code slot identity/budget");
        if (hasNativeJitProvider && !policy.CanRecompile)
            throw new ArgumentException("native JIT requires adaptive build ceiling");
        this.supervisorEpoch = supervisorEpoch;
        this.hasPrecompiledAotProbes = hasPrecompiledAotProbes;
        this.hasNativeJitProvider = hasNativeJitProvider;
        this.hardRealtime = hardRealtime;
        maximumRetained = maxLiveGenerations;
        publicationBudget = maxPublications;
        baseline = new Generation(1, baselineExecutable);
        active = baseline;
        highestGeneration = 1;
        retained.Add(baseline);
    }

    internal Lease Pin()
    {
        lock (gate)
        {
            if (retired) throw new InvalidOperationException("retired trace code slot");
            if (active.Pins == int.MaxValue) throw new InvalidOperationException("pin quota");
            // Policy can be revoked while a trusted compiler publishes outside
            // this lock. Never enter a variant whose probe authority is gone.
            var selected = active;
            var currentPolicy = policy.Snapshot();
            if (selected.ProbeMask != 0 &&
                (selected.PolicyVersion != currentPolicy.Version ||
                 (selected.ProbeMask & ~currentPolicy.Mask) != 0))
                selected = baseline;
            ++selected.Pins;
            return new Lease(this, selected);
        }
    }

    internal ulong ActiveGeneration { get { lock (gate) return active.Id; } }
    internal int RetainedGenerations { get { lock (gate) return retained.Count; } }
    internal int PublicationAttempts { get { lock (gate) return publications; } }

    internal PublishResult Publish(
        Candidate? candidate, bool deepJit, ulong epoch,
        bool tenantAuthorized, bool verifiedBuildBoundSignature)
    {
        // Trusted compiler + signature verification are external inputs. A
        // forged true from a guest must NEVER be able to reach this API.
        if (!tenantAuthorized || !verifiedBuildBoundSignature)
            return PublishResult.Unauthorized;
        lock (gate)
        {
            if (retired) return PublishResult.Retired;
            if (epoch != supervisorEpoch) return PublishResult.StaleGeneration;
            if (deepJit
                ? !hasNativeJitProvider || hardRealtime || !policy.CanRecompile
                : !hasPrecompiledAotProbes)
                return PublishResult.Unsupported;
            if (candidate is null || candidate.Executable is null ||
                !candidate.Identity.Valid() || candidate.ProbeMask == 0 ||
                (candidate.ProbeMask & ~NativeTraceInstrumentationV1.All) != 0)
                return PublishResult.InvalidVariant;
            if (candidate.Identity != identity)
                return PublishResult.Incompatible;
            if (deepJit == candidate.IsPrecompiledAot)
                return PublishResult.Unsupported;
            var currentPolicy = policy.Snapshot();
            if ((candidate.ProbeMask & ~currentPolicy.Mask) != 0)
                return PublishResult.Unauthorized;
            if (candidate.Generation <= highestGeneration || highestGeneration == ulong.MaxValue)
                return PublishResult.StaleGeneration;
            Prune();
            if (publications >= publicationBudget || retained.Count >= maximumRetained)
                return PublishResult.ResourceLimit;
            // Only future invocations see the new target; old pinned scopes
            // retain the old generation until their logical completion.
            var next = new Generation(candidate.Generation, candidate.Executable,
                candidate.ProbeMask, currentPolicy.Version);
            retained.Add(next);
            active = next;
            highestGeneration = next.Id;
            ++publications;
            Prune();
            return PublishResult.Accepted;
        }
    }

    internal void Disable()
    {
        lock (gate)
        {
            active = baseline;
            Prune();
        }
    }

    internal void Retire()
    {
        lock (gate)
        {
            retired = true;
            active = baseline;
            Prune();
        }
    }

    private void Release(Generation generation)
    {
        lock (gate)
        {
            if (generation.Pins <= 0) throw new InvalidOperationException("pin underflow");
            --generation.Pins;
            Prune();
        }
    }

    private void Prune() => retained.RemoveAll(v => v != active && v.Pins == 0 && v != baseline);
}
