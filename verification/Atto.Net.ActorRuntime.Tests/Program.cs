using Atto.Net.ActorRuntime;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class Program
{
    private static int _cases;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAILED: " + message);
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        bool threw = false;
        try { action(); }
        catch (T) { threw = true; }
        Check(threw, message);
        _cases++;
    }

    private static void FrameTurnAndTenantFences()
    {
        using var tenant = new TenantVm(maximumActors: 3, maximumNativeBytes: 512);
        using var otherTenant = new TenantVm(maximumActors: 1, maximumNativeBytes: 128);
        using var actor = tenant.CreateActor(arenaBytes: 128, maximumFrames: 4);
        using var sibling = tenant.CreateActor(arenaBytes: 128, maximumFrames: 4);
        using var foreign = otherTenant.CreateActor(arenaBytes: 128);

        var turn1 = actor.BeginTurn();
        var siblingTurn = sibling.BeginTurn();
        var foreignTurn = foreign.BeginTurn();
        var frame = actor.PushFrame(turn1, 16);
        Expect<NotSupportedException>(
            () => JsonSerializer.Serialize(turn1, NoWireJsonContext.Default.ActorTurn),
            "turn authority JSON serialize denied");
        Expect<NotSupportedException>(
            () => JsonSerializer.Serialize(frame, NoWireJsonContext.Default.ActorFrameHandle),
            "frame authority JSON serialize denied");
        Expect<NotSupportedException>(
            () => JsonSerializer.Deserialize("{}", NoWireJsonContext.Default.ActorTurn),
            "turn authority JSON deserialize denied");
        Expect<NotSupportedException>(
            () => JsonSerializer.Deserialize("{}", NoWireJsonContext.Default.ActorFrameHandle),
            "frame authority JSON deserialize denied");
        actor.WriteFrame(turn1, frame, 0, new byte[] { 42, 17 });
        Exception? offCarrierRead = null;
        Exception? offCarrierExit = null;
        var otherCarrier = new Thread(() =>
        {
            try { actor.ReadFrame(turn1, frame, 0, new byte[2]); }
            catch (Exception ex) { offCarrierRead = ex; }
            try { actor.EndTurn(turn1); }
            catch (Exception ex) { offCarrierExit = ex; }
        });
        otherCarrier.Start();
        Check(otherCarrier.Join(millisecondsTimeout: 10_000),
            "foreign carrier probe completed");
        Check(offCarrierRead is InvalidOperationException &&
              offCarrierExit is InvalidOperationException,
            "turn capability cannot migrate or terminate on another carrier");
        _cases++;
        Expect<InvalidOperationException>(
            () => sibling.ReadFrame(siblingTurn, frame, 0, new byte[2]),
            "cross-actor frame read denied");
        Expect<InvalidOperationException>(
            () => foreign.WriteFrame(foreignTurn, frame, 0, new byte[] { 9 }),
            "cross-tenant frame write denied");
        Expect<InvalidOperationException>(
            () => actor.ReadFrame(siblingTurn, frame, 0, new byte[2]),
            "foreign turn denied");
        actor.EndTurn(turn1);

        var turn2 = actor.BeginTurn();
        var bytes = new byte[2];
        actor.ReadFrame(turn2, frame, 0, bytes);
        Check(bytes[0] == 42 && bytes[1] == 17, "frames persist across valid physical turns");
        Expect<InvalidOperationException>(
            () => actor.ReadFrame(turn1, frame, 0, new byte[2]),
            "old physical turn cannot read resumed frame");
        actor.PopFrame(turn2, frame);
        Expect<InvalidOperationException>(
            () => actor.ReadFrame(turn2, frame, 0, new byte[1]),
            "popped capability cannot be replayed");
        var reused = actor.PushFrame(turn2, 16);
        var zero = new byte[16];
        actor.ReadFrame(turn2, reused, 0, zero);
        Check(zero.All(b => b == 0), "reused arena frame is scrubbed");
        actor.PopFrame(turn2, reused);
        actor.EndTurn(turn2);
        Expect<InvalidOperationException>(
            () => actor.EndTurn(turn2),
            "physical turn exit cannot be replayed");
        sibling.EndTurn(siblingTurn);
        foreign.EndTurn(foreignTurn);
        actor.Stop();
        sibling.Stop();
        foreign.Stop();
        Check(tenant.NativeReservedBytes == 0 && otherTenant.NativeReservedBytes == 0,
            "all quiescent actor memory refunded once");
        _cases++;
    }

    private static void SealedScalarAndCorruption()
    {
        using var tenant = new TenantVm(maximumActors: 2, maximumNativeBytes: 256);
        using var actor = tenant.CreateActor(arenaBytes: 128);
        using var sibling = tenant.CreateActor(arenaBytes: 128);
        var turn = actor.BeginTurn();
        var frame = actor.PushFrame(turn, 16);
        ActorScalarKind[] declared = [ActorScalarKind.I64, ActorScalarKind.Bool];
        actor.SealScalarSchema(turn, frame, declared, blockCount: 3);
        declared[0] = ActorScalarKind.Bool; // caller mutation cannot retype.
        actor.WriteI64(turn, frame, 0, long.MinValue);
        actor.WriteBool(turn, frame, 1, true);
        Check(actor.ReadI64(turn, frame, 0) == long.MinValue, "sealed i64 preserved");
        Check(actor.ReadBool(turn, frame, 1), "canonical bool preserved");
        Check(actor.ReadSealedBlockCount(turn, frame) == 3, "verified CFG blocks sealed");
        Expect<InvalidOperationException>(
            () => actor.WriteBool(turn, frame, 0, false),
            "caller cannot forge sealed scalar kind");
        Expect<InvalidOperationException>(
            () => actor.SealScalarSchema(turn, frame, declared, 4),
            "schema cannot be resealed");

        // Raw-byte APIs must not bypass a sealed frame's typed ABI.
        Expect<ActorResourceExhaustedException>(
            () => actor.WriteFrame(turn, frame, 9, new byte[] { 2 }),
            "untyped writes into sealed scalar frame are terminal");
        Expect<ActorResourceExhaustedException>(
            () => actor.EndTurn(turn),
            "caught sealed-frame bypass remains terminal at dispatcher exit");
        Check(actor.NativeReservedBytes == 0, "poisoned actor arena reclaimed");

        using (var rawReader = tenant.CreateActor(arenaBytes: 128))
        {
            var readTurn = rawReader.BeginTurn();
            var readFrame = rawReader.PushFrame(readTurn, 8);
            rawReader.SealScalarSchema(readTurn, readFrame,
                [ActorScalarKind.Bool], 1);
            Expect<ActorResourceExhaustedException>(
                () => rawReader.ReadFrame(readTurn, readFrame, 0, new byte[1]),
                "untyped reads of sealed bool data are terminal");
            Expect<ActorResourceExhaustedException>(
                () => rawReader.EndTurn(readTurn),
                "caught raw-read bypass is terminal at dispatcher exit");
        }

        // Before sealing, raw initialization is allowed. The seal itself
        // must validate canonical bool data, not defer failure until read.
        using (var malformed = tenant.CreateActor(arenaBytes: 128))
        {
            var corruptTurn = malformed.BeginTurn();
            var corruptFrame = malformed.PushFrame(corruptTurn, 8);
            malformed.WriteFrame(corruptTurn, corruptFrame, 0,
                new byte[] { 1, 7 });
            Expect<ActorResourceExhaustedException>(
                () => malformed.SealScalarSchema(corruptTurn, corruptFrame,
                    [ActorScalarKind.Bool], 1),
                "sealing rejects nonzero boolean padding");
            Expect<ActorResourceExhaustedException>(
                () => malformed.EndTurn(corruptTurn),
                "caught pre-seal corruption cannot complete dispatch");
        }
        Check(tenant.NativeReservedBytes == 128,
            "only healthy sibling actor remains allocated");
        var goodTurn = sibling.BeginTurn();
        var goodFrame = sibling.PushFrame(goodTurn, 8);
        sibling.SealScalarSchema(goodTurn, goodFrame, [ActorScalarKind.Bool], 1);
        sibling.WriteBool(goodTurn, goodFrame, 0, true);
        Check(sibling.ReadBool(goodTurn, goodFrame, 0), "healthy sibling survives");
        sibling.PopFrame(goodTurn, goodFrame);
        sibling.EndTurn(goodTurn);
        sibling.Stop();
        _cases++;
    }

    private static void TerminalQuotaAndSwallowedFailure()
    {
        using var tenant = new TenantVm(maximumActors: 3, maximumNativeBytes: 192);
        using var depthActor = tenant.CreateActor(arenaBytes: 64, maximumFrames: 1);
        using var byteActor = tenant.CreateActor(arenaBytes: 64, maximumFrames: 4);
        using var sibling = tenant.CreateActor(arenaBytes: 64, maximumFrames: 2);

        Expect<ActorResourceExhaustedException>(
            () => tenant.CreateActor(arenaBytes: 64),
            "tenant aggregate/actor admission denied");

        var depthTurn = depthActor.BeginTurn();
        depthActor.PushFrame(depthTurn, 8);
        Expect<ActorResourceExhaustedException>(
            () => depthActor.PushFrame(depthTurn, 8),
            "frame-count exhaustion is terminal");
        Expect<ActorResourceExhaustedException>(
            () => depthActor.EndTurn(depthTurn),
            "catching depth quota cannot complete actor turn");
        Check(depthActor.NativeReservedBytes == 0, "terminal depth actor reclaimed");

        var byteTurn = byteActor.BeginTurn();
        byteActor.PushFrame(byteTurn, 48);
        Expect<ActorResourceExhaustedException>(
            () => byteActor.PushFrame(byteTurn, 24),
            "aggregate frame bytes exhaustion is terminal");
        Expect<ActorResourceExhaustedException>(
            () => byteActor.EndTurn(byteTurn),
            "caught byte quota cannot complete actor turn");
        Check(byteActor.NativeReservedBytes == 0, "terminal byte actor reclaimed");

        var siblingTurn = sibling.BeginTurn();
        var siblingFrame = sibling.PushFrame(siblingTurn, 8);
        sibling.WriteFrame(siblingTurn, siblingFrame, 0, new byte[] { 55 });
        var got = new byte[1];
        sibling.ReadFrame(siblingTurn, siblingFrame, 0, got);
        Check(got[0] == 55, "healthy actor survives two terminal siblings");
        sibling.PopFrame(siblingTurn, siblingFrame);
        sibling.EndTurn(siblingTurn);
        sibling.Stop();
        Check(tenant.NativeReservedBytes == 0 && tenant.LiveActorCount == 0,
            "all native quota refunded");
        _cases++;
    }

    private static void StopQuiescenceAndGeneration()
    {
        using var tenant = new TenantVm(maximumActors: 1, maximumNativeBytes: 64);
        using var actor = tenant.CreateActor(arenaBytes: 64);
        var turn = actor.BeginTurn();
        var oldFrame = actor.PushFrame(turn, 8);
        actor.Stop();
        Check(tenant.NativeReservedBytes == 64, "STOP must not free active turn");
        Expect<InvalidOperationException>(
            () => actor.ReadFrame(turn, oldFrame, 0, new byte[1]),
            "STOP seals new frame access");
        Expect<ActorResourceExhaustedException>(
            () => tenant.CreateActor(arenaBytes: 64),
            "replacement denied until physical quiescence");
        actor.EndTurn(turn);
        Check(tenant.NativeReservedBytes == 0, "STOP refunds after physical turn");
        using var reincarnated = tenant.CreateActor(arenaBytes: 64);
        var nextTurn = reincarnated.BeginTurn();
        Expect<InvalidOperationException>(
            () => reincarnated.ReadFrame(nextTurn, oldFrame, 0, new byte[1]),
            "old incarnation handle is not authority in replacement");
        reincarnated.EndTurn(nextTurn);
        reincarnated.Stop();
        _cases++;
    }

    private static void PersistentPollAndFairness()
    {
        using var tenant = new TenantVm(maximumActors: 3, maximumNativeBytes: 192);
        using var actor = tenant.CreateActor(arenaBytes: 64, logicalFuel: 1000);
        int polls = 0;
        for (int dispatch = 0; dispatch < 4; dispatch++)
        {
            var turn = actor.BeginTurn();
            Check(actor.MeterSnapshot(turn).BackedgesThisDispatch == 0,
                "dispatch fairness resets");
            for (int i = 0; i < 127; i++)
                if (actor.ChargeBackedge(turn).PollDue) polls++;
            var snapshot = actor.MeterSnapshot(turn);
            Check(snapshot.PollPhase == (uint)(((dispatch + 1) * 127) % 128),
                "poll phase survives repeated 127-operation turns");
            Check(snapshot.RemainingFuel == (ulong)(1000 - (dispatch + 1) * 127),
                "hard fuel never refreshes on dispatch");
            actor.EndTurn(turn);
        }
        Check(polls == 3, "508 committed reductions trigger three slow polls");

        var next = actor.BeginTurn();
        var before = actor.MeterSnapshot(next);
        Expect<ArgumentOutOfRangeException>(
            () => actor.ChargeOperation(next, 129),
            "oversized charge refused before fuel debit");
        Check(actor.MeterSnapshot(next).Equals(before),
            "invalid weighted charge is failure atomic");
        actor.ChargeOperation(next, 4);
        Check(actor.MeterSnapshot(next).PollPhase == 0,
            "weighted charge crosses retained poll phase");
        actor.EndTurn(next);

        using var fair = tenant.CreateActor(arenaBytes: 64, logicalFuel: 1000);
        var fairTurn = fair.BeginTurn();
        GuestChargeResult last = default;
        for (int i = 0; i < 512; i++)
            last = fair.ChargeBackedge(fairTurn);
        Check(last.DispatchFairnessExhausted && last.PollDue,
            "512 backedges expire fairness and hit 128-unit poll");
        fair.EndTurn(fairTurn);
        var freshTurn = fair.BeginTurn();
        Check(fair.MeterSnapshot(freshTurn).BackedgesThisDispatch == 0 &&
              fair.MeterSnapshot(freshTurn).RemainingFuel == 488,
            "only fairness resets; fuel remains charged");
        fair.EndTurn(freshTurn);

        using var exhausted = tenant.CreateActor(arenaBytes: 64, logicalFuel: 1);
        var chargedTurn = exhausted.BeginTurn();
        exhausted.ChargeBackedge(chargedTurn);
        exhausted.EndTurn(chargedTurn);
        var exhaustedTurn = exhausted.BeginTurn();
        Expect<ActorResourceExhaustedException>(
            () => exhausted.ChargeBackedge(exhaustedTurn),
            "exhausted tenant fuel terminates offender");
        Expect<ActorResourceExhaustedException>(
            () => exhausted.EndTurn(exhaustedTurn),
            "caught fuel failure does not complete actor turn");
        Check(exhausted.NativeReservedBytes == 0, "exhausted actor reclaimed");
        var siblingTurn = fair.BeginTurn();
        Check(fair.MeterSnapshot(siblingTurn).RemainingFuel == 488,
            "neighboring actor has independent fuel");
        fair.EndTurn(siblingTurn);
        actor.Stop();
        fair.Stop();
        _cases++;
    }


    private static void LogicalFramesSurviveCarrierMigration()
    {
        using var tenant = new TenantVm(maximumActors: 1, maximumNativeBytes: 64);
        using var actor = tenant.CreateActor(arenaBytes: 64);
        var first = actor.BeginTurn();
        var frame = actor.PushFrame(first, 8);
        actor.SealScalarSchema(first, frame, [ActorScalarKind.I64], 1);
        actor.WriteI64(first, frame, 0, 123456789);
        actor.EndTurn(first);

        Exception? failure = null;
        var nextCarrier = new Thread(() =>
        {
            try
            {
                var next = actor.BeginTurn();
                Check(actor.ReadI64(next, frame, 0) == 123456789,
                    "new physical carrier can recover actor-owned frame");
                actor.PopFrame(next, frame);
                actor.EndTurn(next);
            }
            catch (Exception ex) { failure = ex; }
        });
        nextCarrier.Start();
        Check(nextCarrier.Join(millisecondsTimeout: 10_000),
            "actor migrated turn finishes on second physical carrier");
        if (failure is not null)
            throw new Exception("Cross-turn carrier migration failed", failure);
        actor.Stop();
        Check(tenant.NativeReservedBytes == 0,
            "carrier migration retains exact arena lifetime and refund");
        _cases++;
    }

    private static void LongRunPollCannotBeEvaded()
    {
        // A malicious actor that yields after 127 reductions must not receive
        // a fresh polling phase. 256 dispatches exercise 32,512 reductions.
        using var tenant = new TenantVm(maximumActors: 1, maximumNativeBytes: 64);
        using var actor = tenant.CreateActor(arenaBytes: 64, logicalFuel: 35_000);
        int polls = 0;
        for (int dispatch = 0; dispatch < 256; dispatch++)
        {
            var turn = actor.BeginTurn();
            for (int i = 0; i < 127; i++)
                if (actor.ChargeBackedge(turn).PollDue) polls++;
            actor.EndTurn(turn);
        }
        var lastTurn = actor.BeginTurn();
        var snapshot = actor.MeterSnapshot(lastTurn);
        Check(polls == 254 && snapshot.PollPhase == 0,
            "256 short turns must accumulate all 254 polls");
        Check(snapshot.RemainingFuel == 2_488,
            "32,512 logical reductions permanently charged");
        Check(actor.ChargeBackedge(lastTurn, 128).PollDue,
            "maximum legal charge produces one checkpoint");
        Check(actor.MeterSnapshot(lastTurn).RemainingFuel == 2_360,
            "weighted charge reduces persistent fuel");
        actor.EndTurn(lastTurn);
        actor.Stop();
        _cases++;
    }

    private static void QuiescentTenantDisposal()
    {
        var tenant = new TenantVm(maximumActors: 1, maximumNativeBytes: 64);
        var actor = tenant.CreateActor(arenaBytes: 64);
        var active = actor.BeginTurn();
        var frame = actor.PushFrame(active, 8);
        tenant.Dispose();
        Check(tenant.NativeReservedBytes == 64,
            "tenant disposal must not free a live physical turn");
        Expect<ObjectDisposedException>(
            () => tenant.CreateActor(arenaBytes: 64),
            "disposed tenant refuses new actor admission");
        Expect<InvalidOperationException>(
            () => actor.ReadFrame(active, frame, 0, new byte[1]),
            "tenant disposal immediately revokes live turn data access");
        actor.EndTurn(active);
        Check(tenant.NativeReservedBytes == 0 && tenant.LiveActorCount == 0,
            "in-flight turn completion refunds tenant memory exactly once");
        actor.Stop();
        tenant.Dispose();
        _cases++;
    }

    private static void InvalidAdmissionIsAtomic()
    {
        using var tenant = new TenantVm(maximumActors: 1, maximumNativeBytes: 64);
        Expect<ArgumentOutOfRangeException>(
            () => tenant.CreateActor(arenaBytes: 0),
            "zero-capacity actor rejected without budget charge");
        Expect<ArgumentOutOfRangeException>(
            () => tenant.CreateActor(arenaBytes: 64, maximumFrames: 0),
            "zero-frame actor rejected without budget charge");
        Expect<ArgumentOutOfRangeException>(
            () => tenant.CreateActor(arenaBytes: 64, logicalFuel: 0),
            "zero-fuel actor rejected without budget charge");
        Check(tenant.NativeReservedBytes == 0 && tenant.LiveActorCount == 0,
            "failed admissions do not consume tenant quota");
        using var actor = tenant.CreateActor(arenaBytes: 64);
        var turn = actor.BeginTurn();
        var before = actor.MeterSnapshot(turn);
        ulong tenantBefore = tenant.RemainingLogicalFuel;
        Expect<ArgumentOutOfRangeException>(
            () => actor.ChargeBackedge(turn, 0),
            "zero compiler charge is invalid");
        Expect<ArgumentOutOfRangeException>(
            () => actor.ChargeOperation(turn, 129),
            "oversized compiler charge is invalid");
        Check(actor.MeterSnapshot(turn).Equals(before) &&
              tenant.RemainingLogicalFuel == tenantBefore,
            "invalid host charge cannot debit actor or tenant fuel");
        var frame = actor.PushFrame(turn, 8);
        Expect<ArgumentOutOfRangeException>(
            () => actor.PushFrame(turn, 8, alignment: 3),
            "invalid alignment refused before frame publication");
        Check(actor.FrameDepth == 1,
            "invalid frame alignment does not publish any frame");
        actor.PopFrame(turn, frame);
        actor.EndTurn(turn);
        actor.Stop();
        _cases++;
    }

    private static void TenantFuelSurvivesActorReincarnation()
    {
        using var tenant = new TenantVm(maximumActors: 1,
            maximumNativeBytes: 64, maximumTotalLogicalFuel: 256);
        for (int generation = 0; generation < 2; generation++)
        {
            using var actor = tenant.CreateActor(arenaBytes: 64,
                logicalFuel: 10_000);
            var turn = actor.BeginTurn();
            Check(actor.ChargeBackedge(turn, 128).PollDue,
                "each 128-unit charge reaches a slow poll");
            actor.EndTurn(turn);
            actor.Stop();
            Check(tenant.RemainingLogicalFuel == (ulong)(128 - generation * 128),
                "tenant fuel never refunds when actor is retired");
        }
        using var finalActor = tenant.CreateActor(arenaBytes: 64,
            logicalFuel: 10_000);
        var finalTurn = finalActor.BeginTurn();
        Expect<ActorResourceExhaustedException>(
            () => finalActor.ChargeBackedge(finalTurn),
            "reincarnation cannot mint new tenant-wide execution fuel");
        Expect<ActorResourceExhaustedException>(
            () => finalActor.EndTurn(finalTurn),
            "caught tenant fuel exhaustion remains terminal");
        Check(tenant.RemainingLogicalFuel == 0 &&
              tenant.NativeReservedBytes == 0,
            "tenant spent fuel persists while native arena bytes refund");
        _cases++;
    }

    private static void TinyArenaCapsManagedFrameMetadata()
    {
        using var tenant = new TenantVm(maximumActors: 1, maximumNativeBytes: 64);
        using var actor = tenant.CreateActor(arenaBytes: 64, maximumFrames: 1024);
        Check(actor.FrameCapacity == 64,
            "frame metadata capacity must not exceed native one-byte slots");
        var turn = actor.BeginTurn();
        for (int i = 0; i < 64; i++)
            actor.PushFrame(turn, bytes: 1, alignment: 1);
        Check(actor.FrameDepth == 64,
            "tiny arena admits only byte-accounted logical frames");
        Expect<ActorResourceExhaustedException>(
            () => actor.PushFrame(turn, bytes: 1, alignment: 1),
            "65th one-byte frame exhausts tiny actor stack");
        Expect<ActorResourceExhaustedException>(
            () => actor.EndTurn(turn),
            "caught tiny-arena quota failure remains terminal");
        Check(tenant.NativeReservedBytes == 0 &&
              tenant.LiveActorCount == 0,
            "terminal tiny arena is scrubbed and refunded");
        _cases++;
    }

    private static void RacyStopAndTurnExitRefundExactlyOnce()
    {
        // STOP may race with the final physical EndTurn, but neither
        // thread can free native memory while a live turn still owns it.
        using var tenant = new TenantVm(maximumActors: 1, maximumNativeBytes: 64);
        using var actor = tenant.CreateActor(arenaBytes: 64);
        var turn = actor.BeginTurn();
        var frame = actor.PushFrame(turn, 8);
        actor.WriteFrame(turn, frame, 0, new byte[] { 42 });
        Exception? fault = null;
        var faultLock = new object();
        using var go = new ManualResetEventSlim(false);
        Thread[] stops = new Thread[4];
        for (int i = 0; i < stops.Length; i++)
        {
            stops[i] = new Thread(() =>
            {
                try
                {
                    Check(go.Wait(10_000),
                        "STOP stress started before deadline");
                    for (int j = 0; j < 200; j++)
                        actor.Stop();
                }
                catch (Exception ex) { lock (faultLock) fault ??= ex; }
            });
            stops[i].Start();
        }
        go.Set();
        actor.EndTurn(turn);
        foreach (var worker in stops)
            Check(worker.Join(millisecondsTimeout: 10_000),
                "STOP stress worker terminates");
        if (fault is not null) throw new Exception("STOP stress failed", fault);
        Check(tenant.NativeReservedBytes == 0 &&
              tenant.LiveActorCount == 0 &&
              actor.FrameDepth == 0,
            "concurrent STOP and physical exit free/refund exactly once");
        Expect<InvalidOperationException>(
            () => actor.BeginTurn(),
            "stopped incarnation cannot be restarted");
        _cases++;
    }

    private static void ConcurrentActorLifecycleStress()
    {
        using var tenant = new TenantVm(maximumActors: 8, maximumNativeBytes: 512);
        Exception? fault = null;
        var faultLock = new object();
        Thread[] workers = new Thread[4];
        for (int worker = 0; worker < workers.Length; worker++)
        {
            int workerId = worker;
            workers[worker] = new Thread(() =>
            {
                try
                {
                    for (int iteration = 0; iteration < 60; iteration++)
                    {
                        using var actor = tenant.CreateActor(arenaBytes: 64, maximumFrames: 2);
                        var turn = actor.BeginTurn();
                        var frame = actor.PushFrame(turn, 8);
                        actor.SealScalarSchema(turn, frame, [ActorScalarKind.I64], 1);
                        long expected = workerId * 1000L + iteration;
                        actor.WriteI64(turn, frame, 0, expected);
                        Check(actor.ReadI64(turn, frame, 0) == expected,
                            "concurrent actor-private frame value must not alias siblings");
                        actor.PopFrame(turn, frame);
                        actor.EndTurn(turn);
                        actor.Stop();
                    }
                }
                catch (Exception ex)
                {
                    lock (faultLock) fault ??= ex;
                }
            });
            workers[worker].Start();
        }
        foreach (var worker in workers)
            Check(worker.Join(millisecondsTimeout: 20_000),
                "concurrent actor lifecycle worker terminated");
        if (fault is not null)
            throw new Exception("Concurrent actor lifecycle fault", fault);
        Check(tenant.LiveActorCount == 0 && tenant.NativeReservedBytes == 0,
            "240 concurrent lifecycle iterations refund every actor arena");
        _cases++;
    }

    private static void ReentrancyFailsClosed()
    {
        using var tenant = new TenantVm(maximumActors: 1, maximumNativeBytes: 64);
        using var actor = tenant.CreateActor(arenaBytes: 64);
        var turn = actor.BeginTurn();
        Expect<ActorResourceExhaustedException>(
            () => actor.BeginTurn(),
            "second concurrent physical actor turn terminates authority");
        Expect<ActorResourceExhaustedException>(
            () => actor.EndTurn(turn),
            "reentrancy cannot be swallowed");
        Check(tenant.NativeReservedBytes == 0, "reentrant actor scrubbed and refunded");
        _cases++;
    }

    private static int Main()
    {
        try
        {
            FrameTurnAndTenantFences();
            SealedScalarAndCorruption();
            TerminalQuotaAndSwallowedFailure();
            StopQuiescenceAndGeneration();
            PersistentPollAndFairness();
            ReentrancyFailsClosed();
            LongRunPollCannotBeEvaded();
            LogicalFramesSurviveCarrierMigration();
            QuiescentTenantDisposal();
            InvalidAdmissionIsAtomic();
            ConcurrentActorLifecycleStress();
            RacyStopAndTurnExitRefundExactlyOnce();
            TinyArenaCapsManagedFrameMetadata();
            TenantFuelSurvivesActorReincarnation();
            Console.WriteLine($"ATTO_NET_ACTOR_ARENA_COOPERATION_PASS cases={_cases}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ATTO_NET_ACTOR_ARENA_COOPERATION_FAIL " + ex);
            return 1;
        }
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ActorTurn))]
[JsonSerializable(typeof(ActorFrameHandle))]
internal partial class NoWireJsonContext : JsonSerializerContext { }
