namespace Ores.Net.Runtime;

// Host-only fixed-capacity ledger. A trusted dispatcher must additionally
// establish task/FFI/arena quiescence. No source guest or NativeAOT proof.
public sealed class ActorSpawnQuotaGate
{
    public readonly record struct Charge(ulong Heap, ulong Stacks, ulong MailboxBytes, ulong Messages)
    {
        internal bool Positive => Heap != 0 && Stacks != 0 && MailboxBytes != 0 && Messages != 0;
        internal bool Fits(Charge limit, Charge used) =>
            Heap <= limit.Heap - used.Heap && Stacks <= limit.Stacks - used.Stacks &&
            MailboxBytes <= limit.MailboxBytes - used.MailboxBytes && Messages <= limit.Messages - used.Messages;
        internal Charge Add(Charge x) => new(Heap+x.Heap, Stacks+x.Stacks, MailboxBytes+x.MailboxBytes, Messages+x.Messages);
        internal Charge Subtract(Charge x) => new(Heap-x.Heap, Stacks-x.Stacks, MailboxBytes-x.MailboxBytes, Messages-x.Messages);
    }
    public readonly struct Permit
    {
        internal readonly ActorSpawnQuotaGate? Owner;
        internal readonly int Slot;
        public ulong Generation { get; }
        internal Permit(ActorSpawnQuotaGate owner, int slot, ulong generation)
        { Owner=owner; Slot=slot; Generation=generation; }
    }
    public readonly struct InitLease
    {
        internal readonly Permit Permit;
        internal InitLease(Permit permit) { Permit=permit; }
    }
    public readonly struct ReceiveLease
    {
        internal readonly Permit Permit;
        internal readonly ulong Id;
        internal ReceiveLease(Permit permit, ulong id) { Permit=permit; Id=id; }
    }
    private enum Phase : byte { Free, Initializing, Ready, Retiring }
    private struct Entry
    {
        internal Phase Phase;
        internal ulong Generation;
        internal Charge Charge;
        internal bool InitStarted, InitLive;
        internal ulong NextReceive;
    }
    private readonly object _lock = new();
    private readonly Charge _limit;
    private readonly Entry[] _entries;
    private readonly ulong[,] _receives;
    private Charge _used;
    private ulong _generation;

    public ActorSpawnQuotaGate(Charge limit, int slots, int receivesPerActor)
    {
        if (!limit.Positive || slots <= 0 || receivesPerActor <= 0 ||
            (long)slots * receivesPerActor > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(slots));
        _limit=limit; _entries=new Entry[slots]; _receives=new ulong[slots, receivesPerActor];
    }
    public Charge Usage { get { lock (_lock) return _used; } }
    public Permit Reserve(Charge charge)
    {
        lock (_lock)
        {
            if (!charge.Positive || !charge.Fits(_limit,_used)) throw Denied("E_SPAWN_QUOTA");
            int slot=Array.FindIndex(_entries, static e => e.Phase==Phase.Free);
            if (slot<0) throw Denied("E_SPAWN_FULL");
            if (_generation==ulong.MaxValue) throw Denied("E_SPAWN_GENERATION_EXHAUSTED");
            ulong generation=++_generation;
            _entries[slot]=new Entry { Phase=Phase.Initializing, Charge=charge, Generation=generation };
            _used=_used.Add(charge);
            return new Permit(this,slot,generation);
        }
    }
    private ref Entry Require(Permit p)
    {
        if (!ReferenceEquals(p.Owner,this) || (uint)p.Slot >= (uint)_entries.Length ||
            _entries[p.Slot].Phase==Phase.Free || _entries[p.Slot].Generation!=p.Generation)
            throw Denied("E_SPAWN_STALE_PERMIT");
        return ref _entries[p.Slot];
    }
    public InitLease BeginInit(Permit p)
    {
        lock (_lock)
        {
            ref Entry e=ref Require(p);
            if (e.Phase!=Phase.Initializing || e.InitStarted) throw Denied("E_SPAWN_INIT_AUTHORITY");
            e.InitStarted=e.InitLive=true;
            return new InitLease(p);
        }
    }
    public bool CompleteInit(InitLease lease, bool success)
    {
        lock (_lock)
        {
            ref Entry e=ref Require(lease.Permit);
            if (!e.InitLive) throw Denied("E_SPAWN_INIT_AUTHORITY");
            e.InitLive=false;
            if (!success || e.Phase==Phase.Retiring) { e.Phase=Phase.Retiring; return false; }
            e.Phase=Phase.Ready; return true;
        }
    }
    public ReceiveLease? TryEnterReceive(Permit p)
    {
        lock (_lock)
        {
            ref Entry e=ref Require(p);
            if (e.Phase!=Phase.Ready) return null;
            for (int i=0;i<_receives.GetLength(1);i++)
            {
                if (_receives[p.Slot,i]!=0) continue;
                if (e.NextReceive==ulong.MaxValue) throw Denied("E_SPAWN_LEASE_LIMIT");
                ulong id=++e.NextReceive;
                _receives[p.Slot,i]=id;
                return new ReceiveLease(p,id);
            }
            throw Denied("E_SPAWN_FULL");
        }
    }
    public void FinishReceive(ReceiveLease lease)
    {
        lock (_lock)
        {
            _=Require(lease.Permit);
            if (lease.Id!=0)
                for (int i=0;i<_receives.GetLength(1);i++)
                    if (_receives[lease.Permit.Slot,i]==lease.Id)
                    { _receives[lease.Permit.Slot,i]=0; return; }
            throw Denied("E_SPAWN_STALE_PERMIT");
        }
    }
    public void Retire(Permit p) { lock (_lock) { Require(p).Phase=Phase.Retiring; } }
    public void ReleaseAfterQuiescence(Permit p)
    {
        lock (_lock)
        {
            ref Entry e=ref Require(p);
            if (e.Phase!=Phase.Retiring) throw Denied("E_SPAWN_NOT_RETIRED");
            if (e.InitLive) throw Denied("E_SPAWN_IN_FLIGHT");
            for (int i=0;i<_receives.GetLength(1);i++)
                if (_receives[p.Slot,i]!=0) throw Denied("E_SPAWN_IN_FLIGHT");
            _used=_used.Subtract(e.Charge);
            e=default;
        }
    }
    private static InvalidOperationException Denied(string code) => new(code);
}
