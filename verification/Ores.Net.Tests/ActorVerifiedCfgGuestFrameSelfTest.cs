using Ores.Net.Compiler;
using Ores.Net.Runtime;
using System.Threading;

/// <summary>
/// The first real .atto parser -> verified CFG plan -> terminal guest-frame
/// native-memory seam. The message handler here is still a trusted C# host
/// callback, NOT compiler-lowered source-level actor dispatch or async await.
/// </summary>
internal static class ActorVerifiedCfgGuestFrameSelfTest
{
    public static void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "contracts", "actor-guest-frame-seam", "v1", "scalar42.atto");
        var verified = OresStructuredCfgLowering.Lower(File.ReadAllText(path));
        var plan = CfgActorScalarFramePlan.Create(verified);
        Require(plan.ValueSlots == 1 && plan.FrameBytes == 32 &&
                plan.BlockCount == 1 && plan.EntryBlockIndex == 0 &&
                plan.EncodeTypeTags().SequenceEqual(new byte[] { 1 }),
            "real parsed .atto SSA layout matches bounded native guest ABI");

        var entry = verified.Blocks[0];
        var instruction = entry.Instructions.Single(i =>
            i.Opcode == CfgOpcode.ConstantI64 && i.I64Constant == 42);
        int valueSlot = instruction.ResultId;
        long sourceConstant = instruction.I64Constant!.Value;
        byte[] types = plan.EncodeTypeTags();

        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 4,
            MessagesPerDispatch = 1,
            ActorPersistentBytes = 4096,
            ActorScratchBytes = 512,
            ActorStackBytes = 64,
            ActorMaxStackFrames = 2,
            NativeBudgetBytes = 64 * 1024,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1,
                SharedWorkers = 1,
                UntrustedWorkers = 1,
                QueueCapacityPerDomain = 8
            }
        });

        ActorContext? expired = null;
        ActorStackFrameHandle? oldHandle = null;
        var exhausted = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            expired = context;
            var first = ActorScalarGuestFrameAbiV1.Push(
                context, types, plan.BlockCount, plan.EntryBlockIndex);
            oldHandle = first;
            _ = ActorScalarGuestFrameAbiV1.Push(
                context, types, plan.BlockCount, plan.EntryBlockIndex);
            // The 3rd source-planned frame exceeds this actor's 64-byte
            // logical stack, not the CLR worker's physical call stack.
            ExpectInvalid(() => {
                _ = ActorScalarGuestFrameAbiV1.Push(
                    context, types, plan.BlockCount, plan.EntryBlockIndex);
            }, "third verified CFG guest frame must exhaust the actor quota");
            // Even a swallowed overflow must fault this actor at turn exit.
        });

        using var survivorDone = new ManualResetEventSlim();
        int survivorValue = 0;
        var survivor = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var frame = ActorScalarGuestFrameAbiV1.Push(
                context, types, plan.BlockCount, plan.EntryBlockIndex);
            // Source-derived frame bytes must agree with the existing ATF1
            // scalar ABI rather than inventing a second native format.
            byte[] wireFreeHeader = new byte[HeaderBytes];
            context.GuestReadStackFrame(frame, 0, wireFreeHeader);
            Require(wireFreeHeader[0] == (byte)'A' &&
                    wireFreeHeader[1] == (byte)'T' &&
                    wireFreeHeader[2] == (byte)'F' &&
                    wireFreeHeader[3] == (byte)'1' &&
                    wireFreeHeader[4] == plan.ValueSlots &&
                    wireFreeHeader[8] == plan.BlockCount,
                "guest frame ATF1 header differs from host scalar ABI");
            ActorScalarGuestFrameAbiV1.WriteI64(
                context, frame, valueSlot, sourceConstant);
            ActorScalarGuestFrameAbiV1.SetProgramCounter(
                context, frame, plan.EntryBlockIndex);
            long result = ActorScalarGuestFrameAbiV1.ReadI64(
                context, frame, valueSlot);
            Require(ActorScalarGuestFrameAbiV1.ReadProgramCounter(
                context, frame) == plan.EntryBlockIndex && result == 42,
                "actor B failed typed native guest frame round trip");
            ActorScalarGuestFrameAbiV1.Pop(context, frame);
            // The common scalar guest codec must also round-trip an
            // explicitly declared bool without retyping the parsed i64 slot.
            var boolFrame = ActorScalarGuestFrameAbiV1.Push(
                context, new byte[] { 2 }, blockCount: 1);
            ActorScalarGuestFrameAbiV1.WriteBool(context, boolFrame, 0, true);
            Require(ActorScalarGuestFrameAbiV1.ReadBool(context, boolFrame, 0),
                "verified guest bool slot failed canonical encoding");
            ActorScalarGuestFrameAbiV1.Pop(context, boolFrame);
            Volatile.Write(ref survivorValue, checked((int)result));
            survivorDone.Set();
        });

        runtime.Send(exhausted, new byte[] { 1 });
        runtime.Send(survivor, new byte[] { 2 });
        Require(survivorDone.Wait(TimeSpan.FromSeconds(8)) &&
                Volatile.Read(ref survivorValue) == 42,
            "same SHARED carrier failed to run actor B after actor A overflow");
        Require(SpinWait.SpinUntil(
            () => runtime.FaultedTurns == 1 &&
                  runtime.LiveActors == 1 && runtime.CompletedMessages == 1,
            TimeSpan.FromSeconds(8)),
            "caught logical stack overflow must stop only actor A");
        ExpectInvalid(() =>
            ActorScalarGuestFrameAbiV1.ReadI64(expired!, oldHandle!, valueSlot),
            "retained old physical guest frame authority must be rejected");
        ExpectInvalid(() => runtime.Send(exhausted, new byte[] { 9 }),
            "faulted actor reference must not remain reachable");

        // The current runtime re-spawns under a NEW ActorRef/ID; it does
        // NOT preserve a stable supervised reference across generations.
        // Record that limitation rather than calling this hot restart.
        using var replacementDone = new ManualResetEventSlim();
        long replacementValue = 0;
        var replacement = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var frame = ActorScalarGuestFrameAbiV1.Push(
                context, types, plan.BlockCount, plan.EntryBlockIndex);
            // A fresh actor must never inherit the faulted actor's bytes.
            // Attempting an uninitialized read is terminal, so first
            // verify zero bytes via the authorized guest byte API.
            byte[] raw = new byte[SlotBytes];
            context.GuestReadStackFrame(frame, HeaderBytes, raw);
            Require(raw[0] == 0 && raw[1] == 1 &&
                    raw.AsSpan(2).IndexOfAnyExcept((byte)0) < 0,
                "successor actor slot is not clean and uninitialized");
            ActorScalarGuestFrameAbiV1.WriteI64(context, frame,
                valueSlot, sourceConstant);
            replacementValue = ActorScalarGuestFrameAbiV1.ReadI64(
                context, frame, valueSlot);
            ActorScalarGuestFrameAbiV1.Pop(context, frame);
            replacementDone.Set();
        });
        Require(replacement.Id != exhausted.Id,
            "replacement is a fresh actor, not an unsupported stable ref restart");
        runtime.Send(replacement, new byte[] { 3 });
        Require(replacementDone.Wait(TimeSpan.FromSeconds(8)) &&
                replacementValue == 42,
            "fresh actor failed native scalar guest-frame execution");
        ExpectInvalid(() => runtime.Send(exhausted, new byte[] { 9 }),
            "stale failed actor ref revived after replacement admission");
        runtime.Stop(survivor);
        runtime.Stop(replacement);
        Require(SpinWait.SpinUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(8)),
            "native actor heap/frame reservations not refunded on final STOP");

        // In a separate incarnation, an INVALID VERIFIED HEADER is also
        // terminal rather than recoverable as a normal host frame error.
        var corrupted = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var frame = ActorScalarGuestFrameAbiV1.Push(
                context, types, plan.BlockCount, plan.EntryBlockIndex);
            context.GuestWriteStackFrame(frame, 0, new byte[] { 0 });
            ExpectInvalid(() =>
                ActorScalarGuestFrameAbiV1.ReadI64(context, frame, valueSlot),
                "corrupt guest typed header must poison the actor");
        });
        runtime.Send(corrupted, new byte[] { 4 });
        Require(SpinWait.SpinUntil(() =>
            runtime.FaultedTurns == 2 &&
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(8)),
            "corrupt typed guest header was not terminal or leaked actor native memory");
        Console.WriteLine(
            "PASS source-CFG-to-guest-frame-host-seam|value=42|quota_fault=1|header_fault=1|stale=denied|new_actor=1|guest_actor_atto=0");
    }

    private const int HeaderBytes = ActorScalarGuestFrameAbiV1.HeaderBytes;
    private const int SlotBytes = ActorScalarGuestFrameAbiV1.SlotBytes;

    private static void ExpectInvalid(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Missing verified guest ABI rejection: " + why);
    }

    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception("Verified source CFG guest-frame seam: " + why);
    }
}
