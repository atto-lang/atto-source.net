using System.Runtime.InteropServices;

namespace Ores.Net.Runtime;

/// <summary>
/// Trusted-host actor identity. Only the scheduler may attest its values; this
/// record is not a guest authorization token or an unforgeable capability.
/// </summary>
public readonly record struct FixedBufferActorIdentity
{
    public ulong TenantId { get; }
    public ulong ActorId { get; }
    public ulong Incarnation { get; }

    public FixedBufferActorIdentity(ulong tenantId, ulong actorId, ulong incarnation)
    {
        if (tenantId == 0 || actorId == 0 || incarnation == 0)
            throw new ArgumentOutOfRangeException(nameof(actorId), "All actor identity fields must be nonzero");
        TenantId = tenantId;
        ActorId = actorId;
        Incarnation = incarnation;
    }
}

/// <summary>
/// Native fixed-capacity byte storage with affine host lease/loan/escrow
/// semantics. Transferable bytes DO NOT reside in actor-private ref32 arenas.
/// Objects representing leases and transfer receipts still allocate in the CLR
/// heap. This is host-only, not compiled .atto, no verified hot-path zero
/// allocation, and not an async-I/O pin or production mailbox integration.
/// </summary>
public unsafe sealed class NativeFixedBufferPoolV1 : IDisposable
{
    public sealed class Lease
    {
        internal readonly NativeFixedBufferPoolV1 Pool;
        internal readonly int SlotIndex;
        internal readonly ulong Generation;
        internal FixedBufferActorIdentity Owner;
        internal bool Live = true;
        internal bool Escrowed;

        internal Lease(NativeFixedBufferPoolV1 pool, int slotIndex,
            ulong generation, FixedBufferActorIdentity owner)
        {
            Pool = pool;
            SlotIndex = slotIndex;
            Generation = generation;
            Owner = owner;
        }

        public int Capacity => Pool._slotSize;
    }

    public sealed class Loan : IDisposable
    {
        internal readonly NativeFixedBufferPoolV1 Pool;
        internal readonly Lease Lease;
        internal readonly bool Mutable;
        internal bool Closed;

        internal Loan(NativeFixedBufferPoolV1 pool, Lease lease, bool mutable)
        {
            Pool = pool;
            Lease = lease;
            Mutable = mutable;
        }

        public void Dispose() => Pool.EndLoan(this);
    }

    public sealed class Escrow
    {
        internal readonly NativeFixedBufferPoolV1 Pool;
        internal readonly Lease Sender;
        internal bool Settled;

        internal Escrow(NativeFixedBufferPoolV1 pool, Lease sender)
        {
            Pool = pool;
            Sender = sender;
        }
    }

    private struct Slot
    {
        internal ulong Generation;
        internal Lease? Issued;
        internal int Readers;
        internal bool Writer;
        internal bool Disabled;
    }

    private readonly object _gate = new();
    private readonly Slot[] _slots;
    private readonly int _slotSize;
    private readonly int _totalBytes;
    private readonly ActorArenaBudget _budget;
    private readonly bool _separateProcessBudget;
    private byte* _memory;
    private int _inUse;
    private bool _retiring;
    private bool _reclaimed;

    public NativeFixedBufferPoolV1(int slotCount, int slotSize,
        ActorArenaBudget? budget = null)
    {
        if (slotCount <= 0 || slotCount > 65536 || slotSize <= 0 ||
            slotSize > ActorArena.MaxArenaBytes ||
            (long)slotCount * slotSize > ActorArena.MaxArenaBytes)
            throw new ArgumentOutOfRangeException(nameof(slotCount),
                "Invalid fixed buffer-pool size or capacity");
        _slotSize = slotSize;
        _totalBytes = checked(slotCount * slotSize);
        _slots = new Slot[slotCount];
        for (int i = 0; i < _slots.Length; ++i) _slots[i].Generation = 1;
        _budget = budget ?? ActorArenaBudget.Process;
        _separateProcessBudget = !ReferenceEquals(_budget, ActorArenaBudget.Process);
        _budget.Reserve(_totalBytes);
        bool processReserved = false;
        try
        {
            if (_separateProcessBudget)
            {
                ActorArenaBudget.Process.Reserve(_totalBytes);
                processReserved = true;
            }
            _memory = (byte*)NativeMemory.AllocZeroed((nuint)_totalBytes);
            if (_memory == null) throw new OutOfMemoryException("Buffer pool native slab");
        }
        catch
        {
            if (processReserved) ActorArenaBudget.Process.Refund(_totalBytes);
            _budget.Refund(_totalBytes);
            throw;
        }
    }

