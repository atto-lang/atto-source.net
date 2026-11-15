using Ores.Net.Runtime;
using System.Reflection;

internal static class ActorArenaSelfTest
{
    public static void Run()
    {
        CheckedFourByteRefsRejectForeignAndStaleTargets();
        CheckedRef32SurvivesDynamicBackingGrowth();
        DemandBackedGrowthPreservesActorRootsAndBudget();
        DemandBackedGrowthDeniedDuringFfiPin();
        DemandBackedScratchAndQuiescentPin();
        PersistentAndScratchLifetimes();
        ReadLoanFreezesAllocationAcrossTurns();
        PairedLoanAndRetirementLeavePeerWritable();
        LifetimePinDelaysPhysicalReclaim();
        AggregateBudgetAdmissionIsNeutral();
        RuntimeBudgetsAlsoChargeProcessLimit();
        PersistentRootWithTailTempsDoesNotExhaustArena();
        RetirementSealsAdmissionButAllowsTurnCleanup();
        CrossArenaAuthorityAndGenerationFences();
        ForgedMetadataCannotAcquireMemoryAuthority();
        ActiveTurnRequiresExactTokenAndCarrier();
        ForgedLoanAndPinCannotPrematurelyReclaim();
        Console.WriteLine("PASS .NET actor-owned unmanaged arena, loans, pins and quiescent reclaim");
    }

    private static void CheckedFourByteRefsRejectForeignAndStaleTargets()
    {
        using var owner = new ActorArena(85001, 1, 4096, 512, 8, 2, 2);
        using var foreign = new ActorArena(85002, 1, 4096, 512, 8, 2, 2);

        ActorArenaHandle foreignObject;
        using (var otherTurn = foreign.EnterTurn(85002, 1))
            foreignObject = foreign.AllocatePersistent(otherTurn, 8);

        using (var turn = owner.EnterTurn(85001, 1))
        {
            var parent = owner.AllocatePersistent(turn, 16);
            var original = owner.AllocatePersistent(turn, 8);
            Require(owner.ReadManagedRef32(turn, parent, 0) is null,
                "uninitialized guest 32-bit field reads as null");
            owner.WriteManagedRef32(turn, parent, 0, original);

            var raw = new byte[4];
            owner.Read(turn, parent, 0, raw);
            // Parent occupies 0..15; target starts at byte 16 => (16/8)+1.
            Require(raw[0] == 3 && raw[1] == 0 && raw[2] == 0 && raw[3] == 0,
                "guest field is exactly four little-endian bytes, not a 64-bit pointer");
            Require(ReferenceEquals(owner.ReadManagedRef32(turn, parent, 0), original),
                "host ledger restores exact original issued target capability");

            ExpectInvalid(() => owner.WriteManagedRef32(turn, parent, 4, foreignObject),
                "foreign actor handle is not authority to create a ref32 edge");
            Require(owner.ReadManagedRef32(turn, parent, 4) is null,
                "denied foreign reference leaves target field null");

            using (var frozenParent = owner.BorrowRead(turn, parent))
            {
                ExpectInvalid(() => owner.WriteManagedRef32(turn, parent, 0, null),
                    "immutable read loan blocks reference field mutation");
                Require(ReferenceEquals(owner.ReadManagedRef32(turn, parent, 0), original),
                    "read-only parent snapshot retains original target");
            }

            owner.ReleasePersistent(turn, original);
            ExpectInvalid(() => owner.ReadManagedRef32(turn, parent, 0),
                "released target is stale even though its 32-bit offset remains");
            var recycled = owner.AllocatePersistent(turn, 8);
            owner.Read(turn, parent, 0, raw);
            Require(raw[0] == 3,
                "slot/offset recycling can reproduce the same raw ref32 word");
            ExpectInvalid(() => owner.ReadManagedRef32(turn, parent, 0),
                "sidecar original-generation proof prevents ref32 ABA resurrection");
            owner.WriteManagedRef32(turn, parent, 0, recycled);
            Require(ReferenceEquals(owner.ReadManagedRef32(turn, parent, 0), recycled),
                "explicitly replacing field refreshes exact generation proof");

            owner.Write(turn, parent, 0, new byte[] { 255, 255, 255, 255 });
            ExpectInvalid(() => owner.ReadManagedRef32(turn, parent, 0),
                "forged raw 32-bit field cannot authorize a different target");
            owner.WriteManagedRef32(turn, parent, 0, null);
            Require(owner.ReadManagedRef32(turn, parent, 0) is null,
                "clearing a reference writes null and removes original-generation proof");

            bool rejectedMisalignment = false;
            try { owner.ReadManagedRef32(turn, parent, 2); }
            catch (ArgumentOutOfRangeException) { rejectedMisalignment = true; }
            Require(rejectedMisalignment,
                "guest reference slot must occupy a naturally 4-byte-aligned field");
            owner.ReleasePersistent(turn, recycled);
            owner.ReleasePersistent(turn, parent);
        }
        Require(owner.Retire() && foreign.Retire(),
            "parent and foreign heap release without cross-actor ref32 escape");
    }

