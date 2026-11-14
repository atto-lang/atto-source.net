using System;
using System.Collections.Generic;

namespace Ores.Net.Runtime;

/// <summary>
/// Trusted host CONTROL ledger for STOP-only supervision.
/// This does not install a source-level supervisor or run guest lifecycle code.
/// Physical finalization and successful external STOP must be asserted by
/// trusted runtime code, not by a serialized actor handle or guest callback.
/// </summary>
internal sealed class SupervisorStopLedger
{
    internal enum Phase { Active, Finalized, Stopping, Stopped }
    internal enum Receipt { Pending, Applied, Uncertain, Acknowledged }
    internal readonly record struct Child(Guid Supervisor, Guid Group, Guid LogicalId);
    internal readonly record struct StopTicket(
        Child Child, ActorRef Actor, ulong Incarnation, ulong Turn, Guid Nonce)
    {
        // Nonces fence host reconciliation, never appear in logs/wire text.
        public override string ToString() => "StopTicket(redacted)";
    }

    private sealed class Entry(Child child, ActorRef actor)
    {
        internal readonly Child Child = child;
        internal readonly ActorRef Actor = actor;
        internal Phase State = Phase.Active;
        internal Receipt Status = Receipt.Pending;
        internal StopTicket? Ticket;
        internal bool Invoked;
    }

    private readonly object _gate = new();
    private readonly Guid _supervisor;
    private readonly Guid _group;
    private readonly int _capacity;
    private readonly Dictionary<Guid, Entry> _children = new();
    private ulong _turn;

    internal SupervisorStopLedger(Guid supervisor, Guid group, int capacity)
    {
        if (supervisor == Guid.Empty || group == Guid.Empty)
            throw new ArgumentException("supervisor and group identities must be nonempty");
        if (capacity < 1 || capacity > 65_536)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _supervisor = supervisor;
        _group = group;
        _capacity = capacity;
    }

    internal Child Register(ActorRef actor)
    {
        lock (_gate)
        {
            if (_children.Count >= _capacity)
                throw new InvalidOperationException("supervisor child capacity exhausted");
            if (actor.Id == 0 || actor.Generation == 0 || actor.Nonce == Guid.Empty)
                throw new ArgumentException("invalid actor authority", nameof(actor));
            foreach (var existing in _children.Values)
                if (existing.Actor == actor)
                    throw new InvalidOperationException("duplicate physical actor registration");
            var child = new Child(_supervisor, _group, Guid.NewGuid());
            _children.Add(child.LogicalId, new Entry(child, actor));
            return child;
        }
    }

    private Entry Require(Child child, ActorRef actor)
    {
        if (child.Supervisor != _supervisor || child.Group != _group ||
            !_children.TryGetValue(child.LogicalId, out var entry) ||
            entry.Child != child || entry.Actor != actor)
            throw new InvalidOperationException("foreign or stale supervisor child identity");
        return entry;
    }

    private Entry Require(StopTicket ticket)
    {
        var entry = Require(ticket.Child, ticket.Actor);
        if (entry.State != Phase.Stopping || entry.Ticket != ticket)
            throw new InvalidOperationException("stale, replayed or forged STOP ticket");
        return entry;
    }

    internal Phase Snapshot(Child child, ActorRef actor)
    {
        lock (_gate) return Require(child, actor).State;
    }

    // Only after the actor has physically completed cleanup and exited.
    internal void AssertPhysicallyFinalized(Child child, ActorRef actor)
    {
        lock (_gate)
        {
            var entry = Require(child, actor);
            if (entry.State != Phase.Active)
                throw new InvalidOperationException("duplicate or stale finalization");
            entry.State = Phase.Finalized;
        }
    }

    internal StopTicket ReserveStop(Child child, ActorRef actor)
    {
        lock (_gate)
        {
            var entry = Require(child, actor);
            if (entry.State != Phase.Finalized)
                throw new InvalidOperationException("STOP requires finalized physical actor");
            if (_turn == ulong.MaxValue)
                throw new InvalidOperationException("supervisor CONTROL turn overflow");
            var ticket = new StopTicket(child, actor, actor.Generation, _turn + 1, Guid.NewGuid());
            entry.Ticket = ticket;
            entry.Status = Receipt.Pending;
            entry.State = Phase.Stopping;
            _turn++;
            return ticket;
        }
    }

    internal void ClaimHostInvocation(StopTicket ticket)
    {
        lock (_gate)
        {
            var entry = Require(ticket);
            if (entry.Invoked || entry.Status != Receipt.Pending)
                throw new InvalidOperationException("STOP effect already claimed or classified");
            entry.Invoked = true;
        }
    }

    internal void MarkApplied(StopTicket ticket)
    {
        lock (_gate)
        {
            var entry = Require(ticket);
            if (!entry.Invoked || entry.Status != Receipt.Pending)
                throw new InvalidOperationException("unclaimed or previously classified STOP");
            entry.Status = Receipt.Applied;
        }
    }

    internal void MarkUncertain(StopTicket ticket)
    {
        lock (_gate)
        {
            var entry = Require(ticket);
            if (!entry.Invoked)
                throw new InvalidOperationException("STOP was never invoked");
            if (entry.Status == Receipt.Uncertain) return;
            if (entry.Status != Receipt.Pending)
                throw new InvalidOperationException("cannot downgrade confirmed STOP");
            entry.Status = Receipt.Uncertain;
        }
    }

    internal void AcknowledgeApplied(StopTicket ticket)
    {
        lock (_gate)
        {
            var entry = Require(ticket);
            if (entry.Status != Receipt.Applied)
                throw new InvalidOperationException("host STOP was not confirmed applied");
            entry.Status = Receipt.Acknowledged;
            entry.State = Phase.Stopped;
            entry.Ticket = null;
        }
    }

    /// <summary>
    /// Caller must independently verify the external host STOP result. This
    /// method only fences its matching ticket; it cannot verify the host.
    /// </summary>
    internal void ReconcileVerifiedStop(StopTicket ticket)
    {
        lock (_gate)
        {
            var entry = Require(ticket);
            if (entry.Status != Receipt.Uncertain && entry.Status != Receipt.Applied)
                throw new InvalidOperationException("STOP not ready for reconciliation");
            entry.Status = Receipt.Acknowledged;
            entry.State = Phase.Stopped;
            entry.Ticket = null;
        }
    }

    internal Receipt StopReceipt(StopTicket ticket)
    {
        lock (_gate) return Require(ticket).Status;
    }
}
