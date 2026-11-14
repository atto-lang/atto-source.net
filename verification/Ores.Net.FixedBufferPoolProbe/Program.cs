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
}
using (var read = pool.BorrowRead(first, sender))
{
    Require(pool.ReadByte(read, 0) == 0x5a, "read loan preserves bytes");
    Deny(() => pool.BorrowWrite(first, sender), "reader denies writer");
}
Deny(() => pool.BorrowRead(first, reincarnated), "stale incarnation");
Deny(() => pool.Release(first, wrongTenant), "wrong tenant");
var aborted = pool.BeginTransfer(first, sender);
Deny(() => pool.BorrowRead(first, sender), "escrow denies sender access");
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
Console.WriteLine("HOST_ONLY_NATIVE_FIXED_BUFFER_POOL_V1_PASS|ops=100000|compiled_atto=NOT_EXECUTED|native_aot=NOT_EXECUTED|zero_alloc=NOT_PROVEN");