    private static void CheckedRef32SurvivesDynamicBackingGrowth()
    {
        using var actor = new ActorArena(86001, 1, 16384, 512, 8, 2, 2,
            demandBacked: true);
        ActorArenaHandle parent;
        ActorArenaHandle child;
        using (var turn = actor.EnterTurn(86001, 1))
        {
            parent = actor.AllocatePersistent(turn, 16);
            child = actor.AllocatePersistent(turn, 8);
            actor.WriteManagedRef32(turn, parent, 0, child);
            var enlarged = actor.AllocatePersistent(turn, 6000);
            Require(enlarged.Length == 6000 && actor.ReservedNativeBytes == 8192,
                "ref32 field remains four bytes when physical heap grows");
            Require(ReferenceEquals(actor.ReadManagedRef32(turn, parent, 0), child),
                "logical guest field and sidecar proof survive native slab relocation");
        }
        using (var resumed = actor.EnterTurn(86001, 1))
        {
            Require(ReferenceEquals(actor.ReadManagedRef32(resumed, parent, 0), child),
                "carrier-independent logical ref32 remains checked in new actor turn");
        }
        Require(actor.Retire() && actor.ReservedNativeBytes == 0,
            "post-growth actor retirement scrubs and refunds ref32 sidecar + backing");
    }

    private static void DemandBackedGrowthPreservesActorRootsAndBudget()
    {
        var budget = new ActorArenaBudget(10 * 1024);
        long processBefore = ActorArenaBudget.Process.ReservedBytes;
        using var a = new ActorArena(81001, 1, 16 * 1024, 4096, 8, 2, 2,
            budget: budget, demandBacked: true);
        using var b = new ActorArena(81002, 1, 16 * 1024, 4096, 8, 2, 2,
            budget: budget, demandBacked: true);
        using var c = new ActorArena(81003, 1, 16 * 1024, 4096, 8, 2, 2,
            budget: budget, demandBacked: true);
        Require(a.DemandBacked && a.ReservedNativeBytes == 0 &&
                b.ReservedNativeBytes == 0 && c.ReservedNativeBytes == 0 &&
                budget.ReservedBytes == 0,
            "logical actor heaps must not commit or charge maximum at spawn");

        ActorArenaHandle rootA;
        ActorArenaHandle rootB;
        ActorArenaReadLoan loan;
        using (var turnA = a.EnterTurn(81001, 1))
        {
            rootA = a.AllocatePersistent(turnA, 8);
            a.Write(turnA, rootA, 0, new byte[] { 81 });
            loan = a.BorrowRead(turnA, rootA);
        }
        using (var turnB = b.EnterTurn(81002, 1))
        {
            rootB = b.AllocatePersistent(turnB, 8);
            b.Write(turnB, rootB, 0, new byte[] { 82 });
            ExpectInvalid(() => b.Read(turnB, rootA, 0, new byte[1]),
                "foreign actor cannot dereference matching logical offset");
        }
        Require(a.ReservedNativeBytes == 4096 && b.ReservedNativeBytes == 4096 &&
                budget.ReservedBytes == 8192,
            "tenant charge matches two physical slabs rather than both maxima");
        using (var turnC = c.EnterTurn(81003, 1))
            ExpectInvalid(() => c.AllocatePersistent(turnC, 8),
                "aggregate quota denies third actor before native backing allocation");
        Require(c.ReservedNativeBytes == 0 && budget.ReservedBytes == 8192,
            "denied third actor leaves native accounting and contents unchanged");

        using (var turnA = a.EnterTurn(81001, 1))
        {
            ExpectInvalid(() => a.AllocatePersistent(turnA, 6000),
                "growth rejects if physical delta cannot be charged");
            var unchanged = new byte[1];
            a.Read(turnA, rootA, 0, unchanged);
            Require(unchanged[0] == 81 && a.ReservedNativeBytes == 4096,
                "denied growth does not revoke live immutable borrowed root");
        }
        Require(b.Retire(), "unborrowed peer can reclaim independently");
        Require(b.ReservedNativeBytes == 0 && budget.ReservedBytes == 4096,
            "peer release restores tenant capacity");
        using (var turnA = a.EnterTurn(81001, 1))
        {
            var enlarged = a.AllocatePersistent(turnA, 6000);
            var unchanged = new byte[1];
            a.Read(turnA, rootA, 0, unchanged);
            Require(unchanged[0] == 81 && a.ReservedNativeBytes == 8192,
                "demand growth preserves old actor root under a live read loan");
            a.Write(turnA, enlarged, 0, new byte[] { 83 });
            a.Read(turnA, enlarged, 0, unchanged);
            Require(unchanged[0] == 83, "grown allocation is writable by owner");
            ExpectInvalid(() => a.Write(turnA, rootA, 0, new byte[] { 0 }),
                "immutable read loan still forbids mutation after slab relocation");
        }
        Require(!a.Retire(), "loan must delay physically freeing enlarged actor heap");
        Require(budget.ReservedBytes == 8192 && a.ReservedNativeBytes == 8192,
            "logical stop retains charged heap until last borrower exits");
        loan.Dispose();
        Require(a.IsReclaimed && a.ReservedNativeBytes == 0 &&
                budget.ReservedBytes == 0 &&
                ActorArenaBudget.Process.ReservedBytes == processBefore,
            "physical retirement refunds tenant and process exactly once");
        Require(c.Retire() && c.IsReclaimed && c.ReservedNativeBytes == 0,
            "never-allocated actor also retires without a phantom refund");
    }

