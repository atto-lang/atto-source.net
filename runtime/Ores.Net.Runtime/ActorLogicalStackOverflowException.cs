namespace Ores.Net.Runtime;

/// <summary>
/// Which bounded, actor-owned logical stack quota rejected a frame.
/// This is a HOST runtime fault, not proof of guest Error/raise/panic lowering
/// and never indicates a CLR StackOverflowException has occurred.
/// </summary>
public enum ActorLogicalStackLimit
{
    FrameCount,
    ByteCapacity
}

/// <summary>
/// Typed, deterministic actor-local exhaustion. The actor dispatcher fail-stops
/// an unhandled instance and reclaims only that actor's native arena.
/// Do not catch the process-corrupting CLR StackOverflowException as a substitute.
/// Source-level compilation must map this fault to a terminal typed guest Error
/// and emit the independent supervisor CONTROL notice before certification.
/// </summary>
public sealed class ActorLogicalStackOverflowException : InvalidOperationException
{
    public ActorLogicalStackLimit Limit { get; }

    internal ActorLogicalStackOverflowException(ActorLogicalStackLimit limit)
        : base(limit switch
        {
            ActorLogicalStackLimit.FrameCount =>
                "Actor logical frame-count limit exhausted",
            ActorLogicalStackLimit.ByteCapacity =>
                "Actor logical stack byte capacity exhausted",
            _ => "Actor logical stack quota exhausted"
        })
    {
        Limit = limit;
    }
}
