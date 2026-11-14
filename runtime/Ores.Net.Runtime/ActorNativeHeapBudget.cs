namespace Ores.Net.Runtime;

/// <summary>
/// Capacity budget for the experimental ActorNativeHeap allocator only.
/// This does not include ActorArena/ActorNativeArena reservations, mailbox
/// queues or CLR-managed memory until the allocator models are unified.
/// </summary>
public sealed class ActorNativeHeapBudget
{
    public const long DefaultProcessLimitBytes = 1024L * 1024 * 1024;

    public static ActorNativeHeapBudget Process { get; } =
        new(DefaultProcessLimitBytes);

    private readonly object _gate = new();
    private long _reservedBytes;

    public ActorNativeHeapBudget(long limitBytes)
    {
        if (limitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limitBytes));
        LimitBytes = limitBytes;
    }

    public long LimitBytes { get; }

    public long ReservedBytes
    {
        get { lock (_gate) return _reservedBytes; }
    }

    internal void Reserve(int bytes)
    {
        if (bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            if (_reservedBytes > LimitBytes - bytes)
                throw new InvalidOperationException(
                    "Actor-native heap reservation budget exhausted");
            _reservedBytes += bytes;
        }
    }

    internal void Refund(int bytes)
    {
        if (bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            if (_reservedBytes < bytes)
                throw new InvalidOperationException(
                    "Actor-native heap reservation refund underflow");
            _reservedBytes -= bytes;
        }
    }
}
