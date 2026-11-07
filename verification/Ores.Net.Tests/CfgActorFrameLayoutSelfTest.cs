using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class CfgActorFrameLayoutSelfTest
{
    public static void Run()
    {
        // Only verified, frozen SSA may generate a frame layout. These are
        // compiler-owned typed slots, NOT executable .atto actor frames.
        var mutable = new[]
        {
            new CfgInstruction(0, CfgOpcode.ConstantBool, IrValueType.Bool,
                BoolConstant: true),
            new CfgInstruction(1, CfgOpcode.ConstantI64, IrValueType.I64,
                I64Constant: 42)
        };
        var input = new CfgFunction("main", IrValueType.I64, "entry",
            new[] { new CfgBlock("entry", Array.Empty<CfgParameter>(),
                mutable, new CfgReturn(1)) });
        var verified = OresCfgVerifier.Verify(input);
        var layout = CfgActorFrameLayoutPlanner.Plan(verified, maximumFrameBytes: 16);
        Require(layout.ByteLength == 16 && layout.Slots.Count == 2,
            "two typed values occupy two bounded native scalar slots");
        Require(layout.Slots[0] == new CfgActorFrameSlot(
                    0, IrValueType.Bool, 0, 8) &&
                layout.Slots[1] == new CfgActorFrameSlot(
                    1, IrValueType.I64, 8, 8),
            "SSA slot offsets and scalar types remain deterministic");

        // No stale alias to unverified input may mutate the frame descriptor.
        mutable[0] = new CfgInstruction(0, CfgOpcode.ConstantI64,
            IrValueType.I64, I64Constant: 123);
        Require(layout.Slots[0].Type == IrValueType.Bool &&
                CfgActorFrameLayoutPlanner.Plan(verified, 16).Slots[0].Type ==
                    IrValueType.Bool,
            "verified graph snapshot prevents post-verification type drift");

        ExpectFrameError(() => CfgActorFrameLayoutPlanner.Plan(verified, 15),
            "frame byte budget must reject an otherwise valid typed graph");
        ExpectFrameError(() => CfgActorFrameLayoutPlanner.Plan(verified, 8),
            "small per-actor frame budget must fail closed");
        bool rejectedLimit = false;
        try
        {
            _ = CfgActorFrameLayoutPlanner.Plan(verified,
                CfgActorFrameLayoutPlanner.MaximumFrameBytes + 1);
        }
        catch (ArgumentOutOfRangeException) { rejectedLimit = true; }
        Require(rejectedLimit, "invalid maximum frame bound rejected before planning");

        CanonicalTypedFrameBudgetIsIndependentOfGenericCFG();
        SourceVerifiedLayoutFeedsCanonicalActorSlab();
        Console.WriteLine(
            "PASS .NET compiler typed CFG actor-frame layout plan (no CIL spill or .atto actor guest executed)");
    }

    private static void CanonicalTypedFrameBudgetIsIndependentOfGenericCFG()
    {
        var allSlots = Enumerable.Range(0, CfgActorFrameLayoutPlanner.MaximumCanonicalTypedSlots + 1)
            .Select(i => new CfgInstruction(i, CfgOpcode.ConstantI64,
                IrValueType.I64, I64Constant: i)).ToArray();
        var verified = OresCfgVerifier.Verify(new CfgFunction(
            "main", IrValueType.I64, "entry", new[]
            {
                new CfgBlock("entry", Array.Empty<CfgParameter>(), allSlots,
                    new CfgReturn(allSlots.Length - 1))
            }));
        Require(CfgActorFrameLayoutPlanner.Plan(verified, 4096).Slots.Count == 129,
            "generic verified CFG may retain more than 128 scalar values");
        ExpectFrameError(() => CfgActorFrameLayoutPlanner.PlanCanonicalTyped(
                verified, 4096),
            "canonical typed actor frame rejects 129 values before runtime allocation");
        bool rejectsInvalidMaximum = false;
        try { _ = CfgActorFrameLayoutPlanner.PlanCanonicalTyped(
            verified, CfgActorFrameLayoutPlanner.MaximumFrameBytes + 1); }
        catch (ArgumentOutOfRangeException) { rejectsInvalidMaximum = true; }
        Require(rejectsInvalidMaximum,
            "canonical planner rejects invalid byte budget before layout construction");
        Require(CfgActorFrameLayoutPlanner.MaximumCanonicalTypedBytes ==
                8 * ActorFrameTypedLayoutMaximumSlots(),
            "compiler canonical scalar slot contract matches the runtime 128-slot limit");
    }

    private static int ActorFrameTypedLayoutMaximumSlots() => 128;

    private static void SourceVerifiedLayoutFeedsCanonicalActorSlab()
    {
        const string source = """
            fnc main():int {
                val bool enabled = true;
                if enabled { return 42; } else { return 0; }
            }
            """;
        var verified = OresStructuredCfgLowering.Lower(source);
        var plan = CfgActorFrameLayoutPlanner.PlanCanonicalTyped(
            verified, maximumFrameBytes: 2048);
        Require(plan.Slots.Any(slot => slot.Type == IrValueType.I64) &&
                plan.Slots.Any(slot => slot.Type == IrValueType.Bool),
            "real typed source preserves scalar types through verified CFG");
        var layout = new ActorFrameTypedLayout(plan.Slots.Select(slot =>
            slot.Type switch
            {
                IrValueType.I64 => ActorFrameScalarKind.I64,
                IrValueType.Bool => ActorFrameScalarKind.Bool,
                _ => throw new FormatException("Unverified spill slot kind")
            }).ToArray());
        Require(layout.ByteLength == plan.ByteLength,
            "compiler-owned CFG offsets match canonical runtime frame ABI");
        int i64Slot = plan.Slots.First(slot => slot.Type == IrValueType.I64).ValueId;
        int boolSlot = plan.Slots.First(slot => slot.Type == IrValueType.Bool).ValueId;

        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 1,
            MessagesPerDispatch = 1,
            ActorPersistentBytes = 8192,
            ActorScratchBytes = 512,
            ActorFrameBytes = 2048,
            ActorMaxFrames = 4,
            NativeBudgetBytes = 64 * 1024,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
                QueueCapacityPerDomain = 4
            }
        });
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            var execution = context.BeginFrames();
            var frame = context.PushTypedFrame(execution, layout);
            context.WriteFrameI64(execution, frame, i64Slot, 42);
            context.WriteFrameBool(execution, frame, boolSlot, true);
            Require(context.ReadFrameI64(execution, frame, i64Slot) == 42 &&
                    context.ReadFrameBool(execution, frame, boolSlot),
                "source-derived types reach independent actor-owned native slab");
            context.PopFrame(execution, frame);
            context.FinishFrames(execution);
        });
        runtime.Send(actor, new byte[] { 1 });
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 1,
            TimeSpan.FromSeconds(5)), "source-derived typed frame host turn completed");
        Require(runtime.FaultedTurns == 0,
            "typed compiler-plan host probe does not fault its actor");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() =>
                runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(5)),
            "source-derived actor frame and arena release native quota");
    }

    private static void ExpectFrameError(Action action, string scenario)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Expected typed frame layout rejection: " + scenario);
    }

    private static void Require(bool value, string scenario)
    {
        if (!value)
            throw new Exception("Compiler actor frame layout regression: " + scenario);
    }
}
