// Trusted .NET HOST-ONLY per-message destination mailbox receipt proof.
// NOT compiled .atto send_ack/request, crypto or network, authorization,
// production TenantVM separation, NativeAOT execution, or a second application
// reply registry. Gateway must validate capabilities before TrySendTicket.
// Full route identity is correlation metadata, not proof of authorization.
using System;
using System.Collections.Generic;

namespace Atto.Runtime.Proxy;

public enum HostTicketStateV1 { Admitted, Started, Processed, Failed, Abandoned }
public enum HostTicketAdmissionStateV1 { Admitted, Invalid, Stale, Closed, Full }
public sealed record HostTicketSnapshotV1(
    ReceiptIdentityV1 Route, HostTicketStateV1 State, bool HasResult, long Result);
public sealed class HostMailboxTicketV1
{
    internal readonly object Owner;
    internal readonly ReceiptIdentityV1 Route;
    internal HostTicketStateV1 State;
    internal bool HasResult;
    internal long CompletionValue;
    internal HostMailboxTicketV1(object owner, ReceiptIdentityV1 route)
    {
        Owner = owner;
        Route = route;
        State = HostTicketStateV1.Admitted;
    }
}
public sealed record HostTicketAdmissionV1(
    HostTicketAdmissionStateV1 Status, HostMailboxTicketV1? Ticket);
public sealed class HostTurnV1
{
    internal readonly HostMailboxTicketV1 Ticket;
    public long Value { get; }
    internal HostTurnV1(HostMailboxTicketV1 ticket, long value)
    {
        Ticket = ticket; Value = value;
    }
}
public sealed class HostProxyMailboxTicketsV1
{
    private readonly object gate = new();
    private readonly object owner = new();
    private readonly ulong actor;
    private readonly ulong incarnation;
    private readonly int capacity;
    private readonly Queue<(long Value, HostMailboxTicketV1 Ticket)> queued = new();
    private HostMailboxTicketV1? current;
    private bool closed, fenced;
    private long admitted, processed, failed, abandoned;

    public HostProxyMailboxTicketsV1(ulong actor, ulong incarnation, int capacity)
    {
        if (actor == 0 || incarnation == 0 || capacity is < 1 or > 4096)
            throw new ArgumentException("invalid bounded actor mailbox");
        this.actor = actor; this.incarnation = incarnation; this.capacity = capacity;
    }
    // Authorization is the caller's obligation; the route only scopes the
    // issued handle. Enqueue and ticket issue have one linearization point.
    public HostTicketAdmissionV1 TrySendTicket(ReceiptIdentityV1 route, long value)
    {
        if (!route.IsValid)
            return new(HostTicketAdmissionStateV1.Invalid, null);
        if (route.TargetActor != actor || route.TargetIncarnation != incarnation)
            return new(HostTicketAdmissionStateV1.Stale, null);
        lock (gate)
        {
            if (closed || fenced) return new(HostTicketAdmissionStateV1.Closed, null);
            if (queued.Count >= capacity)
                return new(HostTicketAdmissionStateV1.Full, null);
            var ticket = new HostMailboxTicketV1(owner, route);
            queued.Enqueue((value, ticket));
            admitted = checked(admitted + 1);
            return new(HostTicketAdmissionStateV1.Admitted, ticket);
        }
    }
    public HostTicketSnapshotV1? Snapshot(HostMailboxTicketV1 ticket)
    {
        lock (gate)
        {
            if (ticket is null || !ReferenceEquals(ticket.Owner, owner)) return null;
            return new(ticket.Route, ticket.State, ticket.HasResult, ticket.CompletionValue);
        }
    }
    // Called by trusted actor scheduler after *this mail* is dequeued.
    public HostTurnV1? Begin()
    {
        lock (gate)
        {
            if (fenced || current is not null || queued.Count == 0) return null;
            var item = queued.Dequeue();
            if (item.Ticket.State != HostTicketStateV1.Admitted)
                throw new InvalidOperationException("mailbox conservation failed");
            item.Ticket.State = HostTicketStateV1.Started;
            current = item.Ticket;
            return new HostTurnV1(item.Ticket, item.Value);
        }
    }
    public bool Commit(HostTurnV1 turn, long result)
    {
        lock (gate)
        {
            if (turn is null || fenced || current is null ||
                !ReferenceEquals(turn.Ticket, current) ||
                current.State != HostTicketStateV1.Started) return false;
            current.CompletionValue = result;
            current.HasResult = true;
            current.State = HostTicketStateV1.Processed;
            current = null;
            processed = checked(processed + 1);
            return true;
        }
    }
    // Panic fails the actual active mail and abandons queued mail. A separate
    // supervisor CONTROL path remains required in a real actor runtime.
    public bool Panic(HostTurnV1 turn)
    {
        lock (gate)
        {
            if (turn is null || fenced || current is null ||
                !ReferenceEquals(turn.Ticket, current) ||
                current.State != HostTicketStateV1.Started) return false;
            current.State = HostTicketStateV1.Failed;
            current = null;
            failed = checked(failed + 1);
            fenced = true;
            closed = true;
            while (queued.Count > 0)
            {
                var item = queued.Dequeue();
                if (item.Ticket.State != HostTicketStateV1.Admitted)
                    throw new InvalidOperationException("mailbox conservation failed");
                item.Ticket.State = HostTicketStateV1.Abandoned;
                abandoned = checked(abandoned + 1);
            }
            return true;
        }
    }
    public void Close() { lock (gate) closed = true; } // allow previously admitted drain
    public bool CheckConservation()
    {
        lock (gate)
            return admitted == processed + failed + abandoned +
                queued.Count + (current is null ? 0 : 1);
    }
}