    private static void DemandBackedGrowthDeniedDuringFfiPin()
    {
        var budget = new ActorArenaBudget(32 * 1024);
        var arena = new ActorArena(83001, 1, 16 * 1024, 1024, 8, 2, 2,
            budget: budget, demandBacked: true);
        ActorArenaHandle root;
        ActorArenaLifetimePin external;
        using (var turn = arena.EnterTurn(83001, 1))
        {
            root = arena.AllocatePersistent(turn, 8);
            arena.Write(turn, root, 0, new byte[] { 49 });
            external = arena.AcquireLifetimePin(turn);
            ExpectInvalid(() => arena.AllocatePersistent(turn, 6000),
                "possible raw FFI pointer makes native slab relocation unsafe");
            Require(arena.ReservedNativeBytes == 4096 && budget.ReservedBytes == 4096,
                "denied growth under pin does not change native commitment");
        }
        external.Dispose();
        using (var resumed = arena.EnterTurn(83001, 1))
        {
            var grown = arena.AllocatePersistent(resumed, 6000);
            var result = new byte[1];
            arena.Read(resumed, root, 0, result);
            Require(result[0] == 49 && grown.Length == 6000,
                "growth becomes safe after last external FFI pin is released");
        }
        Require(arena.Retire() && budget.ReservedBytes == 0,
            "physically released heap is fully refunded after pin and turn exit");
    }

    private static void DemandBackedScratchAndQuiescentPin()
    {
        var budget = new ActorArenaBudget(32 * 1024);
        var arena = new ActorArena(82001, 1, 8 * 1024, 8 * 1024, 8, 2, 2,
            budget: budget, demandBacked: true);
        Require(arena.ReservedNativeBytes == 0, "dynamic scratch starts uncommitted");
        ActorArenaLifetimePin pin;
        ActorScratchHandle stale;
        using (var turn = arena.EnterTurn(82001, 1))
        {
            stale = arena.AllocateScratch(turn, 16, 8);
            arena.WriteScratch(turn, stale, 0, new byte[] { 17 });
            var expanded = arena.AllocateScratch(turn, 5000, 64);
            var buffer = new byte[1];
            arena.ReadScratch(turn, stale, 0, buffer);
            Require(buffer[0] == 17, "scratch growth preserves earlier allocations");
            arena.ReadScratch(turn, expanded, 0, buffer);
            Require(buffer[0] == 0 && arena.ReservedNativeBytes == 8192,
                "fresh grown scratch bytes are zeroed and charged");
            pin = arena.AcquireLifetimePin(turn);
        }
        using (var turn = arena.EnterTurn(82001, 1))
        {
            ExpectInvalid(() => arena.ReadScratch(turn, stale, 0, new byte[1]),
                "scratch handle from previous turn cannot survive an epoch change");
            var scratch = arena.AllocateScratch(turn, 8);
            var bytes = new byte[1];
            arena.ReadScratch(turn, scratch, 0, bytes);
            Require(bytes[0] == 0, "new turn receives scrubbed scratch");
        }
        Require(!arena.Retire() && arena.ReservedNativeBytes == 8192,
            "lifetime-only pin prevents native release after turn exit");
        pin.Dispose();
        Require(arena.IsReclaimed && budget.ReservedBytes == 0,
            "async/FFI lifetime pin defers and then permits physical refund");
    }

