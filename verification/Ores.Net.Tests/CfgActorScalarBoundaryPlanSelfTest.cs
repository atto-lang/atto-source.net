using System.Linq;
using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class CfgActorScalarBoundaryPlanSelfTest
{
    public static void Run()
    {
        SingleSourceReturnRequiresNoBoundarySpill();
        DiamondEdgesPreserveParallelTypedArguments();
        LoopBackedgeHasBoundedLiveScalarState();
        ParallelSwapTransfersAreNotOrderedStores();
        DominatingScalarWithoutEdgeParameterRemainsLive();
        HostCallIsDeniedBeforeBoundaryPlanning();
        Console.WriteLine(
            "PASS .NET verified CFG scalar live-spill boundary plan (NOT generated actor CIL or guest execution)");
    }

    private static void SingleSourceReturnRequiresNoBoundarySpill()
    {
        var verified = OresStructuredCfgLowering.Lower(
            "fnc main():int{return 42;}");
        var plan = CfgActorScalarBoundaryPlan.Create(verified);
        Require(plan.Frame.ValueSlots > 0 &&
                plan.Frame.FrameBytes ==
                CfgActorScalarFramePlan.HeaderBytes +
                plan.Frame.ValueSlots * CfgActorScalarFramePlan.SlotBytes,
            "actual .atto source receives canonical 16-byte native scalar ABI layout");
        Require(plan.Blocks.Count == 1 &&
                plan.Block(verified.Entry).LiveInValueIds.Count == 0 &&
                plan.Block(verified.Entry).LiveOutValueIds.Count == 0 &&
                plan.Block(verified.Entry).Edges.Count == 0,
            "source return requires no serialized cross-block state");
        RejectFormat(() => { _ = plan.Block("foreign_block"); },
            "unknown block cannot be used as a compiler spill location");
    }

    private static void DiamondEdgesPreserveParallelTypedArguments()
    {
        var verified = OresCfgVerifier.Verify(new CfgFunction(
            "main", IrValueType.I64, "entry", new[]
            {
                new CfgBlock("entry", Array.Empty<CfgParameter>(), new[]
                {
                    new CfgInstruction(0, CfgOpcode.ConstantBool, IrValueType.Bool,
                        BoolConstant: true),
                    new CfgInstruction(1, CfgOpcode.ConstantI64, IrValueType.I64,
                        I64Constant: 41)
                }, new CfgBranch(0, "yes", new[] { 1 },
                    "no", new[] { 1 })),
                new CfgBlock("yes", new[] { new CfgParameter(2, IrValueType.I64) },
                    new[]
                    {
                        new CfgInstruction(3, CfgOpcode.ConstantI64, IrValueType.I64,
                            I64Constant: 1),
                        new CfgInstruction(4, CfgOpcode.CheckedAdd, IrValueType.I64,
                            Left: 2, Right: 3)
                    }, new CfgJump("end", new[] { 4 })),
                new CfgBlock("no", new[] { new CfgParameter(5, IrValueType.I64) },
                    new[]
                    {
                        new CfgInstruction(6, CfgOpcode.ConstantI64, IrValueType.I64,
                            I64Constant: 0),
                        new CfgInstruction(7, CfgOpcode.CheckedAdd, IrValueType.I64,
                            Left: 5, Right: 6)
                    }, new CfgJump("end", new[] { 7 })),
                new CfgBlock("end", new[] { new CfgParameter(8, IrValueType.I64) },
                    Array.Empty<CfgInstruction>(), new CfgReturn(8))
            }));
        var plan = CfgActorScalarBoundaryPlan.Create(verified);
        Require(plan.Frame.ValueSlots == 9 &&
                plan.Frame.SlotType(0) == IrValueType.Bool &&
                plan.Frame.SlotType(8) == IrValueType.I64,
            "SSA scalar types stay unchanged across both edges");
        Sequence(plan.Block("entry").LiveOutValueIds, 1);
        Sequence(plan.Block("yes").LiveOutValueIds, 4);
        Sequence(plan.Block("no").LiveOutValueIds, 7);
        Require(plan.Block("end").LiveOutValueIds.Count == 0,
            "return has no false successor or spill obligation");
        var edges = plan.Block("entry").Edges;
        Require(edges.Count == 2 && edges[0].Kind == "true" &&
                edges[1].Kind == "false" &&
                edges[0].Target == "yes" && edges[1].Target == "no",
            "both branches are retained and ordered independently");
        Sequence(edges[0].SourceValueIds, 1);
        Sequence(edges[1].SourceValueIds, 1);
        Sequence(edges[0].DestinationParameterIds, 2);
        Sequence(edges[1].DestinationParameterIds, 5);

        // A caller cannot rewrite a verified transfer or a frozen liveness set.
        bool denied = false;
        try { ((IList<int>)edges[0].DestinationParameterIds)[0] = 99; }
        catch (NotSupportedException) { denied = true; }
        Require(denied && edges[0].DestinationParameterIds[0] == 2,
            "edge destinations are immutable to malicious caller aliases");
        denied = false;
        try { ((IList<int>)plan.Block("entry").LiveOutValueIds)[0] = 0; }
        catch (NotSupportedException) { denied = true; }
        Require(denied && plan.Block("entry").LiveOutValueIds[0] == 1,
            "live-out spill descriptor cannot be mutated");
    }

    private static void LoopBackedgeHasBoundedLiveScalarState()
    {
        var verified = OresCfgVerifier.Verify(new CfgFunction(
            "main", IrValueType.I64, "entry", new[]
            {
                new CfgBlock("entry", Array.Empty<CfgParameter>(),
                    new[] { new CfgInstruction(0, CfgOpcode.ConstantI64,
                        IrValueType.I64, I64Constant: 0) },
                    new CfgJump("loop", new[] { 0 })),
                new CfgBlock("loop", new[] { new CfgParameter(1, IrValueType.I64) },
                    new[]
                    {
                        new CfgInstruction(2, CfgOpcode.ConstantI64, IrValueType.I64,
                            I64Constant: 3),
                        new CfgInstruction(3, CfgOpcode.LessThanI64, IrValueType.Bool,
                            Left: 1, Right: 2)
                    }, new CfgBranch(3, "body", new[] { 1 },
                        "done", new[] { 1 })),
                new CfgBlock("body", new[] { new CfgParameter(4, IrValueType.I64) },
                    new[]
                    {
                        new CfgInstruction(5, CfgOpcode.ConstantI64, IrValueType.I64,
                            I64Constant: 1),
                        new CfgInstruction(6, CfgOpcode.CheckedAdd, IrValueType.I64,
                            Left: 4, Right: 5)
                    }, new CfgJump("loop", new[] { 6 })),
                new CfgBlock("done", new[] { new CfgParameter(7, IrValueType.I64) },
                    Array.Empty<CfgInstruction>(), new CfgReturn(7))
            }));
        var plan = CfgActorScalarBoundaryPlan.Create(verified);
        Sequence(plan.Block("entry").LiveOutValueIds, 0);
        Sequence(plan.Block("loop").LiveOutValueIds, 1);
        Sequence(plan.Block("body").LiveOutValueIds, 6);
        Require(plan.Block("done").LiveOutValueIds.Count == 0 &&
                plan.Block("body").Edges.Single().Target == "loop",
            "loop backedge remains a bounded, single-scalar transfer");
    }

    private static void ParallelSwapTransfersAreNotOrderedStores()
    {
        // A loop carries two independently typed scalar parameters. Its
        // backedge swaps them. An emitter must stage the complete input edge
        // before updating either destination slot; sequential writes corrupt.
        var verified = OresCfgVerifier.Verify(new CfgFunction(
            "main", IrValueType.I64, "entry", new[]
            {
                new CfgBlock("entry", Array.Empty<CfgParameter>(),
                    new[]
                    {
                        new CfgInstruction(0, CfgOpcode.ConstantI64,
                            IrValueType.I64, I64Constant: 41),
                        new CfgInstruction(1, CfgOpcode.ConstantI64,
                            IrValueType.I64, I64Constant: 42)
                    }, new CfgJump("loop", new[] { 0, 1 })),
                new CfgBlock("loop", new[]
                    {
                        new CfgParameter(2, IrValueType.I64),
                        new CfgParameter(3, IrValueType.I64)
                    }, new[]
                    {
                        new CfgInstruction(4, CfgOpcode.ConstantBool,
                            IrValueType.Bool, BoolConstant: false)
                    }, new CfgBranch(4, "loop", new[] { 3, 2 },
                        "done", new[] { 3 })),
                new CfgBlock("done", new[] { new CfgParameter(5, IrValueType.I64) },
                    Array.Empty<CfgInstruction>(), new CfgReturn(5))
            }));
        var plan = CfgActorScalarBoundaryPlan.Create(verified);
        Sequence(plan.Block("entry").LiveOutValueIds, 0, 1);
        Sequence(plan.Block("loop").LiveOutValueIds, 2, 3);
        var swap = plan.Block("loop").Edges[0];
        Require(swap.Kind == "true" && swap.Target == "loop",
            "self-edge identity must survive immutable CFG lowering");
        Sequence(swap.SourceValueIds, 3, 2);
        Sequence(swap.DestinationParameterIds, 2, 3);
        Require(plan.Block("loop").Edges[1].Kind == "false",
            "a self-loop must not erase its independent exit edge");
        Sequence(plan.Block("loop").Edges[1].SourceValueIds, 3);
        Sequence(plan.Block("loop").Edges[1].DestinationParameterIds, 5);

        // Host-level integration of the compiler-derived edge descriptor.
        // No emitted CIL/actor await is implied. The native byte spill ABI
        // must stage both original values before it overwrites either target.
        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 1, MessagesPerDispatch = 1,
            ActorPersistentBytes = 8192, ActorScratchBytes = 512,
            ActorStackBytes = 256, ActorMaxStackFrames = 4,
            NativeBudgetBytes = 65536,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1, SharedWorkers = 1,
                UntrustedWorkers = 1, QueueCapacityPerDomain = 4
            }
        });
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            byte[] schema = plan.Frame.EncodeTypeTags();
            var frame = ActorScalarFrameAbiV1.Push(context, schema,
                plan.Frame.BlockCount);
            ActorScalarFrameAbiV1.WriteI64(context, frame, 2, 41);
            ActorScalarFrameAbiV1.WriteI64(context, frame, 3, 42);
            byte[] kinds = swap.SourceValueIds.Select(id => schema[id]).ToArray();
            ActorScalarFrameAbiV1.TransferParallel(context, frame,
                swap.SourceValueIds.ToArray(),
                swap.DestinationParameterIds.ToArray(), kinds);
            Require(ActorScalarFrameAbiV1.ReadI64(context, frame, 2) == 42 &&
                    ActorScalarFrameAbiV1.ReadI64(context, frame, 3) == 41,
                "native typed ABI must commit a verified swap simultaneously");

            // The second destination is sealed i64, but the caller forges a
            // bool write. All proposals are checked before the first write.
            RejectInvalid(() => ActorScalarFrameAbiV1.TransferParallel(
                context, frame, new[] { 2, 3 }, new[] { 2, 3 },
                new byte[] { 1, 2 }),
                "failed later destination must not partially mutate earlier");
            Require(ActorScalarFrameAbiV1.ReadI64(context, frame, 2) == 42 &&
                    ActorScalarFrameAbiV1.ReadI64(context, frame, 3) == 41,
                "typed native frame remains unchanged after rejected transfer");

            RejectFormat(() => ActorScalarFrameAbiV1.TransferParallel(
                context, frame, new[] { 2, 3 }, new[] { 2, 2 },
                new byte[] { 1, 1 }),
                "duplicate edge destination cannot be committed");
            RejectFormat(() => ActorScalarFrameAbiV1.TransferParallel(
                context, frame, new[] { 2 }, new[] { 3, 2 },
                new byte[] { 1 }),
                "partial edge arity cannot reach native frame writes");
            RejectFormat(() => ActorScalarFrameAbiV1.TransferParallel(
                context, frame, new[] { 2 }, new[] { 3 },
                new byte[] { 3 }),
                "unknown scalar edge tags must be rejected before writes");
            RejectInvalid(() => ActorScalarFrameAbiV1.TransferParallel(
                context, frame, new[] { 2, 4 }, new[] { 2, 3 },
                new byte[] { 1, 1 }),
                "late bool source cannot impersonate an i64 edge input");
            Require(ActorScalarFrameAbiV1.ReadI64(context, frame, 2) == 42 &&
                    ActorScalarFrameAbiV1.ReadI64(context, frame, 3) == 41,
                "rejected late source must leave earlier destination unchanged");
            ActorScalarFrameAbiV1.Pop(context, frame);
        });
        runtime.Send(actor, new byte[] { 1 });
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 1,
                TimeSpan.FromSeconds(5)) && runtime.FaultedTurns == 0,
            "verified CFG boundary host transfer completed without actor fault");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() =>
                runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
                TimeSpan.FromSeconds(5)),
            "CFG-to-actor native frame transfer reclaims its actor arena");
    }

    private static void DominatingScalarWithoutEdgeParameterRemainsLive()
    {
        var verified = OresCfgVerifier.Verify(new CfgFunction(
            "main", IrValueType.I64, "entry", new[]
            {
                new CfgBlock("entry", Array.Empty<CfgParameter>(),
                    new[] { new CfgInstruction(0, CfgOpcode.ConstantI64,
                        IrValueType.I64, I64Constant: 40) },
                    new CfgJump("finish", Array.Empty<int>())),
                new CfgBlock("finish", Array.Empty<CfgParameter>(),
                    new[]
                    {
                        new CfgInstruction(1, CfgOpcode.ConstantI64,
                            IrValueType.I64, I64Constant: 2),
                        new CfgInstruction(2, CfgOpcode.CheckedAdd,
                            IrValueType.I64, Left: 0, Right: 1)
                    }, new CfgReturn(2))
            }));
        var plan = CfgActorScalarBoundaryPlan.Create(verified);
        Sequence(plan.Block("entry").LiveOutValueIds, 0);
        Sequence(plan.Block("finish").LiveInValueIds, 0);
        Require(plan.Block("entry").Edges[0].SourceValueIds.Count == 0,
            "cross-block dominated liveness cannot depend on explicit edge args");
    }

    private static void HostCallIsDeniedBeforeBoundaryPlanning()
    {
        var verified = OresCfgVerifier.Verify(new CfgFunction(
            "main", IrValueType.I64, "entry", new[]
            {
                new CfgBlock("entry", Array.Empty<CfgParameter>(),
                    new[] { new CfgInstruction(0, CfgOpcode.Call,
                        IrValueType.I64, Target: "helper",
                        Arguments: Array.Empty<int>()) }, new CfgReturn(0))
            }), new Dictionary<string, CfgCallSignature>(StringComparer.Ordinal)
            {
                ["helper"] = new CfgCallSignature(IrValueType.I64,
                    Array.Empty<IrValueType>())
            });
        RejectFormat(() => { _ = CfgActorScalarBoundaryPlan.Create(verified); },
            "compiler must not authorize unverified ownership/unwind host calls");
    }

    private static void Sequence(IReadOnlyList<int> actual, params int[] expected)
    {
        Require(actual.SequenceEqual(expected),
            "unexpected exact SSA live-value/transfer boundary");
    }

    private static void RejectInvalid(Action action, string scenario)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected native transfer rejection: " + scenario);
    }

    private static void RejectFormat(Action action, string scenario)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Expected scalar boundary admission rejection: " + scenario);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor scalar boundary plan regression: " + scenario);
    }
}
