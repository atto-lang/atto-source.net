namespace Ores.Net.Runtime;

/// <summary>Same-thread cooperation for a standalone inline task with no guest scheduler carrier.</summary>
public static class RootInlineCooperation
{
    // Offer the current OS time slice and return on the same thread. A guest
    // scheduler may later replace this root-only checkpoint with a same-carrier
    // handoff once its reentrancy and continuation rules are verified.
    public static bool Yield() => Thread.Yield();
}
