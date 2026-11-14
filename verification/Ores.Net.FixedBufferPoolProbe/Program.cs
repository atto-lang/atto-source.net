using System.Buffers.Binary;
using Ores.Net.Runtime;

static void Require(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

static void Deny(Action action, string message)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("Missing negative proof: " + message);
}

var sender = new FixedBufferActorIdentity(1, 10, 1);
var receiver = new FixedBufferActorIdentity(1, 11, 2);
var wrongTenant = new FixedBufferActorIdentity(2, 12, 1);
var reincarnated = new FixedBufferActorIdentity(1, 10, 2);
using var pool = new NativeFixedBufferPoolV1(2, 16);
var first = pool.TryAcquire(sender)!;
var second = pool.TryAcquire(receiver)!;
Require(first != null && second != null && pool.TryAcquire(sender) == null && pool.InUse == 2,
    "fixed capacity must not grow under exhaustion");

using (var write = pool.BorrowWrite(first, sender))
{
    pool.WriteByte(write, 0, 0x5a);
    Require(pool.ReadByte(write, 0) == 0x5a, "native byte roundtrip");
    Deny(() => pool.BorrowRead(first, sender), "writer denies reader");
    Deny(() => pool.BorrowWrite(first, sender), "writer denies writer");
    Deny(() => pool.BeginTransfer(first, sender), "loan denies transfer");
    Deny(() => pool.Release(first, sender), "loan denies release");

    // A narrow native scalar transport primitive; this is not yet a mailbox.
    Span<byte> incoming = stackalloc byte[8];
    Span<byte> observed = stackalloc byte[8];
    for (int i = 0; i < 512; ++i)
    {
        BinaryPrimitives.WriteInt64LittleEndian(incoming, i);
        pool.WriteBytes(write, 1, incoming);
        pool.ReadBytes(write, 1, observed);
        Require(incoming.SequenceEqual(observed), "bulk copied bytes differ");
    }
    _ = GC.GetAllocatedBytesForCurrentThread();
    long before = GC.GetAllocatedBytesForCurrentThread();
    bool coherent = true;
    for (int i = 0; i < 50_000; ++i)
    {
        BinaryPrimitives.WriteInt64LittleEndian(incoming, i);
        pool.WriteBytes(write, 1, incoming);
        pool.ReadBytes(write, 1, observed);
        if (!incoming.SequenceEqual(observed)) { coherent = false; break; }
    }
    long managedDelta = GC.GetAllocatedBytesForCurrentThread() - before;
    Require(coherent && managedDelta == 0,
        "50k warmed native bulk operations must allocate zero CLR bytes");
    pool.WriteBytes(write, 16, ReadOnlySpan<byte>.Empty);
    pool.ReadBytes(write, 16, Span<byte>.Empty);
    byte[] tooLong = new byte[2];
    bool boundsRejected = false;
    try { pool.ReadBytes(write, 15, tooLong); }
    catch (ArgumentOutOfRangeException) { boundsRejected = true; }
    Require(boundsRejected, "bulk read past end must reject");
    boundsRejected = false;
    try { pool.WriteBytes(write, -1, tooLong); }
    catch (ArgumentOutOfRangeException) { boundsRejected = true; }
    Require(boundsRejected, "negative bulk write offset must reject");
    pool.ReadBytes(write, 1, observed);
    Require(BinaryPrimitives.ReadInt64LittleEndian(observed) == 49_999L,
        "out of bounds write must not mutate the slot");

}
using (var read = pool.BorrowRead(first, sender))
{
    Require(pool.ReadByte(read, 0) == 0x5a, "read loan preserves bytes");
    Deny(() => pool.BorrowWrite(first, sender), "reader denies writer");
}
// Preserve source-owner authority on native bulk reads and writes.
using (var read = pool.BorrowRead(first, sender))
{
    byte[] target = new byte[8];
    pool.ReadBytes(read, 1, target);
    Require(BinaryPrimitives.ReadInt64LittleEndian(target) == 49_999L,
        "read loan bulk scalar state");
    bool deniedWrite = false;
    try { pool.WriteBytes(read, 1, target); }
    catch (InvalidOperationException) { deniedWrite = true; }
    Require(deniedWrite, "read loan must not authorize bulk write");
}
Deny(() => pool.BorrowRead(first, reincarnated), "stale incarnation");
Deny(() => pool.Release(first, wrongTenant), "wrong tenant");
var aborted = pool.BeginTransfer(first, sender);
Deny(() => pool.BorrowRead(first, sender), "escrow denies sender access");
bool escrowDenied = false;
try
{
    using var forbidden = pool.BorrowRead(first, sender);
    byte[] bytes = new byte[8];
    pool.ReadBytes(forbidden, 1, bytes);
}
catch (InvalidOperationException) { escrowDenied = true; }
Require(escrowDenied, "escrowed slot cannot expose bulk data");

