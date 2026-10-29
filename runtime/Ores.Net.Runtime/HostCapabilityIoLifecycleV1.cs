namespace Ores.Net.Runtime;

/// <summary>
/// HOST-ONLY reference lifecycle gate; not guest I/O, a sandbox, or authority
/// to create a network connection. Trusted transport MUST authenticate actor
/// incarnation and independently authorize both requested and selected target.
/// </summary>
public sealed class HostCapabilityIoLifecycleV1
{
    public readonly record struct Ticket(int Slot, ulong Nonce);
    private enum Phase { Free, Reserved, Dispatched, CancelRequested, Settled }
    private sealed class Slot
    {
        internal ulong Actor, Incarnation, Selected, Nonce;
        internal Phase Phase;
    }
    private readonly Slot[] _slots;
    private readonly (ulong Actor, ulong Incarnation)[] _revoked;
    private readonly object _sync = new();
    private int _inFlight, _revokedCount;
    private ulong _nextNonce;
    private bool _closed;

    public HostCapabilityIoLifecycleV1(int capacity, int maxRevocations = 1024)
    {
        if (capacity < 1 || capacity > 65536 || maxRevocations < 1 || maxRevocations > 65536)
            throw new ArgumentOutOfRangeException(nameof(capacity), "bounded lifecycle capacity required");
        _slots = Enumerable.Range(0, capacity).Select(_ => new Slot()).ToArray();
        _revoked = new (ulong, ulong)[maxRevocations];
    }

    public Ticket? Reserve(bool authenticated, bool requestedAllowed, bool selectedAllowed,
                           ulong actor, ulong incarnation, ulong selectedBinding)
    {
        lock (_sync)
        {
            if (_closed || !authenticated || !requestedAllowed || !selectedAllowed ||
                actor == 0 || incarnation == 0 || selectedBinding == 0 ||
                IsRevoked(actor, incarnation)) return null;
            for (int i = 0; i < _slots.Length; ++i)
            {
                var slot = _slots[i];
                if (slot.Phase != Phase.Free) continue;
                if (_nextNonce == ulong.MaxValue) { _closed = true; return null; }
                slot.Actor = actor;
                slot.Incarnation = incarnation;
                slot.Selected = selectedBinding;
                slot.Nonce = ++_nextNonce;
                slot.Phase = Phase.Reserved;
                ++_inFlight;
                return new Ticket(i, slot.Nonce);
            }
            return null;
        }
    }

    public bool Dispatch(Ticket ticket, ulong actor, ulong incarnation, ulong actualBinding)
    {
        lock (_sync)
        {
            var slot = Owned(ticket, actor, incarnation);
            if (_closed || slot is null || slot.Phase != Phase.Reserved ||
                slot.Selected != actualBinding || IsRevoked(actor, incarnation)) return false;
            slot.Phase = Phase.Dispatched;
            return true;
        }
    }
    public bool Cancel(Ticket ticket, ulong actor, ulong incarnation)
    {
        lock (_sync)
        {
            var slot = Owned(ticket, actor, incarnation);
            if (slot is null || (slot.Phase != Phase.Reserved && slot.Phase != Phase.Dispatched))
                return false;
            slot.Phase = Phase.CancelRequested;
            return true;
        }
    }
    public bool Complete(Ticket ticket, ulong actor, ulong incarnation)
    {
        lock (_sync)
        {
            var slot = Owned(ticket, actor, incarnation);
            if (slot is null || slot.Phase is Phase.Free or Phase.Settled) return false;
            slot.Phase = Phase.Settled;
            return true;
        }
    }
    /// <summary>
    /// Only trusted host adapter can attest physical descriptor/transport retirement.
    /// A cancelled or completed Future alone cannot release the active credit.
    /// </summary>
    public bool Retire(Ticket ticket, ulong actor, ulong incarnation, bool physicallyQuiesced)
    {
        lock (_sync)
        {
            var slot = Owned(ticket, actor, incarnation);
            if (slot is null || !physicallyQuiesced ||
                (slot.Phase != Phase.Settled && slot.Phase != Phase.CancelRequested)) return false;
            slot.Actor = slot.Incarnation = slot.Selected = slot.Nonce = 0;
            slot.Phase = Phase.Free;
            --_inFlight;
            return true;
        }
    }
    public bool RevokeActor(ulong actor, ulong incarnation)
    {
        lock (_sync)
        {
            if (actor == 0 || incarnation == 0 || IsRevoked(actor, incarnation)) return false;
            if (_revokedCount == _revoked.Length) _closed = true;
            else _revoked[_revokedCount++] = (actor, incarnation);
            foreach (var slot in _slots)
            {
                if (slot.Actor == actor && slot.Incarnation == incarnation &&
                    slot.Phase is Phase.Reserved or Phase.Dispatched)
                    slot.Phase = Phase.CancelRequested;
            }
            return true;
        }
    }
    public int InFlight { get { lock (_sync) return _inFlight; } }
    private Slot? Owned(Ticket ticket, ulong actor, ulong incarnation)
    {
        if (ticket.Nonce == 0 || ticket.Slot < 0 || ticket.Slot >= _slots.Length) return null;
        var slot = _slots[ticket.Slot];
        return slot.Nonce == ticket.Nonce && slot.Actor == actor && slot.Incarnation == incarnation
            ? slot : null;
    }
    private bool IsRevoked(ulong actor, ulong incarnation)
    {
        for (int i = 0; i < _revokedCount; ++i)
            if (_revoked[i] == (actor, incarnation)) return true;
        return false;
    }
}
