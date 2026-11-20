namespace Atto.Net.ActorRuntime;

/// <summary>
/// Trusted host resource owner. This C# object is NOT an in-process sandbox
/// for arbitrary managed or native code. Untrusted guest admission remains
/// denied pending an independently verified TenantVM execution provider.
/// </summary>
public sealed class TenantVm : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<ActorArena> _actors = new();
    private readonly int _maximumActors;
    private readonly int _maximumNativeBytes;
    private int _nativeBytes;
    private ulong _remainingLogicalFuel;
    private bool _closed;

    public TenantVm(
        int maximumActors = 64,
        int maximumNativeBytes = 16 * 1024 * 1024,
        ulong maximumTotalLogicalFuel = GuestCooperationBudget.MaximumLogicalFuel)
    {
        if (maximumActors is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maximumActors));
        if (maximumNativeBytes is < 64 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumNativeBytes));
        if (maximumTotalLogicalFuel is 0 or > GuestCooperationBudget.MaximumLogicalFuel)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalLogicalFuel));
        _maximumActors = maximumActors;
        _maximumNativeBytes = maximumNativeBytes;
        _remainingLogicalFuel = maximumTotalLogicalFuel;
    }

    public int NativeReservedBytes { get { lock (_gate) return _nativeBytes; } }
    public int LiveActorCount { get { lock (_gate) return _actors.Count; } }
    public ulong RemainingLogicalFuel { get { lock (_gate) return _remainingLogicalFuel; } }

    // Charge irreversibly against the tenant lifetime, not actor lifetime.
    // All calls originate from an authenticated physical actor turn. Never
    // take an actor gate from inside this tenant gate.
    internal bool TryChargeLogicalFuel(ulong weight)
    {
        lock (_gate)
        {
            if (_closed || weight > _remainingLogicalFuel) return false;
            _remainingLogicalFuel -= weight;
            return true;
        }
    }

    public ActorArena CreateActor(
        int arenaBytes = 64 * 1024, int maximumFrames = 256,
        ulong logicalFuel = 1_000_000)
    {
        if (arenaBytes is < 64 or > ActorArena.MaximumArenaBytes)
            throw new ArgumentOutOfRangeException(nameof(arenaBytes));
        if (maximumFrames is < 1 or > ActorArena.MaximumFrames)
            throw new ArgumentOutOfRangeException(nameof(maximumFrames));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_actors.Count >= _maximumActors ||
                arenaBytes > _maximumNativeBytes - _nativeBytes)
                throw new ActorResourceExhaustedException("Tenant actor/native-memory quota exhausted");

            // Construction can fail before any budget or actor is published.
            var actor = new ActorArena(this, arenaBytes, maximumFrames, logicalFuel);
            try
            {
                // Publication is transactional: if managed registry allocation
                // fails, native memory must not escape without a registered
                // owner and corresponding tenant reservation.
                if (!_actors.Add(actor))
                    throw new InvalidOperationException("Duplicate actor admission");
            }
            catch
            {
                actor.DiscardUnpublishedAllocation();
                throw;
            }
            _nativeBytes += arenaBytes;
            return actor;
        }
    }

    internal void ReleaseActor(ActorArena actor, int reservedBytes)
    {
        lock (_gate)
        {
            if (!_actors.Remove(actor))
                throw new InvalidOperationException("Duplicate actor arena refund");
            _nativeBytes -= reservedBytes;
        }
    }

    public void Dispose()
    {
        ActorArena[] snapshot;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            snapshot = _actors.ToArray();
        }
        // Never hold the tenant lock while taking actor locks. In-flight
        // actors retain their reservation until their physical turn exits.
        foreach (var actor in snapshot) actor.Stop();
    }
}