    private static void PersistentAndScratchLifetimes()
    {
        using var arena = new ActorArena(
            actorId: 1001, incarnation: 7,
            persistentBytes: 4096, scratchBytes: 512,
            maxAllocations: 16, maxLifetimePins: 8, maxReadLoans: 8);

        ActorArenaHandle persistent;
        ActorScratchHandle scratch;
        using (var turn = arena.EnterTurn(1001, 7))
        {
            persistent = arena.AllocatePersistent(turn, 16, alignment: 8);
            arena.Write(turn, persistent, 0, new byte[] { 1, 2, 3, 4 });

            scratch = arena.AllocateScratch(turn, 16, alignment: 16);
            arena.WriteScratch(turn, scratch, 0, new byte[] { 9, 8, 7, 6 });

            var p = new byte[4];
            var s = new byte[4];
            arena.Read(turn, persistent, 0, p);
            arena.ReadScratch(turn, scratch, 0, s);
            Require(p.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "persistent bytes round trip");
            Require(s.AsSpan().SequenceEqual(new byte[] { 9, 8, 7, 6 }),
                "scratch bytes round trip");

            ExpectInvalid(() => arena.EnterTurn(1001, 7),
                "second simultaneous actor turn");
        }

        using (var resumed = arena.EnterTurn(1001, 7))
        {
            var p = new byte[4];
            arena.Read(resumed, persistent, 0, p);
            Require(p.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "persistent bytes survive carrier/turn rollover");

            ExpectInvalid(() =>
            {
                var one = new byte[1];
                arena.ReadScratch(resumed, scratch, 0, one);
            }, "scratch capability must die at turn exit");

            var freshScratch = arena.AllocateScratch(resumed, 512, alignment: 8);
            var oneByte = new byte[1];
            arena.ReadScratch(resumed, freshScratch, 0, oneByte);
            Require(oneByte[0] == 0, "reused scratch capacity is zeroed");
        }

        ExpectInvalid(() => arena.EnterTurn(9999, 7), "wrong actor id");
        ExpectInvalid(() => arena.EnterTurn(1001, 8), "wrong actor incarnation");

        using var cleanup = arena.EnterTurn(1001, 7);
        arena.ReleasePersistent(cleanup, persistent);
        Require(arena.PersistentLiveBytes == 0 && arena.LiveAllocations == 0,
            "persistent quota refunded after final drop");
    }

    private static void ReadLoanFreezesAllocationAcrossTurns()
    {
        using var arena = new ActorArena(
            actorId: 2001, incarnation: 3,
            persistentBytes: 2048, scratchBytes: 256,
            maxAllocations: 8, maxLifetimePins: 4, maxReadLoans: 4);

        ActorArenaHandle handle;
        ActorArenaReadLoan loan;
        using (var turn = arena.EnterTurn(2001, 3))
        {
            handle = arena.AllocatePersistent(turn, 8);
            arena.Write(turn, handle, 0, new byte[] { 42, 0, 0, 0, 0, 0, 0, 0 });
            loan = arena.BorrowRead(turn, handle);
            Require(arena.OutstandingReadLoans == 1, "read loan charged");
        }

        using (var resumed = arena.EnterTurn(2001, 3))
        {
            var value = new byte[1];
            arena.Read(resumed, handle, 0, value);
            Require(value[0] == 42, "loaned persistent value survives suspension");

            ExpectInvalid(() => arena.Write(resumed, handle, 0, new byte[] { 99 }),
                "live shared/read loan blocks mutation");
            ExpectInvalid(() => arena.ReleasePersistent(resumed, handle),
                "live shared/read loan blocks drop");
        }

        Task.Run(loan.Dispose).GetAwaiter().GetResult();
        loan.Dispose();
        Require(arena.OutstandingReadLoans == 0,
            "off-carrier idempotent read-loan release");

        using var finalTurn = arena.EnterTurn(2001, 3);
        arena.Write(finalTurn, handle, 0, new byte[] { 55 });
        var check = new byte[1];
        arena.Read(finalTurn, handle, 0, check);
        Require(check[0] == 55, "mutation resumes after read loan release");
        arena.ReleasePersistent(finalTurn, handle);
    }


