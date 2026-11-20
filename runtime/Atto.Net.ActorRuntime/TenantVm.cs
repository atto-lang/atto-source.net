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
    private bool _closed;

    public TenantVm(int maximumActors = 64, int maximumNativeBytes = 16 * 1024 * 1024)
    {
        if (maximumActors is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maximumActors));
        if (maximumNativeBytes is < 64 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumNativeBytes));
        _maximumActors = maximumActors;
        _maximumNativeBytes = maximumNativeBytes;
    }

    public int NativeReservedBytes { get { lock (_gate) return _nativeBytes; } }
    public int LiveActorCount { get { lock (_gate) return _actors.Count; } }

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
            _actors.Add(actor);
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