    public int Capacity => _slots.Length;
    public int SlotSize => _slotSize;
    public int InUse { get { lock (_gate) return _inUse; } }
    public bool IsQuiescent { get { lock (_gate) return _retiring && _inUse == 0; } }

    public Lease? TryAcquire(FixedBufferActorIdentity owner)
    {
        ValidateIdentity(owner);
        lock (_gate)
        {
            if (_retiring) return null;
            for (int i = 0; i < _slots.Length; ++i)
            {
                ref Slot slot = ref _slots[i];
                if (slot.Issued != null || slot.Disabled) continue;
                var lease = new Lease(this, i, slot.Generation, owner);
                slot.Issued = lease;
                ++_inUse;
                return lease;
            }
            return null;
        }
    }

    private static void ValidateIdentity(FixedBufferActorIdentity owner)
    {
        if (owner.TenantId == 0 || owner.ActorId == 0 || owner.Incarnation == 0)
            throw new InvalidOperationException("A trusted nonzero actor identity is required");
    }

    private ref Slot Check(Lease lease)
    {
        if (lease == null || !ReferenceEquals(lease.Pool, this) ||
            !lease.Live || (uint)lease.SlotIndex >= (uint)_slots.Length)
            throw new InvalidOperationException("Foreign or consumed pooled buffer lease");
        ref Slot slot = ref _slots[lease.SlotIndex];
        if (!ReferenceEquals(slot.Issued, lease) || slot.Generation != lease.Generation)
            throw new InvalidOperationException("Stale pooled buffer lease");
        return ref slot;
    }

    private ref Slot CheckOwner(Lease lease, FixedBufferActorIdentity owner)
    {
        ValidateIdentity(owner);
        ref Slot slot = ref Check(lease);
        if (lease.Owner != owner)
            throw new InvalidOperationException("Wrong tenant, actor or incarnation");
        return ref slot;
    }

    public Loan BorrowRead(Lease lease, FixedBufferActorIdentity owner)
    {
        lock (_gate)
        {
            ref Slot slot = ref CheckOwner(lease, owner);
            if (_retiring || lease.Escrowed || slot.Writer || slot.Readers == int.MaxValue)
                throw new InvalidOperationException("Read loan denied");
            ++slot.Readers;
            return new Loan(this, lease, false);
        }
    }

    public Loan BorrowWrite(Lease lease, FixedBufferActorIdentity owner)
    {
        lock (_gate)
        {
            ref Slot slot = ref CheckOwner(lease, owner);
            if (_retiring || lease.Escrowed || slot.Writer || slot.Readers != 0)
                throw new InvalidOperationException("Exclusive write loan denied");
            slot.Writer = true;
            return new Loan(this, lease, true);
        }
    }

    private void EndLoan(Loan loan)
    {
        lock (_gate)
        {
            if (loan == null || !ReferenceEquals(loan.Pool, this) || loan.Closed)
                throw new InvalidOperationException("Foreign or already released loan");
            ref Slot slot = ref Check(loan.Lease);
            if (loan.Mutable)
            {
                if (!slot.Writer) throw new InvalidOperationException("Missing write loan");
                slot.Writer = false;
            }
            else
            {
                if (slot.Readers == 0) throw new InvalidOperationException("Missing read loan");
                --slot.Readers;
            }
            loan.Closed = true;
        }
    }