    private static void PairedLoanAndRetirementLeavePeerWritable()
    {
        const int perArena = 192; // 128 persistent + 64 scratch, already slab-aligned.
        var budget = new ActorArenaBudget(2 * perArena);
        using var left = new ActorArena(9101, 1, 128, 64, 4, 2, 2, budget);
        using var right = new ActorArena(9102, 1, 128, 64, 4, 2, 2, budget);
        ActorArenaHandle leftHandle;
        ActorArenaHandle rightHandle;
        using (var turn = left.EnterTurn(9101, 1))
        {
            leftHandle = left.AllocatePersistent(turn, 8);
            left.Write(turn, leftHandle, 0, new byte[] { 111 });
        }
        using (var turn = right.EnterTurn(9102, 1))
        {
            rightHandle = right.AllocatePersistent(turn, 8);
            right.Write(turn, rightHandle, 0, new byte[] { 222 });
        }
        ActorArenaReadLoan loan;
        using (var turn = left.EnterTurn(9101, 1))
        {
            loan = left.BorrowRead(turn, leftHandle);
            ExpectInvalid(() => left.Write(turn, leftHandle, 0, new byte[] { 99 }),
                "A read loan excludes A writes");
            ExpectInvalid(() => left.ReleasePersistent(turn, leftHandle),
                "A read loan excludes A drop");
        }
        try
        {
            using (var turn = right.EnterTurn(9102, 1))
            {
                ExpectInvalid(() => right.Read(turn, leftHandle, 0, new byte[1]),
                    "live foreign A handle cannot be read through B");
                ExpectInvalid(() => right.Write(turn, leftHandle, 0, new byte[] { 99 }),
                    "live foreign A handle cannot be written through B");
                right.Write(turn, rightHandle, 0, new byte[] { 223 });
            }
            Require(!left.Retire() && !left.IsReclaimed,
                "A read loan prevents physical retirement");
            Require(budget.ReservedBytes == 2 * perArena,
                "logical retirement does not refund pinned A or live B");
            using (var turn = right.EnterTurn(9102, 1))
            {
                var value = new byte[1];
                right.Read(turn, rightHandle, 0, value);
                Require(value[0] == 223, "B survives A retirement request");
                right.Write(turn, rightHandle, 0, new byte[] { 224 });
            }
            Task.Run(loan.Dispose).GetAwaiter().GetResult();
            loan.Dispose();
            Require(left.IsReclaimed && budget.ReservedBytes == perArena,
                "last A loan refunds only A exactly once");
            ExpectInvalid(() => left.EnterTurn(9101, 1),
                "reclaimed A cannot admit another turn");
            using (var turn = right.EnterTurn(9102, 1))
            {
                var value = new byte[1];
                right.Read(turn, rightHandle, 0, value);
                Require(value[0] == 224, "B bytes survive A physical reclamation");
                right.Write(turn, rightHandle, 0, new byte[] { 225 });
                right.Read(turn, rightHandle, 0, value);
                Require(value[0] == 225, "B remains writable after A physical reclamation");
            }
            Require(right.Retire() && budget.ReservedBytes == 0,
                "B independently retires and refunds");
        }
        finally
        {
            loan.Dispose();
        }
    }

    private static void LifetimePinDelaysPhysicalReclaim()
    {
        var arena = new ActorArena(
            actorId: 3001, incarnation: 11,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2);

        ActorArenaLifetimePin pin;
        using (var turn = arena.EnterTurn(3001, 11))
        {
            _ = arena.AllocatePersistent(turn, 32);
            pin = arena.AcquireLifetimePin(turn);
        }

        Require(!arena.Retire(), "lifetime pin must delay physical reclaim");
        Require(arena.IsRetired && !arena.IsReclaimed,
            "logical retirement precedes physical reclaim");
        ExpectInvalid(() => arena.EnterTurn(3001, 11),
            "retired arena must reject a new turn");

        Task.Run(pin.Dispose).GetAwaiter().GetResult();
        pin.Dispose();
        Require(arena.IsReclaimed, "last lifetime pin triggers physical reclaim");
        Require(arena.TryReclaim(), "reclaim is idempotent after physical release");
        arena.Dispose();
    }

