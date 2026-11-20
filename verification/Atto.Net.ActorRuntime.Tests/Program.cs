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

        // Host raw-byte mutator cannot turn a corrupt payload into valid bool.
        actor.WriteFrame(turn, frame, 9, new byte[] { 2 });
        Expect<ActorResourceExhaustedException>(
            () => actor.ReadBool(turn, frame, 1),
            "noncanonical bool padding must terminate actor");
        Expect<ActorResourceExhaustedException>(
            () => actor.EndTurn(turn),
            "caught corruption fault remains terminal at dispatcher exit");
        Check(actor.NativeReservedBytes == 0, "corrupt actor arena reclaimed");
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
