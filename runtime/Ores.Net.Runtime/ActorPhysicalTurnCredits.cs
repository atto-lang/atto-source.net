using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>
/// Host-only physical actor turn credits. It does NOT enable concurrent
/// execution of Oreslang source actors, protect actor mutable state, or
/// implement a guest I/O runtime. Contended admission returns null immediately.
/// </summary>
public sealed class ActorPhysicalTurnCredits
{
    public const int DefaultMaximum = 1;
    public const int SupportedMaximum = 3;
    private const int ClosedBit = 4;
    private const int ActiveMask = 3;
    private int _state;

    public ActorPhysicalTurnCredits(int maximum = DefaultMaximum)
    {
        if (maximum is < 1 or > SupportedMaximum)
            throw new ArgumentOutOfRangeException(nameof(maximum),
                "physical actor parallelism must be between 1 and 3");
        Maximum = maximum;
    }

    public int Maximum { get; }
    public int Active => Volatile.Read(ref _state) & ActiveMask;
    public bool IsClosed => (Volatile.Read(ref _state) & ClosedBit) != 0;

    public Permit? TryAcquire()
    {
        int before = Volatile.Read(ref _state);
        if ((before & ClosedBit) != 0 || (before & ActiveMask) >= Maximum)
            return null;
        // One CAS attempt; no spin, wait, monitor or thread provisioning.
        return Interlocked.CompareExchange(ref _state, before + 1, before) == before
            ? new Permit(this) : null;
    }

    public void Close() => Interlocked.Or(ref _state, ClosedBit);

    private void Release()
    {
        // Each Permit invokes this at most once; preserve the independent
        // closed bit and never reopen a retired actor during release.
        Interlocked.Decrement(ref _state);
    }

    public sealed class Permit : IDisposable
    {
        private ActorPhysicalTurnCredits? _owner;
        internal Permit(ActorPhysicalTurnCredits owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
