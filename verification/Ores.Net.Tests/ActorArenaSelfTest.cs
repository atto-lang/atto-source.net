using Ores.Net.Runtime;
using System.Reflection;

internal static class ActorArenaSelfTest
{
    public static void Run()
    {
        PersistentAndScratchLifetimes();
        ReadLoanFreezesAllocationAcrossTurns();
        LifetimePinDelaysPhysicalReclaim();
        AggregateBudgetAdmissionIsNeutral();
        RuntimeBudgetsAlsoChargeProcessLimit();
        PersistentRootWithTailTempsDoesNotExhaustArena();
        OutOfOrderDropsCoalesceDeadTail();
        RetirementSealsAdmissionButAllowsTurnCleanup();
        CrossArenaAuthorityAndGenerationFences();
        ForgedMetadataCannotAcquireMemoryAuthority();
        ActiveTurnRequiresExactTokenAndCarrier();
        ForgedLoanAndPinCannotPrematurelyReclaim();
        Console.WriteLine("PASS .NET actor-owned unmanaged arena, loans, pins and quiescent reclaim");
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

    private static void OutOfOrderDropsCoalesceDeadTail()
    {
        using var arena = new ActorArena(
            actorId: 9702, incarnation: 1,
            persistentBytes: 96, scratchBytes: 64,
            maxAllocations: 4, maxLifetimePins: 2, maxReadLoans: 2);
        using var turn = arena.EnterTurn(9702, 1);
        var root = arena.AllocatePersistent(turn, 8);
        arena.Write(turn, root, 0, new byte[] { 0x74 });
        var middle = arena.AllocatePersistent(turn, 40);
        var tail = arena.AllocatePersistent(turn, 40);
        arena.Write(turn, middle, 0, new byte[] { 0xA5 });
        arena.Write(turn, tail, 0, new byte[] { 0xC7 });

        // Drop the older middle allocation before the newest allocation.
        // Rewinding only to the newest allocation's start leaves 40 dead
        // bytes stranded above the surviving permanent root.
        arena.ReleasePersistent(turn, middle);
        arena.ReleasePersistent(turn, tail);
        var replacement = arena.AllocatePersistent(turn, 80);
        var zeroed = new byte[80];
        arena.Read(turn, replacement, 0, zeroed);
        Require(zeroed.All(static value => value == 0),
            "coalesced tail reallocation cannot reveal freed bytes");

        var rootCheck = new byte[1];
        arena.Read(turn, root, 0, rootCheck);
        Require(rootCheck[0] == 0x74,
            "tail coalescing cannot move or overwrite live actor roots");
        arena.ReleasePersistent(turn, replacement);
        arena.ReleasePersistent(turn, root);
        Require(arena.PersistentLiveBytes == 0 && arena.LiveAllocations == 0,
            "out-of-order tail cleanup refunds all persistent allocation state");
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

        Task.Run(() =>
        {
            ExpectInvalid(() => arena.Read(turn, persistent, 0, new byte[1]),
                "stolen turn cannot read bytes on a second carrier");
            ExpectInvalid(() => arena.AllocateScratch(turn, 8),
                "stolen turn cannot allocate scratch on a second carrier");
            ExpectInvalid(turn.Dispose,
                "off-carrier Dispose cannot close the physical turn");
        }).GetAwaiter().GetResult();

        var check = new byte[1];
        arena.Read(turn, persistent, 0, check);
        Require(check[0] == 0x54, "off-carrier denial preserves owner turn");
        ExpectInvalid(() => arena.EnterTurn(7001, 9),
            "off-carrier Dispose must not prematurely release the live turn");

        turn.Dispose();
        // Idempotent release of an already-closed turn is safe on other carriers.
        Task.Run(turn.Dispose).GetAwaiter().GetResult();

        Task.Run(() =>
        {
            using var migrated = arena.EnterTurn(7001, 9);
            var bytes = new byte[1];
            arena.Read(migrated, persistent, 0, bytes);
            Require(bytes[0] == 0x54, "fresh physical turn can migrate carriers");
            arena.ReleasePersistent(migrated, persistent);
        }).GetAwaiter().GetResult();
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
