using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class ActorScalarFrameAbiSelfTest
{
    public static void Run()
    {
        VerifiedCfgLayoutIsBoundedAndTyped();
        ActorOwnedSpillsSurvivePhysicalTurns();
        Console.WriteLine("PASS actor scalar frame ABI v1 host/compiler-boundary tests (NOT guest execution)");
    }

    private static CfgActorScalarFramePlan CheckedLayout()
    {
        // This passes through the real structured .atto parser and CFG verifier;
        // no actor syntax, guest runtime execution, or await is implied.
        var parsed = OresStructuredCfgLowering.Lower(
            "fnc main():int{return 42;}");
        var parsedPlan = CfgActorScalarFramePlan.Create(parsed);
        Require(parsedPlan.ValueSlots > 0 &&
                parsedPlan.FrameBytes == 16 + parsedPlan.ValueSlots * 16,
            "real source CFG yields a bounded native spill layout");

        var scalar = new CfgFunction("main", IrValueType.I64, "entry",
            new[] {
                new CfgBlock("entry", Array.Empty<CfgParameter>(),
                    new[] {
                        new CfgInstruction(0, CfgOpcode.ConstantBool,
                            IrValueType.Bool, BoolConstant: true),
                        new CfgInstruction(1, CfgOpcode.ConstantI64,
                            IrValueType.I64, I64Constant: 42)
                    }, new CfgReturn(1))
            });
        return CfgActorScalarFramePlan.Create(OresCfgVerifier.Verify(scalar));
    }

    private static void VerifiedCfgLayoutIsBoundedAndTyped()
    {
        var plan = CheckedLayout();
        Require(plan.ValueSlots == 2 && plan.BlockCount == 1 &&
                plan.EntryBlockIndex == 0 &&
                plan.SlotType(0) == IrValueType.Bool &&
                plan.SlotType(1) == IrValueType.I64 &&
                plan.FrameBytes == ActorScalarFrameAbiV1.FrameBytes(2) &&
                plan.EncodeTypeTags().SequenceEqual(new byte[] { 2, 1 }) &&
                plan.BlockIndex("entry") == 0,
            "verified CFG value IDs and native scalar ABI layout agree");
        RejectRange(() => ActorScalarFrameAbiV1.FrameBytes(0),
            "empty native frame prohibited");
        RejectRange(() => ActorScalarFrameAbiV1.FrameBytes(257),
            "scalar spill maximum enforced");
        RejectRange(() => { _ = plan.SlotType(2); },
            "out-of-bounds compiler slot rejected");
        RejectFormat(() => { _ = plan.BlockIndex("not_a_block"); },
            "unknown compiler block rejected");

        // Verifier accepts larger CFGs; this ABI has a deliberately smaller
        // actor-stack budget and must reject them before frame allocation.
        var many = Enumerable.Range(0, 257)
            .Select(i => new CfgInstruction(i, CfgOpcode.ConstantI64,
                IrValueType.I64, I64Constant: i)).ToArray();
        var large = OresCfgVerifier.Verify(new CfgFunction(
            "main", IrValueType.I64, "entry",
            new[] { new CfgBlock("entry", Array.Empty<CfgParameter>(),
                many, new CfgReturn(256)) }));
        RejectFormat(() => { _ = CfgActorScalarFramePlan.Create(large); },
            "compiler budget refuses large spilled SSA graphs");

        // A verified host call remains inadmissible: no source argument,
        // ownership, drop, or suspension-transfer ABI has been proven.
        var call = OresCfgVerifier.Verify(new CfgFunction(
            "main", IrValueType.I64, "entry",
            new[] { new CfgBlock("entry", Array.Empty<CfgParameter>(),
                new[] { new CfgInstruction(0, CfgOpcode.Call,
                    IrValueType.I64, Target: "helper",
                    Arguments: Array.Empty<int>()) }, new CfgReturn(0)) }),
            new Dictionary<string, CfgCallSignature>(StringComparer.Ordinal)
            {
                ["helper"] = new CfgCallSignature(IrValueType.I64,
                    Array.Empty<IrValueType>())
            });
        RejectFormat(() => { _ = CfgActorScalarFramePlan.Create(call); },
            "unproven helper call cannot enter actor frame ABI");
    }

    private static void ActorOwnedSpillsSurvivePhysicalTurns()
    {
        var plan = CheckedLayout();
        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 4, MessagesPerDispatch = 1,
            ActorPersistentBytes = 4096, ActorScratchBytes = 512,
            ActorStackBytes = 2048, ActorMaxStackFrames = 4,
            NativeBudgetBytes = 65536,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1, SharedWorkers = 1,
                UntrustedWorkers = 1, QueueCapacityPerDomain = 8
            }
        });

        ActorStackFrameHandle? saved = null;
        ActorContext? expired = null;
        var actor = runtime.Spawn(ActorKind.Private, (context, data) =>
        {
            if (data.Span[0] == 1)
            {
                var mutableTypes = plan.EncodeTypeTags();
                saved = ActorScalarFrameAbiV1.Push(context,
                    mutableTypes, plan.BlockCount);
                // A mutable caller array must not change issued type authority.
                mutableTypes[1] = 2;
                ActorScalarFrameAbiV1.WriteBool(context, saved, 0, true);
                ActorScalarFrameAbiV1.WriteI64(context, saved, 1, 42);
                ActorScalarFrameAbiV1.SetProgramCounter(context, saved,
                    plan.EntryBlockIndex);
                expired = context;
                return;
            }

            Require(ActorScalarFrameAbiV1.ReadProgramCounter(context, saved!) == 0 &&
                    ActorScalarFrameAbiV1.ReadBool(context, saved!, 0) &&
                    ActorScalarFrameAbiV1.ReadI64(context, saved!, 1) == 42,
                "typed PC/bool/i64 native bytes survive carrier turn handoff");
            Span<byte> headerBytes = stackalloc byte[16];
            context.ReadStackFrame(saved!, 0, headerBytes);
            Require(headerBytes[0] == (byte)'A' && headerBytes[1] == (byte)'T' &&
                    headerBytes[2] == (byte)'F' && headerBytes[3] == (byte)'1' &&
                    headerBytes[4] == plan.ValueSlots && headerBytes[8] == plan.BlockCount,
                "physical little-endian frame header agrees with verified CFG layout");
            Span<byte> encodedBool = stackalloc byte[16];
            context.ReadStackFrame(saved!, 16, encodedBool);
            Require(encodedBool[0] == 2 && encodedBool[1] == 2 &&
                    encodedBool[8] == 1,
                "native boolean slot keeps separate initialized and declared type tags");
            RejectInvalid(() => { _ = ActorScalarFrameAbiV1.ReadI64(context, saved!, 0); },
                "bool slot cannot masquerade as int");
            RejectInvalid(() => ActorScalarFrameAbiV1.WriteI64(context, saved!, 0, 11),
                "verified bool slot cannot be rewritten as i64");
            RejectInvalid(() => { _ = ActorScalarFrameAbiV1.ReadBool(context, saved!, 1); },
                "int slot cannot masquerade as bool");
            RejectInvalid(() => ActorScalarFrameAbiV1.WriteBool(context, saved!, 1, false),
                "verified i64 slot cannot be rewritten as bool");

            // Direct raw writes are still trusted-host APIs. They may corrupt
            // native tags, but cannot retype the independent sealed CFG schema.
            byte[] originalI64 = new byte[ActorScalarFrameAbiV1.SlotBytes];
            context.ReadStackFrame(saved!, ActorScalarFrameAbiV1.HeaderBytes +
                ActorScalarFrameAbiV1.SlotBytes, originalI64);
            var forgedBool = new byte[ActorScalarFrameAbiV1.SlotBytes];
            forgedBool[0] = 2;
            forgedBool[1] = 2;
            forgedBool[8] = 1;
            context.WriteStackFrame(saved!, ActorScalarFrameAbiV1.HeaderBytes +
                ActorScalarFrameAbiV1.SlotBytes, forgedBool);
            RejectInvalid(() => { _ = ActorScalarFrameAbiV1.ReadBool(context, saved!, 1); },
                "forged native bool tags cannot retype an issued i64 slot");
            RejectInvalid(() => ActorScalarFrameAbiV1.WriteBool(context, saved!, 1, true),
                "forged native tag must not authorize type-changing writes");
            context.WriteStackFrame(saved!, ActorScalarFrameAbiV1.HeaderBytes +
                ActorScalarFrameAbiV1.SlotBytes, originalI64);
            Require(ActorScalarFrameAbiV1.ReadI64(context, saved!, 1) == 42,
                "restoring the original bytes preserves the sealed i64 schema");

            context.WriteStackFrame(saved!, 8, new byte[] { 2, 0, 0, 0 });
            RejectInvalid(() => { _ = ActorScalarFrameAbiV1.ReadProgramCounter(context, saved!); },
                "forged block-count header cannot redefine verified CFG layout");
            context.WriteStackFrame(saved!, 8, new byte[] { 1, 0, 0, 0 });
            Require(ActorScalarFrameAbiV1.ReadProgramCounter(context, saved!) == 0,
                "restored block count re-admits the original sealed frame");

            RejectRange(() => ActorScalarFrameAbiV1.SetProgramCounter(
                context, saved!, plan.BlockCount),
                "compiler PC bounds enforced");
            RejectRange(() => ActorScalarFrameAbiV1.WriteI64(
                context, saved!, plan.ValueSlots, 0),
                "slot bounds enforced");
            ActorScalarFrameAbiV1.Pop(context, saved!);
            RejectInvalid(() => { _ = ActorScalarFrameAbiV1.ReadI64(context, saved!, 1); },
                "stale popped activation denied");

            RejectFormat(() => {
                _ = ActorScalarFrameAbiV1.Push(context, new byte[] { 3 }, 1);
            }, "unknown type tags are denied before native frame allocation");
            var multiBlock = ActorScalarFrameAbiV1.Push(context,
                plan.EncodeTypeTags(), 3);
            ActorScalarFrameAbiV1.SetProgramCounter(context, multiBlock, 2);
            Require(ActorScalarFrameAbiV1.ReadProgramCounter(context, multiBlock) == 2,
                "nonzero native PC persists through typed ABI");
            ActorScalarFrameAbiV1.Pop(context, multiBlock);
            var reused = ActorScalarFrameAbiV1.Push(context,
                plan.EncodeTypeTags(), plan.BlockCount);
            RejectInvalid(() => { _ = ActorScalarFrameAbiV1.ReadI64(context, reused, 1); },
                "recycled native slot is uninitialized, not prior data");
            context.WriteStackFrame(reused, 12, new byte[] { 255, 255, 255, 127 });
            RejectInvalid(() => { _ = ActorScalarFrameAbiV1.ReadI64(context, reused, 1); },
                "corrupt saved PC must deny even ordinary scalar slot access");
            context.WriteStackFrame(reused, 12, new byte[] { 0, 0, 0, 0 });
            context.WriteStackFrame(reused, 0, new byte[] { 0 });
            RejectInvalid(() => { _ = ActorScalarFrameAbiV1.ReadBool(context, reused, 0); },
                "corrupted native frame header cannot authorize reads");
            // Corrupt headers cannot authorize high-level Pop; exact-issued
            // underlying frame cleanup is still possible on the live turn.
            context.PopStackFrame(reused);
        });

        runtime.Send(actor, new byte[] { 1 });
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 1,
                TimeSpan.FromSeconds(5)), "initial typed frame creation turn completed");
        RejectInvalid(() => {
            _ = ActorScalarFrameAbiV1.ReadI64(expired!, saved!, 1);
        }, "old ActorContext must not read a suspended scalar activation");

        runtime.Send(actor, new byte[] { 2 });
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 2,
                TimeSpan.FromSeconds(5)), "second turn restored and popped typed frame");
        Require(runtime.FaultedTurns == 0,
            "typed scalar frame controls did not fault host actors");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(5)),
            "actor-owned typed spill bytes reclaimed with native arena");
    }

    private static void RejectInvalid(Action action, string purpose)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Missing actor scalar ABI rejection: " + purpose);
    }

    private static void RejectRange(Action action, string purpose)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Missing actor scalar ABI bounds rejection: " + purpose);
    }

    private static void RejectFormat(Action action, string purpose)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Missing actor scalar layout rejection: " + purpose);
    }

    private static void Require(bool condition, string purpose)
    {
        if (!condition) throw new Exception("Actor scalar ABI: " + purpose);
    }
}
