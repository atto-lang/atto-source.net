namespace Ores.Net.Runtime;

/// <summary>
/// A process-local aggregate quota for actor-owned native bytes. One authority
/// is shared by all actor heaps admitted by the same trusted runtime domain.
/// The reservation is made BEFORE native memory allocation and returned only
/// after actual physical reclamation, not when actor retirement is requested.
/// </summary>
/// <remarks>
/// A quota controls resource exhaustion; it is not an OS memory protection
/// boundary and does not account for unrelated runtime/library allocations.
/// Host admission must keep this instance private rather than accepting an
/// arbitrary guest-created budget.
/// </remarks>
public sealed class ActorNativeMemoryBudget
{
    private readonly object _gate = new();
    private readonly long _limit;
    private long _reserved;

    public ActorNativeMemoryBudget(long limitBytes)
    {
        if (limitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limitBytes));
        _limit = limitBytes;
    }

    public long LimitBytes => _limit;
    public long ReservedBytes
    {
        get { lock (_gate) return _reserved; }
    }

    /// <summary>Fail without changing accounting on quota exhaustion.</summary>
    internal bool TryReserve(int bytes)
    {
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            if (bytes > _limit - _reserved) return false;
            _reserved += bytes;
            return true;
        }
    }

    /// <summary>Only the owning heap releases a successful reservation.</summary>
    internal void Release(int bytes)
    {
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            if (bytes > _reserved)
                throw new InvalidOperationException("native actor heap quota refund underflow");
            _reserved -= bytes;
        }
    }
}