    private static void AggregateBudgetAdmissionIsNeutral()
    {
        const int reserved = 1280;
        var budget = new ActorArenaBudget(reserved);

        var first = new ActorArena(
            actorId: 3501, incarnation: 1,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2,
            budget: budget);
        Require(first.ReservedNativeBytes == reserved &&
            budget.ReservedBytes == reserved,
            "arena reserves aggregate native capacity before use");

        ExpectInvalid(() => _ = new ActorArena(
            actorId: 3502, incarnation: 1,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2,
            budget: budget),
            "aggregate budget denies second arena");
        Require(budget.ReservedBytes == reserved,
            "failed aggregate admission is accounting-neutral");

        Require(first.Retire(), "quiescent arena reclaims immediately");
        Require(budget.ReservedBytes == 0,
            "physical reclaim refunds aggregate arena capacity");
        first.Dispose();

        var replacement = new ActorArena(
            actorId: 3503, incarnation: 1,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2,
            budget: budget);
        Require(budget.ReservedBytes == reserved,
            "refunded native capacity can be admitted by a new actor");
        replacement.Dispose();
        Require(budget.ReservedBytes == 0,
            "replacement retirement refunds exactly once");

        var tooSmall = new ActorArenaBudget(reserved - 1);
        ExpectInvalid(() => _ = new ActorArena(
            actorId: 3504, incarnation: 1,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2,
            budget: tooSmall),
            "constructor rejected by aggregate budget");
        Require(tooSmall.ReservedBytes == 0,
            "rejected constructor consumes no aggregate reservation");
    }

    private static void RuntimeBudgetsAlsoChargeProcessLimit()
    {
        long processBefore = ActorArenaBudget.Process.ReservedBytes;
        var tenant = new ActorArenaBudget(2048);
        var arena = new ActorArena(
            actorId: 3601, incarnation: 1,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2,
            budget: tenant);

        Require(tenant.ReservedBytes == 1280 &&
                ActorArenaBudget.Process.ReservedBytes == processBefore + 1280,
            "tenant arena must charge tenant and process budgets");

        Require(arena.Retire(), "quiescent actor may reclaim immediately");
        arena.Dispose();
        Require(tenant.ReservedBytes == 0 &&
                ActorArenaBudget.Process.ReservedBytes == processBefore,
            "physical reclamation refunds both budgets exactly once");
    }

    private static void PersistentRootWithTailTempsDoesNotExhaustArena()
    {
        using var arena = new ActorArena(
            actorId: 9701, incarnation: 1,
            persistentBytes: 128, scratchBytes: 64,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2);

        ActorArenaHandle root;
        using (var turn = arena.EnterTurn(9701, 1))
        {
            root = arena.AllocatePersistent(turn, 8, alignment: 8);
            arena.Write(turn, root, 0, new byte[] { 0x63 });
        }

        for (int i = 0; i < 2048; i++)
        {
            using var turn = arena.EnterTurn(9701, 1);
            var temporary = arena.AllocatePersistent(turn, 64, alignment: 8);
            arena.Write(turn, temporary, 0, new byte[] { 0xCD });
            arena.ReleasePersistent(turn, temporary);

            var check = new byte[1];
            arena.Read(turn, root, 0, check);
            Require(check[0] == 0x63, "persistent root survives transient tail release");
            Require(arena.LiveAllocations == 1 && arena.PersistentLiveBytes == 8,
                "only the persistent root retains actor-owned bytes");
        }

        using (var turn = arena.EnterTurn(9701, 1))
            arena.ReleasePersistent(turn, root);
        Require(arena.LiveAllocations == 0 && arena.PersistentLiveBytes == 0,
            "root finally drops without leaving transient arena allocations");
    }

    private static void RetirementSealsAdmissionButAllowsTurnCleanup()
    {
        var arena = new ActorArena(
            actorId: 4001, incarnation: 2,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2);

        using (var turn = arena.EnterTurn(4001, 2))
        {
            var handle = arena.AllocatePersistent(turn, 16);
            arena.Write(turn, handle, 0, new byte[] { 1, 2, 3, 4 });

            Require(!arena.Retire(), "active turn must delay reclaim");
            ExpectInvalid(() => arena.AllocatePersistent(turn, 8),
                "retirement seals new persistent allocation");
            ExpectInvalid(() => arena.AllocateScratch(turn, 8),
                "retirement seals new scratch allocation");
            ExpectInvalid(() => arena.AcquireLifetimePin(turn),
                "retirement seals new lifetime pins");
            ExpectInvalid(() => arena.BorrowRead(turn, handle),
                "retirement seals new read loans");

            var bytes = new byte[4];
            arena.Read(turn, handle, 0, bytes);
            Require(bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "existing turn can finish cleanup after logical retirement");
            arena.ReleasePersistent(turn, handle);
        }

        Require(arena.IsReclaimed,
            "turn exit after retirement performs quiescent physical reclaim");
        arena.Dispose();
    }

