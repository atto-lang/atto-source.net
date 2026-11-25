namespace Ores.Net.Runtime;

public sealed partial class OresVmHost
{
    public enum ChildTerminalKind { Stopped, Failed, Cancelled }

    // Value-only, generation-bearing notice. No child state or guest references.
    public readonly record struct ChildTerminalNotice(ulong ChildId, ulong ChildEpoch,
        ChildTerminalKind Kind);

    private const int SupervisorControlLimit = 64;

    internal sealed class TerminalControlSlot
    {
        internal TerminalControlSlot(ActorTicket parent) { Parent = parent; }
        internal ActorTicket Parent { get; }
        internal ChildTerminalNotice? Notice { get; set; }
    }

    /// <summary>
    /// Trusted host-only child admission. Reserve one independent terminal
    /// CONTROL slot before admitting the child. Never shares DATA capacity.
    /// The exact parent incarnation owns settlement; guest code must not
    /// receive this host authority.
    /// </summary>
    public ActorTicket RegisterSupervisedActor(IsolateHandle handle, ActorTicket parent,
        ulong id, bool serial = false)
    {
        lock (_gate) {
            var s = Check(handle); CheckActor(s, parent);
            if (parent.TerminalControls.Count >= SupervisorControlLimit)
                throw new InvalidOperationException("supervisor terminal CONTROL capacity exhausted");
            var slot = new TerminalControlSlot(parent);
            // Ensure no growth is needed when a child terminates.
            parent.TerminalControls.EnsureCapacity(SupervisorControlLimit);
            var child = RegisterActor(handle, id, serial);
            child.ParentTerminalSlot = slot;
            parent.TerminalControls.Add(slot);
            return child;
        }
    }

    /// <summary>
    /// Direct-parent, exactly-once consumption. Pending slots are skipped;
    /// completed slots are removed and capacity is restored. This host ledger
    /// does not yet schedule CONTROL priority relative to DATA receives.
    /// </summary>
    public bool TryTakeChildTerminalControl(IsolateHandle handle, ActorTicket parent,
        out ChildTerminalNotice notice)
    {
        lock (_gate) {
            var s = Check(handle); CheckActor(s, parent);
            for (int i = 0; i < parent.TerminalControls.Count; ++i) {
                if (parent.TerminalControls[i].Notice is not { } ready) continue;
                notice = ready;
                parent.TerminalControls.RemoveAt(i);
                return true;
            }
            notice = default;
            return false;
        }
    }
}
