using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class VerifiedActorFramePlannerSelfTest
{
    public static void Run()
    {
        SourceVerifiedCfgPlansAreBoundedAndTyped();
        OversizedVerifiedCfgIsRejectedBeforeFrameAllocation();
        Console.WriteLine(
            "PASS .NET compiler-verified CFG actor frame plans (metadata and host ABI only)");
    }

    private static void SourceVerifiedCfgPlansAreBoundedAndTyped()
    {
        // This is real parsed typed source and CFG verification, not
        // independently hand-authored host slot metadata.
        var module = OresStructuredCfgLowering.LowerModule("""
            fnc plus(int x):int { return x + 1; }
            fnc main():int {
                val bool enabled = true;
                if enabled { return plus(41); }
                else { return 0; }
            }
            """);
        Require(module.Helpers.Count == 1,
            "source helper remains a separately verified function");
        foreach (var verified in new[] { module.Main, module.Helpers[0] })
        {
            var plan = OresActorFramePlanner.Plan(verified);
            Require(plan.FunctionName == verified.Name && plan.Slots.Count > 0 &&
                    plan.Slots.Count <= OresActorFramePlanner.MaxSlots,
                "verified CFG produces a bounded function-owned plan");
            for (int i = 0; i < plan.Slots.Count; i++)
            {
                Require(plan.Slots[i].ResultId == i &&
                        plan.Slots[i].ByteOffset == checked(i * 8) &&
                        plan.Slots[i].Type is IrValueType.Bool or IrValueType.I64,
                    "dense typed SSA ID has an exact deterministic spill offset");
            }
            var mutableCopy = plan.Slots.ToArray();
            var original = plan.Slots[0];
            mutableCopy[0] = new ActorFrameSlotPlan(88, IrValueType.Bool, 0);
            Require(plan.Slots[0] == original,
                "compiler frame plan cannot mutate through an exported snapshot");
        }

        var mainPlan = OresActorFramePlanner.Plan(module.Main);
        Require(mainPlan.Slots.Any(slot => slot.Type == IrValueType.Bool),
            "source bool values retain their type through verified CFG");
        Require(mainPlan.Slots.Any(slot => slot.Type == IrValueType.I64),
            "source i64 values retain their type through verified CFG");
        var layout = new ActorStackFrameLayout(mainPlan.Slots.Select(slot =>
            slot.Type switch
            {
                IrValueType.I64 => ActorStackSlotKind.I64,
                IrValueType.Bool => ActorStackSlotKind.Bool,
                _ => throw new FormatException("Unsupported compiler spill type")
            }).ToArray());
        Require(layout.ByteLength == mainPlan.ByteLength,
            "compiler and runtime agree on deterministic eight-byte slots");

        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 1,
            MessagesPerDispatch = 2,
            ActorPersistentBytes = 8192,
            ActorScratchBytes = 512,
            ActorStackBytes = 2048,
            ActorMaxStackFrames = 4,
            NativeBudgetBytes = 64 * 1024,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1,
                SharedWorkers = 1,
                UntrustedWorkers = 1,
                QueueCapacityPerDomain = 4
            }
        });
        var boolSlot = mainPlan.Slots.First(s => s.Type == IrValueType.Bool).ResultId;
        var i64Slot = mainPlan.Slots.First(s => s.Type == IrValueType.I64).ResultId;
        ActorStackFrameHandle? frame = null;
        ActorStackResumeTicket? ticket = null;
        var actor = runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            if (message.Span[0] == 1)
            {
                frame = context.PushTypedStackFrame(layout);
                context.WriteStackBool(frame!, boolSlot, true);
                context.WriteStackI64(frame!, i64Slot, 42);
                ticket = context.SuspendStackFrame(frame!);
                return;
            }

            Require(message.Span[0] == 2,
                "unexpected compiler plan resume fixture message");
            var restored = context.ResumeStackFrame(ticket!);
            Require(context.ReadStackBool(restored, boolSlot) &&
                    context.ReadStackI64(restored, i64Slot) == 42,
                "source-derived layout restores typed native values after turn change");
            context.PopStackFrame(restored);
        });

        runtime.Send(actor, new byte[] { 1 });
        Require(WaitUntil(() => runtime.CompletedMessages == 1),
            "source-verified frame plan first physical turn completed");
        runtime.Send(actor, new byte[] { 2 });
        Require(WaitUntil(() => runtime.CompletedMessages == 2),
            "source-verified frame plan resumed on a new physical turn");
        Require(runtime.FaultedTurns == 0, "compiler metadata runtime host probe remains healthy");
        runtime.Stop(actor);
        Require(WaitUntil(() => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
            "source-plan actor frame and native heap fully reclaimed");
    }

    private static void OversizedVerifiedCfgIsRejectedBeforeFrameAllocation()
    {
        var ops = Enumerable.Range(0, OresActorFramePlanner.MaxSlots + 1)
            .Select(i => new CfgInstruction(
                i, CfgOpcode.ConstantI64, IrValueType.I64, I64Constant: i))
            .ToArray();
        // First verify typed SSA under the normal CIL limit; the independent
        // actor continuation frame cap is tighter than normal CFG admission.
        var verified = OresCfgVerifier.Verify(new CfgFunction(
            "main", IrValueType.I64, "entry", new[]
            {
                new CfgBlock("entry", Array.Empty<CfgParameter>(), ops,
                    new CfgReturn(ops.Length - 1))
            }));
        Require(verified.Blocks.Count == 1,
            "oversized scalar function still passes ordinary CFG verifier");
        try { _ = OresActorFramePlanner.Plan(verified); }
        catch (FormatException error) when
            (error.Message.Contains("128-slot budget", StringComparison.Ordinal))
        { return; }
        throw new Exception(
            "Compiler actor frame planner admitted more than the safe slot budget");
    }

    private static bool WaitUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Verified actor frame plan regression: " + scenario);
    }
}
