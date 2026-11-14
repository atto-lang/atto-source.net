namespace Ores.Net.Runtime.Net;

/// <summary>
/// Trusted-host admission ledger for exclusively owned physical TCP slots.
/// This is NOT a socket connector, a guest API, a shared pointer or a
/// multiplexing implementation. Every operation attempts a bounded nonparking
/// monitor gate; the reactor must finish an in-flight operation before reuse.
/// </summary>
internal sealed class TcpPoolLeaseLedger
{
    internal readonly record struct ActorIdentity(ulong Id, ulong Incarnation);

    internal sealed class Lease
    {
        internal readonly TcpPoolLeaseLedger Owner;
        internal readonly int Slot;
        internal readonly ulong Generation;
        internal readonly ActorIdentity Actor;
        internal readonly ulong Epoch;
        internal readonly string Partition;
        internal bool Active;
        // Without operation-specific completion tickets, reusing one lease
        // for multiple physical I/O operations permits late-callback ABA.
        // Restrict v1 to a single exchange per lease.
        internal bool EverBegun;
        internal bool Revoked;

        internal Lease(TcpPoolLeaseLedger owner, int slot, ulong generation,
                       ActorIdentity actor, ulong epoch, string partition)
        {
            Owner = owner;
            Slot = slot;
            Generation = generation;
            Actor = actor;
            Epoch = epoch;
            Partition = partition;
        }
    }

    private sealed class SlotState
    {
        internal ulong Generation = 1;
        internal Lease? Current;
        internal bool Quarantine;
    }

    private readonly object gate = new();
    private readonly SlotState[] slots;
    private readonly string partition;
    private ulong policyEpoch;
    private bool stopped;

    internal TcpPoolLeaseLedger(int capacity, string partition, ulong policyEpoch)
    {
        if (capacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (string.IsNullOrWhiteSpace(partition) || partition != partition.Trim())
            throw new ArgumentException("canonical partition required", nameof(partition));
        if (policyEpoch == 0) throw new ArgumentOutOfRangeException(nameof(policyEpoch));
        this.partition = partition;
        this.policyEpoch = policyEpoch;
        slots = Enumerable.Range(0, capacity).Select(_ => new SlotState()).ToArray();
    }

    internal bool TryAcquire(int slot, ActorIdentity actor, string requestedPartition,
                             ulong epoch, out Lease? lease)
    {
        lease = null;
        if (actor.Id == 0 || actor.Incarnation == 0) return false;
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            if (stopped || epoch != policyEpoch || requestedPartition != partition ||
                (uint)slot >= (uint)slots.Length) return false;
            var state = slots[slot];
            if (state.Generation == ulong.MaxValue || state.Current is not null ||
                state.Quarantine)
                return false;
            lease = new Lease(this, slot, state.Generation, actor, epoch, partition);
            state.Current = lease;
            return true;
        }
        finally { Monitor.Exit(gate); }
    }

    internal bool TryBegin(Lease? lease, ActorIdentity actor, ulong epoch)
    {
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            if (stopped || !Valid(lease, actor, epoch) || lease!.EverBegun) return false;
            lease.EverBegun = true;
            lease.Active = true;
            return true;
        }
        finally { Monitor.Exit(gate); }
    }

    // Trusted physical reactor completion; actor identity must not be required
    // once a cancellation or actor crash has revoked guest authority.
    internal bool TryComplete(Lease? lease)
    {
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            if (!Owns(lease) || !lease!.Active) return false;
            lease.Active = false;
            if (lease.Revoked) Retire(lease);
            return true;
        }
        finally { Monitor.Exit(gate); }
    }

    internal bool TryRelease(Lease? lease, ActorIdentity actor, ulong epoch)
    {
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            if (!Valid(lease, actor, epoch)) return false;
            lease!.Revoked = true;
            if (!lease.Active) Retire(lease);
            return true;
        }
        finally { Monitor.Exit(gate); }
    }

    internal bool TryRevokeEpoch(ulong next)
    {
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            if (stopped || next <= policyEpoch) return false;
            policyEpoch = next;
            foreach (var slot in slots)
            {
                // Idle pre-connected sockets may carry stale TLS/tenant
                // authority. Epoch rollover quarantines every slot.
                slot.Quarantine = true;
                if (slot.Current is not { } lease) continue;
                lease.Revoked = true;
                if (!lease.Active) Retire(lease);
            }
            return true;
        }
        finally { Monitor.Exit(gate); }
    }

    internal bool TryStop()
    {
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            stopped = true;
            foreach (var slot in slots)
            {
                if (slot.Current is not { } lease) continue;
                lease.Revoked = true;
                if (!lease.Active) Retire(lease);
            }
            return true;
        }
        finally { Monitor.Exit(gate); }
    }

    /// <summary>
    /// Host-only fence after the old physical socket has actually closed
    /// and all pinned buffers/reactor completions are quiescent. A guest
    /// lease token is not proof that shutdown completed.
    /// </summary>
    internal bool TryConfirmPhysicalClose(int slot, ulong expectedGeneration)
    {
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            if (stopped || (uint)slot >= (uint)slots.Length) return false;
            var state = slots[slot];
            if (!state.Quarantine || state.Current is not null ||
                state.Generation != expectedGeneration) return false;
            state.Quarantine = false;
            return true;
        }
        finally { Monitor.Exit(gate); }
    }

    private bool Owns(Lease? lease)
    {
        if (lease is null || !ReferenceEquals(lease.Owner, this) ||
            (uint)lease.Slot >= (uint)slots.Length) return false;
        var state = slots[lease.Slot];
        return ReferenceEquals(state.Current, lease) &&
               state.Generation == lease.Generation;
    }

    private bool Valid(Lease? lease, ActorIdentity actor, ulong epoch)
    {
        return Owns(lease) && !lease!.Revoked && !stopped &&
               lease.Actor == actor && lease.Epoch == epoch &&
               epoch == policyEpoch && lease.Partition == partition;
    }

    private void Retire(Lease lease)
    {
        var state = slots[lease.Slot];
        state.Current = null;
        state.Quarantine = true; // No reuse before trusted physical close.
        lease.Revoked = true;
        // Saturation is terminal: never wrap to an earlier generation.
        if (state.Generation < ulong.MaxValue) state.Generation++;
    }
}