    private static void CrossArenaAuthorityAndGenerationFences()
    {
        using var left = new ActorArena(
            actorId: 5001, incarnation: 1,
            persistentBytes: 1024, scratchBytes: 128,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2);
        using var right = new ActorArena(
            actorId: 5001, incarnation: 1,
            persistentBytes: 1024, scratchBytes: 128,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2);

        ActorArenaHandle stale;
        using (var leftTurn = left.EnterTurn(5001, 1))
        {
            stale = left.AllocatePersistent(leftTurn, 8);
            left.Write(leftTurn, stale, 0,
                new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 });
            left.ReleasePersistent(leftTurn, stale);

            var replacement = left.AllocatePersistent(leftTurn, 8);
            var zeroed = new byte[8];
            left.Read(leftTurn, replacement, 0, zeroed);
            Require(zeroed.All(static b => b == 0),
                "reused persistent capacity is scrubbed");

            ExpectInvalid(() =>
            {
                var value = new byte[1];
                left.Read(leftTurn, stale, 0, value);
            }, "stale generation replay");

            left.ReleasePersistent(leftTurn, replacement);
        }

        using var rightTurn = right.EnterTurn(5001, 1);
        ExpectInvalid(() =>
        {
            var value = new byte[1];
            right.Read(rightTurn, stale, 0, value);
        }, "same numeric actor identity cannot cross arena authority");
    }


    private static void ForgedMetadataCannotAcquireMemoryAuthority()
    {
        using var arena = new ActorArena(
            actorId: 6001, incarnation: 2,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2);

        using (var turn = arena.EnterTurn(6001, 2))
        {
            var persistent = arena.AllocatePersistent(turn, 8);
            var scratch = arena.AllocateScratch(turn, 8);
            arena.Write(turn, persistent, 0, new byte[] { 0x51 });
            arena.WriteScratch(turn, scratch, 0, new byte[] { 0xA3 });

            // Clones preserve *all* private numeric metadata, including the
            // arena Guid. They must not be accepted as independently issued
            // authority. This is defense in depth for trusted host boundaries.
            var forgedPersistent = CloneWithoutIssuance(persistent);
            var forgedScratch = CloneWithoutIssuance(scratch);
            Require(!ReferenceEquals(persistent, forgedPersistent) &&
                    !ReferenceEquals(scratch, forgedScratch),
                "reflection created separate matching-metadata objects");

            ExpectInvalid(() => arena.Read(turn, forgedPersistent, 0, new byte[1]),
                "reconstructed persistent capability read");
            ExpectInvalid(() => arena.Write(turn, forgedPersistent, 0, new byte[] { 1 }),
                "reconstructed persistent capability write");
            ExpectInvalid(() => arena.ReleasePersistent(turn, forgedPersistent),
                "reconstructed persistent capability drop");
            ExpectInvalid(() => arena.BorrowRead(turn, forgedPersistent),
                "reconstructed persistent capability borrow");

            ExpectInvalid(() => arena.ReadScratch(turn, forgedScratch, 0, new byte[1]),
                "reconstructed scratch capability read");
            ExpectInvalid(() => arena.WriteScratch(turn, forgedScratch, 0, new byte[] { 1 }),
                "reconstructed scratch capability write");

            byte[] read = new byte[1];
            arena.Read(turn, persistent, 0, read);
            Require(read[0] == 0x51, "original persistent capability remains valid");
            arena.ReadScratch(turn, scratch, 0, read);
            Require(read[0] == 0xA3, "original scratch capability remains valid");
            arena.ReleasePersistent(turn, persistent);
            Require(arena.LiveAllocations == 0 && arena.PersistentLiveBytes == 0,
                "forgery rejection does not corrupt arena accounting");
        }
    }


    private static void ActiveTurnRequiresExactTokenAndCarrier()
    {
        using var arena = new ActorArena(
            actorId: 7001, incarnation: 9,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2);

        var turn = arena.EnterTurn(7001, 9);
        var persistent = arena.AllocatePersistent(turn, 8);
        arena.Write(turn, persistent, 0, new byte[] { 0x54 });

        var forgedTurn = CloneWithoutIssuance(turn);
        ExpectInvalid(() => arena.Read(forgedTurn, persistent, 0, new byte[1]),
            "metadata-equivalent turn cannot read actor memory");
        ExpectInvalid(forgedTurn.Dispose,
            "metadata-equivalent turn cannot end the issued physical turn");

        OnAnotherCarrier(() =>
        {
            ExpectInvalid(() => arena.Read(turn, persistent, 0, new byte[1]),
                "stolen turn cannot read bytes on a second carrier");
            ExpectInvalid(() => arena.AllocateScratch(turn, 8),
                "stolen turn cannot allocate scratch on a second carrier");
            ExpectInvalid(turn.Dispose,
                "off-carrier Dispose cannot close the physical turn");
        });

        var check = new byte[1];
        arena.Read(turn, persistent, 0, check);
        Require(check[0] == 0x54, "off-carrier denial preserves owner turn");
        ExpectInvalid(() => arena.EnterTurn(7001, 9),
            "off-carrier Dispose must not prematurely release the live turn");

        turn.Dispose();
        // Idempotent release of an already-closed turn is safe on other carriers.
        OnAnotherCarrier(turn.Dispose);

        OnAnotherCarrier(() =>
        {
            using var migrated = arena.EnterTurn(7001, 9);
            var bytes = new byte[1];
            arena.Read(migrated, persistent, 0, bytes);
            Require(bytes[0] == 0x54, "fresh physical turn can migrate carriers");
            arena.ReleasePersistent(migrated, persistent);
        });
    }

    private static void OnAnotherCarrier(Action action)
    {
        // Task.Run followed by a synchronous wait may inline on the waiting
        // pool thread. These checks require a genuinely different carrier.
        int owner = Environment.CurrentManagedThreadId;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Require(Environment.CurrentManagedThreadId != owner,
                    "cross-carrier fixture must use a distinct physical thread");
                action();
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        worker.Start();
        Require(worker.Join(TimeSpan.FromSeconds(10)), "cross-carrier fixture timed out");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void ForgedLoanAndPinCannotPrematurelyReclaim()
    {
        using var arena = new ActorArena(
            actorId: 8001, incarnation: 1,
            persistentBytes: 1024, scratchBytes: 256,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2);

        ActorArenaReadLoan loan;
        ActorArenaLifetimePin pin;
        ActorArenaHandle handle;
        using (var turn = arena.EnterTurn(8001, 1))
        {
            handle = arena.AllocatePersistent(turn, 8);
            loan = arena.BorrowRead(turn, handle);
            pin = arena.AcquireLifetimePin(turn);
            var fakeLoan = CloneWithoutIssuance(loan);
            var fakePin = CloneWithoutIssuance(pin);

            ExpectInvalid(fakeLoan.Dispose,
                "metadata-equivalent read loan cannot release a legitimate loan");
            ExpectInvalid(fakePin.Dispose,
                "metadata-equivalent lifetime pin cannot release a legitimate pin");
            Require(arena.OutstandingReadLoans == 1 &&
                    arena.OutstandingLifetimePins == 1,
                "forged release must leave both lifetime counters unchanged");
            ExpectInvalid(() => arena.Write(turn, handle, 0, new byte[] { 7 }),
                "legitimate read loan still blocks mutation after forged release");
            ExpectInvalid(() => arena.ReleasePersistent(turn, handle),
                "legitimate read loan still blocks drop after forged release");
        }

        Require(!arena.Retire(), "legitimate loan and pin delay retired arena reclaim");
        Task.Run(loan.Dispose).GetAwaiter().GetResult();
        Require(!arena.IsReclaimed &&
                arena.OutstandingReadLoans == 0 &&
                arena.OutstandingLifetimePins == 1,
            "live pin alone still delays native reclamation");

        Task.Run(pin.Dispose).GetAwaiter().GetResult();
        Require(arena.IsReclaimed, "last legitimate pin completes native reclamation");
        loan.Dispose();
        pin.Dispose();
    }

    private static T CloneWithoutIssuance<T>(T issued) where T : class
    {
        MethodInfo? clone = typeof(object).GetMethod(
            "MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        if (clone is null)
            throw new Exception("Reflection clone regression setup unavailable");
        return (T)(clone.Invoke(issued, null) ??
            throw new Exception("Reflection clone regression setup returned null"));
    }

    private static void ExpectInvalid(Action action, string scenario)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Exception("Actor arena unexpectedly admitted: " + scenario);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor arena regression: " + scenario);
    }
}

