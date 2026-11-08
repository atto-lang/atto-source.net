using System.Buffers.Binary;
using System.Threading;
using Ores.Net.Compiler;
using Ores.Net.Runtime;

// Test-only CFG executor: real .atto source -> structured lowering -> verified
// typed CFG -> actor-owned native logical frame bytes. Not emitted NativeAOT
// guest machine code, actor declaration lowering, or a general scheduler.
// Never interpret source text directly; the only executed operations come
// from the verified compiler CFG and its immutable frame layout.
internal static class ActorCompiledCfgFrameSelfTest
{
    private const int HeaderBytes = CfgActorScalarFramePlan.HeaderBytes;
    private const int SlotBytes = CfgActorScalarFramePlan.SlotBytes;

    public static void Run()
    {
        var scalar = Compile("scalar42.atto");
        var loop = Compile("loop42.atto");
        Require(CfgReferenceEvaluator.Evaluate(scalar.Graph) == 42 &&
                CfgReferenceEvaluator.Evaluate(loop.Graph) == 42,
            "compiled source oracle disagrees with canonical fixtures");

        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 8,
            MessagesPerDispatch = 2,
            ActorPersistentBytes = 16384,
            ActorScratchBytes = 512,
            ActorStackBytes = 8192,
            ActorMaxStackFrames = 3,
            NativeBudgetBytes = 128 * 1024,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1,
                SharedWorkers = 1,
                UntrustedWorkers = 1,
                QueueCapacityPerDomain = 8
            }
        });

        using var simpleDone = new ManualResetEventSlim();
        using var loopDone = new ManualResetEventSlim();
        ActorContext? expired = null;
        ActorStackFrameHandle? stale = null;
        bool quotaDenied = false;
        long simpleAnswer = -1;
        long loopAnswer = -1;

        // A's fourth nested guest frame must fail. Even if the handler
        // catches the exception, its terminal guest fault latch stops A.
        var actorA = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
        {
            expired = ctx;
            stale = ctx.GuestPushStackFrame(8);
            _ = ctx.GuestPushStackFrame(8);
            _ = ctx.GuestPushStackFrame(8);
            try
            {
                _ = ctx.GuestPushStackFrame(8);
            }
            catch (InvalidOperationException)
            {
                quotaDenied = true;
            }
        });
        var actorB = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
        {
            simpleAnswer = ExecuteVerified(ctx, scalar.Graph, scalar.Plan);
            simpleDone.Set();
        });

        runtime.Send(actorA, new byte[] { 1 });
        runtime.Send(actorB, new byte[] { 2 });
        Require(simpleDone.Wait(TimeSpan.FromSeconds(8)),
            "actor B did not finish compiled source CFG work");
        Require(SpinWait.SpinUntil(
            () => runtime.FaultedTurns == 1 && runtime.CompletedMessages == 1 &&
                  runtime.LiveActors == 1,
            TimeSpan.FromSeconds(8)),
            "failed actor A was not contained while B completed");
        Require(quotaDenied && simpleAnswer == 42,
            "compiled scalar actor B did not survive A's frame quota failure");

        Reject(() => runtime.Send(actorA, new byte[] { 3 }),
            "terminal actor reference remains sendable");
        Reject(() => { _ = expired!.GuestPushStackFrame(8); },
            "expired physical turn reused its guest-frame authority");

        // A foreign actor cannot import the former actor's frame handle.
        // The terminal stale-handle fault must stop only this probe actor.
        var staleProbe = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
        {
            Reject(() => ctx.GuestReadStackFrame(stale!, 0, new byte[1]),
                "old-incarnation guest frame handle entered new actor");
        });
        runtime.Send(staleProbe, new byte[] { 4 });
        Require(SpinWait.SpinUntil(
            () => runtime.FaultedTurns == 2 && runtime.LiveActors == 1,
            TimeSpan.FromSeconds(8)),
            "stale capability fault did not stop only the receiving actor");

        // Replacement actor is an independently minted ActorRef. This is
        // NOT stable-ID supervisor restart; that needs a new runtime ABI.
        var replacement = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
        {
            loopAnswer = ExecuteVerified(ctx, loop.Graph, loop.Plan);
            loopDone.Set();
        });
        runtime.Send(replacement, new byte[] { 5 });
        Require(loopDone.Wait(TimeSpan.FromSeconds(8)) &&
                SpinWait.SpinUntil(
                    () => runtime.CompletedMessages == 2 &&
                          runtime.FaultedTurns == 2 && runtime.LiveActors == 2,
                    TimeSpan.FromSeconds(8)),
            "compiled loop CFG did not finish after failure and stale-handle rejection");
        Require(loopAnswer == 42 && runtime.NativeReservedBytes > 0,
            "source-verified loop failed to run on the replacement actor");

        runtime.Stop(actorB);
        runtime.Stop(replacement);
        Require(SpinWait.SpinUntil(
            () => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(8)),
            "actor-owned native frames leaked after both healthy actors STOP");

        Console.WriteLine(
            "PASS net-source-cfg-actor-frames-v1|source=2|faults=2|peer=42|loop=42|native_refund=1|guest_machine_code=0");
    }

    private static (VerifiedCfgFunction Graph, CfgActorScalarFramePlan Plan) Compile(
        string filename)
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "contracts", "actor-scalar-execution-v1", filename);
        var graph = OresStructuredCfgLowering.Lower(File.ReadAllText(path));
        var plan = CfgActorScalarFramePlan.Create(graph);
        Require(graph.ResultType == IrValueType.I64 &&
                plan.FrameBytes <= 8192 &&
                plan.FrameBytes == ActorScalarFrameAbiV1.FrameBytes(plan.ValueSlots),
            "compiler output exceeds actor-owned native scalar frame limits");
        return (graph, plan);
    }

    private static long ExecuteVerified(ActorContext context,
        VerifiedCfgFunction graph, CfgActorScalarFramePlan plan)
    {
        byte[] types = plan.EncodeTypeTags();
        var frame = context.GuestPushStackFrame(plan.FrameBytes, alignment: 8);
        Span<byte> header = stackalloc byte[HeaderBytes];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x31465441);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], types.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], plan.BlockCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], plan.EntryBlockIndex);
        context.GuestWriteStackFrame(frame, 0, header);

        for (int slot = 0; slot < types.Length; slot++)
        {
            Span<byte> uninitialized = stackalloc byte[SlotBytes];
            uninitialized.Clear();
            uninitialized[1] = types[slot];
            context.GuestWriteStackFrame(frame,
                checked(HeaderBytes + slot * SlotBytes), uninitialized);
        }

        int pc = plan.EntryBlockIndex;
        (byte Tag, long Value)[] edgeValues = [];
        // A finite block fuel is required because bounded frame depth does
        // not bound a loop's total CPU consumption.
        for (int fuel = 0; fuel < 256; fuel++)
        {
            var block = graph.Blocks[pc];
            if (block.Parameters.Count != edgeValues.Length)
                throw new InvalidOperationException("Unverified CFG edge arity");
            for (int i = 0; i < edgeValues.Length; i++)
                WriteSlot(block.Parameters[i].Id, edgeValues[i]);

            Span<byte> pcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(pcBytes, pc);
            context.GuestWriteStackFrame(frame, 12, pcBytes);
            foreach (var op in block.Instructions)
            {
                long L() => ReadI64(op.Left!.Value);
                long R() => ReadI64(op.Right!.Value);
                switch (op.Opcode)
                {
                    case CfgOpcode.ConstantI64:
                        WriteSlot(op.ResultId, (1, op.I64Constant!.Value));
                        break;
                    case CfgOpcode.ConstantBool:
                        WriteSlot(op.ResultId, (2, op.BoolConstant!.Value ? 1L : 0L));
                        break;
                    case CfgOpcode.CheckedAdd:
                        WriteSlot(op.ResultId, (1, checked(L() + R())));
                        break;
                    case CfgOpcode.CheckedSubtract:
                        WriteSlot(op.ResultId, (1, checked(L() - R())));
                        break;
                    case CfgOpcode.CheckedMultiply:
                        WriteSlot(op.ResultId, (1, checked(L() * R())));
                        break;
                    case CfgOpcode.CheckedDivide:
                    {
                        long left = L(), right = R();
                        if (right == 0) throw new DivideByZeroException();
                        if (left == long.MinValue && right == -1)
                            throw new OverflowException();
                        WriteSlot(op.ResultId, (1, left / right));
                        break;
                    }
                    case CfgOpcode.EqualI64:
                        WriteSlot(op.ResultId, (2, L() == R() ? 1L : 0L));
                        break;
                    case CfgOpcode.NotEqualI64:
                        WriteSlot(op.ResultId, (2, L() != R() ? 1L : 0L));
                        break;
                    case CfgOpcode.LessThanI64:
                        WriteSlot(op.ResultId, (2, L() < R() ? 1L : 0L));
                        break;
                    default:
                        throw new InvalidOperationException(
                            "Unsupported compiler CFG effect in guest-frame interpreter");
                }
            }

            switch (block.Terminator)
            {
                case CfgReturn ret:
                {
                    long result = ReadI64(ret.ValueId);
                    // Independent runtime typed ABI agrees with the source
                    // compiler's SSA types and native little-endian slots.
                    Require(ActorScalarFrameAbiV1.ReadI64(
                        context, frame, ret.ValueId) == result,
                        "runtime ABI disagrees with compiler CFG return bytes");
                    context.GuestPopStackFrame(frame);
                    return result;
                }
                case CfgJump jump:
                    Transfer(jump.Target, jump.Arguments);
                    break;
                case CfgBranch branch:
                    bool chooseTrue = ReadBool(branch.Condition);
                    Transfer(chooseTrue ? branch.TrueTarget : branch.FalseTarget,
                        chooseTrue ? branch.TrueArguments : branch.FalseArguments);
                    break;
                default:
                    throw new InvalidOperationException("Unverified CFG terminator");
            }
        }
        throw new InvalidOperationException("Compiled actor CFG exceeded bounded fuel");

        long ReadI64(int slot)
        {
            var value = ReadSlot(slot);
            if (value.Tag != 1)
                throw new InvalidOperationException("Implicit bool-to-int conversion denied");
            return value.Value;
        }

        bool ReadBool(int slot)
        {
            var value = ReadSlot(slot);
            if (value.Tag != 2 || value.Value is not (0 or 1))
                throw new InvalidOperationException("Noncanonical CFG boolean");
            return value.Value == 1;
        }

        (byte Tag, long Value) ReadSlot(int slot)
        {
            if (slot < 0 || slot >= types.Length)
                throw new InvalidOperationException("CFG slot exceeds verified layout");
            Span<byte> raw = stackalloc byte[SlotBytes];
            context.GuestReadStackFrame(frame,
                checked(HeaderBytes + slot * SlotBytes), raw);
            if (raw[0] != types[slot] || raw[1] != types[slot] ||
                raw[2..8].IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidOperationException("Uninitialized or mistyped CFG scalar slot");
            long value = BinaryPrimitives.ReadInt64LittleEndian(raw[8..]);
            if (types[slot] == 2 && value is not (0 or 1))
                throw new InvalidOperationException("Noncanonical CFG bool value");
            return (types[slot], value);
        }

        void WriteSlot(int slot, (byte Tag, long Value) value)
        {
            if (slot < 0 || slot >= types.Length ||
                types[slot] != value.Tag ||
                (value.Tag == 2 && value.Value is not (0 or 1)))
                throw new InvalidOperationException("CFG SSA frame type mismatch");
            Span<byte> raw = stackalloc byte[SlotBytes];
            raw.Clear();
            raw[0] = raw[1] = value.Tag;
            BinaryPrimitives.WriteInt64LittleEndian(raw[8..], value.Value);
            context.GuestWriteStackFrame(frame,
                checked(HeaderBytes + slot * SlotBytes), raw);
        }

        void Transfer(string destination, IReadOnlyList<int> sourceIds)
        {
            int next = plan.BlockIndex(destination);
            var parameters = graph.Blocks[next].Parameters;
            if (parameters.Count != sourceIds.Count)
                throw new InvalidOperationException("CFG edge arity mismatch");
            // Capture all values before writing any destination: a loop edge
            // may swap two SSA slots, so sequential assignment is incorrect.
            var staged = new (byte Tag, long Value)[sourceIds.Count];
            for (int i = 0; i < staged.Length; i++)
                staged[i] = ReadSlot(sourceIds[i]);
            edgeValues = staged;
            pc = next;
        }
    }

    private static void Reject(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected compiled actor-frame rejection: " + why);
    }

    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception("Compiler-backed actor frame: " + why);
    }
}