    private ref Slot CheckLoan(Loan loan, bool write)
    {
        if (loan == null || !ReferenceEquals(loan.Pool, this) || loan.Closed ||
            (write && !loan.Mutable))
            throw new InvalidOperationException("Invalid pooled byte access loan");
        ref Slot slot = ref Check(loan.Lease);
        if (_retiring || loan.Lease.Escrowed ||
            (loan.Mutable ? !slot.Writer : slot.Readers == 0))
            throw new InvalidOperationException("Loan access denied");
        return ref slot;
    }

    private void CheckOffset(int offset)
    {
        if ((uint)offset >= (uint)_slotSize)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }

    public byte ReadByte(Loan loan, int offset)
    {
        lock (_gate)
        {
            CheckLoan(loan, false);
            CheckOffset(offset);
            return _memory[checked(loan.Lease.SlotIndex * _slotSize + offset)];
        }
    }

    public void WriteByte(Loan loan, int offset, byte value)
    {
        lock (_gate)
        {
            CheckLoan(loan, true);
            CheckOffset(offset);
            _memory[checked(loan.Lease.SlotIndex * _slotSize + offset)] = value;
        }
    }

    public Escrow BeginTransfer(Lease lease, FixedBufferActorIdentity sender)
    {
        lock (_gate)
        {
            ref Slot slot = ref CheckOwner(lease, sender);
            if (_retiring || lease.Escrowed || slot.Readers != 0 || slot.Writer)
                throw new InvalidOperationException("Transfer admission denied");
            lease.Escrowed = true;
            return new Escrow(this, lease);
        }
    }

    private ref Slot CheckEscrow(Escrow escrow)
    {
        if (escrow == null || !ReferenceEquals(escrow.Pool, this) || escrow.Settled)
            throw new InvalidOperationException("Settled or foreign buffer transfer");
        ref Slot slot = ref Check(escrow.Sender);
        if (!escrow.Sender.Escrowed || slot.Readers != 0 || slot.Writer)
            throw new InvalidOperationException("Escrow was not admitted");
        return ref slot;
    }

    public Lease CommitTransfer(Escrow escrow, FixedBufferActorIdentity receiver)
    {
        ValidateIdentity(receiver);
        lock (_gate)
        {
            ref Slot slot = ref CheckEscrow(escrow);
            if (_retiring || escrow.Sender.Owner.TenantId != receiver.TenantId)
                throw new InvalidOperationException("Retired or cross-tenant transfer");
            var recipient = new Lease(this, escrow.Sender.SlotIndex, slot.Generation, receiver);
            escrow.Sender.Live = false;
            escrow.Sender.Escrowed = false;
            slot.Issued = recipient;
            escrow.Settled = true;
            return recipient;
        }
    }

    public Lease AbortTransfer(Escrow escrow)
    {
        lock (_gate)
        {
            CheckEscrow(escrow);
            escrow.Sender.Escrowed = false;
            escrow.Settled = true;
            return escrow.Sender;
        }
    }

    public void Release(Lease lease, FixedBufferActorIdentity caller)
    {
        lock (_gate)
        {
            ref Slot slot = ref CheckOwner(lease, caller);
            if (lease.Escrowed || slot.Readers != 0 || slot.Writer)
                throw new InvalidOperationException("Lease is escrowed or borrowed");
            // Byte reuse across actors/tenants is preceded by explicit scrubbing.
            NativeMemory.Clear(_memory + checked(lease.SlotIndex * _slotSize),
                (nuint)_slotSize);
            lease.Live = false;
            slot.Issued = null;
            if (slot.Generation == ulong.MaxValue) slot.Disabled = true;
            else ++slot.Generation;
            --_inUse;
            TryReclaim();
        }
    }

    private void TryReclaim()
    {
        // Must be called under _gate only; active loans always hold a live lease.
        if (!_retiring || _inUse != 0 || _reclaimed) return;
        NativeMemory.Clear(_memory, (nuint)_totalBytes);
        NativeMemory.Free(_memory);
        _memory = null;
        _reclaimed = true;
        if (_separateProcessBudget) ActorArenaBudget.Process.Refund(_totalBytes);
        _budget.Refund(_totalBytes);
    }

    public void Retire()
    {
        lock (_gate)
        {
            _retiring = true;
            TryReclaim();
        }
    }

    public void Dispose() => Retire();
}