Deny(() => pool.BeginTransfer(first, sender), "escrow denies second transfer");
Deny(() => pool.CommitTransfer(aborted, wrongTenant), "cross tenant denied");
Require(ReferenceEquals(pool.AbortTransfer(aborted), first), "abort restores exact lease");
Deny(() => pool.AbortTransfer(aborted), "double abort");
using (var write = pool.BorrowWrite(first, sender))
    Require(pool.ReadByte(write, 0) == 0x5a, "aborted transfer kept backing bytes");
var escrow = pool.BeginTransfer(first, sender);
var moved = pool.CommitTransfer(escrow, receiver);
Deny(() => pool.BorrowRead(first, sender), "old sender lease invalidated");
Deny(() => pool.Release(first, sender), "old sender cannot recycle");
Deny(() => pool.CommitTransfer(escrow, receiver), "duplicate commit");
using (var read = pool.BorrowRead(moved, receiver))
    Require(pool.ReadByte(read, 0) == 0x5a, "zero-copy owner move kept bytes");
pool.Release(moved, receiver);
Deny(() => pool.Release(moved, receiver), "double free");
var recycled = pool.TryAcquire(wrongTenant)!;
using (var read = pool.BorrowRead(recycled, wrongTenant))
    Require(pool.ReadByte(read, 0) == 0, "scrub before cross-tenant reuse");
Deny(() => pool.BorrowRead(moved, receiver), "recycled generation denies stale lease");
pool.Release(recycled, wrongTenant);
pool.Release(second, receiver);

for (int i = 0; i < 100_000; ++i)
{
    var lease = pool.TryAcquire(sender)!;
    Require(lease != null, "warm loop must have a free slot");
    using (var loan = pool.BorrowWrite(lease, sender))
        pool.WriteByte(loan, 0, (byte)i);
    var transfer = pool.BeginTransfer(lease, sender);
    var recipientLease = pool.CommitTransfer(transfer, receiver);
    using (var loan = pool.BorrowRead(recipientLease, receiver))
        Require(pool.ReadByte(loan, 0) == (byte)i, "byte identity across logical move");
    pool.Release(recipientLease, receiver);
}
var retained = pool.TryAcquire(sender)!;
using (var loan = pool.BorrowRead(retained, sender))
{
    pool.Retire();
    Require(pool.TryAcquire(sender) == null && !pool.IsQuiescent,
        "retirement must reject new acquisition and retain active backing");
    Deny(() => pool.BorrowWrite(retained, sender), "retired pool rejects new borrow");
}
pool.Release(retained, sender);
Require(pool.IsQuiescent && pool.InUse == 0, "retirement reclaims only after last release");
Console.WriteLine("HOST_ONLY_NATIVE_FIXED_BUFFER_POOL_V1_PASS|ops=100000|compiled_atto=NOT_EXECUTED|zero_alloc=NOT_PROVEN");
