using Ores.Net.Runtime;
using Ores.Net.Compiler;

internal static class ActorGuestHeap32SelfTest
{
    public static void Run()
    {
        PerActorReferencesSurviveTurnsAndDenyStaleAccess();
        GenerationExhaustionFailsClosedWithoutWrapping();
        GuestPayloadFastPathDoesNotAllocateClrObjects();
        TypedGuestPayloadFastPathDoesNotAllocateClrObjects();
        GuestReadLoanDelaysArenaReclaimAfterStop();
        TypedNodeGraphEnforcesFieldLayouts();
        CompiledGuestFixtureMustRemainUncertified();
        Console.WriteLine("PASS .NET actor-local 32-bit guest refs (host runtime only; not compiled guest)");
    }

    private static ActorRuntimeOptions Options(int slots = 8) => new()
    {
        MaxActors = 8,
        MessagesPerDispatch = 8,
        MailboxMessages = 32,
        MailboxBytes = 4096,
        ActorPersistentBytes = 2048,
        ActorScratchBytes = 128,
        ActorMaxAllocations = slots,
        ActorMaxLifetimePins = 8,
        ActorMaxReadLoans = 8,
        NativeBudgetBytes = 1024 * 1024,
        Dispatcher = new ActorDispatcherOptions
        {
            PrivateWorkers = 1,
            SharedWorkers = 1,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 16
        }
    };

    private static void PerActorReferencesSurviveTurnsAndDenyStaleAccess()
    {
        using var runtime = new ActorRuntime(Options());
        using var done = new CountdownEvent(4);
        object failureGate = new();
        Exception? failure = null;
        uint leftRoot = 0, rightRoot = 0;
        uint leftStale = 0, rightStale = 0;
        ActorContext? expiredContext = null;
        ActorArenaReadLoan? carriedReadLoan = null;

        ActorMessageHandler Handler(bool left) => (context, message) =>
        {
            try
            {
                Require(message.Length == 1, "fixture message length");
                if (message.Span[0] == 1)
                {
                    uint root = context.AllocateGuestObject32(16);
                    Require(root != 0, "zero remains the null ref");
                    context.WriteGuestObject32(root, 0,
                        new byte[] { left ? (byte)0xA1 : (byte)0xB2 });
                    uint child = context.AllocateGuestObject32(8);
                    context.WriteGuestReference32(root, 8, child);
                    Require(context.ReadGuestReference32(root, 8) == child,
                        "nested pointer occupies one little-endian uint32 word");
                    ExpectInvalid(() => context.WriteGuestReference32(root, 8, uint.MaxValue),
                        "forged target cannot enter a typed field");
                    Require(context.ReadGuestReference32(root, 8) == child,
                        "failed invalid-target write leaves child pointer unchanged");
                    context.DropGuestObject32(child);
                    ExpectInvalid(() => context.ReadGuestReference32(root, 8),
                        "dropped child pointer read must fail");
                    context.WriteGuestReference32(root, 8, 0);
                    if (left)
                    {
                        leftRoot = root;
                        leftStale = child;
                        expiredContext = context;
                        carriedReadLoan = context.BorrowGuestObjectRead32(root);
                    }
                    else
                    {
                        rightRoot = root;
                        rightStale = child;
                    }
                }
                else if (message.Span[0] == 2)
                {
                    uint root = left ? leftRoot : rightRoot;
                    uint stale = left ? leftStale : rightStale;
                    byte[] data = new byte[1];
                    context.ReadGuestObject32(root, 0, data);
                    if (left)
                    {
                        Require(carriedReadLoan is not null,
                            "read loan must survive the preceding physical actor turn");
                        ExpectInvalid(() => context.WriteGuestObject32(root, 0,
                            new byte[] { 99 }),
                            "cross-turn read loan must prohibit guest writes");
                        ExpectInvalid(() => context.DropGuestObject32(root),
                            "cross-turn read loan must prohibit guest drop");
                        carriedReadLoan!.Dispose();
                        carriedReadLoan = null;
                    }
                    Require(data[0] == (left ? (byte)0xA1 : (byte)0xB2),
                        "actor-private object survives physical turn migration");

                    ExpectInvalid(() => context.AllocateGuestObject32(2049),
                        "over-capacity native request cannot consume guest reference slot");
                    using (var loan = context.BorrowGuestObjectRead32(root))
                    {
                        ExpectInvalid(() => context.WriteGuestObject32(root, 0,
                            new byte[] { 77 }),
                            "read loan freezes guest object mutation");
                        ExpectInvalid(() => context.DropGuestObject32(root),
                            "read loan freezes guest object drop");
                    }
                    uint fresh = context.AllocateGuestObject32(8);
                    Require(fresh != stale, "slot reuse increments 16-bit generation");
                    ExpectInvalid(() => context.ReadGuestObject32(stale, 0, data),
                        "stale guest reference is not revived by slot reuse");
                    ExpectInvalid(() => context.WriteGuestObject32(stale, 0, data),
                        "stale guest write cannot mutate new allocation");
                    ExpectInvalid(() => context.DropGuestObject32(stale),
                        "double drop of stale guest reference denied");
                    ExpectOutOfRange(() => context.ReadGuestObject32(root, 16, data),
                        "guest reads must be allocation-bounded");
                    ExpectInvalid(() => context.DropGuestObject32(0),
                        "zero cannot be dropped");
                    context.WriteGuestReference32(root, 8, fresh);
                    Require(context.ReadGuestReference32(root, 8) == fresh,
                        "fresh reference round trip");
                    context.WriteGuestObject32(root, 8, new byte[] { 255, 255, 255, 255 });
                    ExpectInvalid(() => context.ReadGuestReference32(root, 8),
                        "malformed native bytes cannot be elevated to a guest ref");
                    context.WriteGuestReference32(root, 8, 0);
                    context.DropGuestObject32(fresh);
                    context.DropGuestObject32(root);
                }
                else
                {
                    throw new InvalidOperationException("Unexpected test message");
                }
            }
            catch (Exception error)
            {
                lock (failureGate) failure ??= error;
            }
            finally
            {
                done.Signal();
            }
        };

        ActorRef leftActor = runtime.Spawn(ActorKind.Private, Handler(true));
        ActorRef rightActor = runtime.Spawn(ActorKind.Shared, Handler(false));
        Require(runtime.NativeReservedBytes == 2 * (2048 + 128),
            "reference table does not reserve a second native payload arena");

        runtime.Send(leftActor, new byte[] { 1 });
        runtime.Send(rightActor, new byte[] { 1 });
        runtime.Send(leftActor, new byte[] { 2 });
        runtime.Send(rightActor, new byte[] { 2 });
        Require(done.Wait(TimeSpan.FromSeconds(10)), "both actors complete both turns");
        if (failure is not null) throw new Exception("guest ref32 actor fixture failed", failure);
        Require(leftRoot == rightRoot && leftRoot != 0,
            "the same actor-local reference word can map to independent storage");
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 4,
            TimeSpan.FromSeconds(10)) && runtime.FaultedTurns == 0,
            "four actual host actor message turns completed");

        Require(expiredContext is not null, "retained-context fixture");
        ExpectInvalid(() => expiredContext!.AllocateGuestObject32(8),
            "retained context cannot allocate after physical turn");
        ExpectInvalid(() => expiredContext!.ReadGuestObject32(leftRoot, 0, new byte[1]),
            "retained context cannot dereference after physical turn");

        runtime.Stop(leftActor);
        runtime.Stop(rightActor);
        Require(SpinWait.SpinUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(10)),
            "actor retirement must reclaim both physical native payload slabs");
    }

    private static void GenerationExhaustionFailsClosedWithoutWrapping()
    {
        using var runtime = new ActorRuntime(Options(slots: 1));
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        ActorRef actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            try
            {
                uint oldest = 0;
                for (int i = 0; i < ushort.MaxValue; i++)
                {
                    uint reference = context.AllocateGuestObject32(8);
                    if (i == 0) oldest = reference;
                    context.DropGuestObject32(reference);
                }
                ExpectInvalid(() => context.AllocateGuestObject32(8),
                    "generation exhaustion retires the only slot");
                ExpectInvalid(() => context.ReadGuestObject32(oldest, 0, new byte[1]),
                    "oldest reference cannot alias wrapped generation");
            }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        });
        runtime.Send(actor, new byte[] { 1 });
        Require(finished.Wait(TimeSpan.FromSeconds(20)),
            "bounded slot generation rollover fixture completed");
        if (failure is not null) throw new Exception("generation exhaustion fixture failed", failure);
        Require(runtime.FaultedTurns == 0, "generation exhaustion does not fault host actor");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(10)),
            "exhausted slot still permits deterministic native arena retirement");
    }

    /// <summary>
    /// Host actor fixture for the planned compiler-emitted Node { i64, Node? }.
    /// This proves the checked runtime ABI only; it does NOT run compiled .atto.
    /// </summary>
    private static void TypedNodeGraphEnforcesFieldLayouts()
    {
        const uint nodeType = 0xAA01;
        const uint otherType = 0xAA02;
        var node = new ActorGuestLayout32(nodeType, 16,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64),
                    new ActorGuestField32(8, ActorGuestFieldKind32.Ref32, nodeType) });
        var other = new ActorGuestLayout32(otherType, 16,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64),
                    new ActorGuestField32(8, ActorGuestFieldKind32.Ref32, otherType) });

        ExpectOutOfRange(() => _ = new ActorGuestLayout32(0, 16,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64) }),
            "null nominal type identity rejected");
        ExpectOutOfRange(() => _ = new ActorGuestLayout32(nodeType, 12,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64) }),
            "unaligned object size rejected");
        ExpectArgument(() => _ = new ActorGuestLayout32(nodeType, 16,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64),
                    new ActorGuestField32(4, ActorGuestFieldKind32.Ref32, nodeType) }),
            "overlapping scalar and reference fields rejected");
        ExpectArgument(() => _ = new ActorGuestLayout32(nodeType, 16,
            new[] { new ActorGuestField32(12, ActorGuestFieldKind32.I64) }),
            "out-of-bounds i64 field rejected");
        ExpectArgument(() => _ = new ActorGuestLayout32(nodeType, 16,
            new[] { new ActorGuestField32(8, ActorGuestFieldKind32.Ref32) }),
            "untyped reference field rejected");
        ExpectArgument(() => _ = new ActorGuestLayout32(nodeType, 16,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64, nodeType) }),
            "scalar field cannot claim a reference type");

        var equivalent = new ActorGuestLayout32(nodeType, 16,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64),
                    new ActorGuestField32(8, ActorGuestFieldKind32.Ref32, nodeType) });
        var colliding = new ActorGuestLayout32(nodeType, 16,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64),
                    new ActorGuestField32(8, ActorGuestFieldKind32.I64) });
        var oversized = new ActorGuestLayout32(0xAA03, 4096,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64) });
        var retryLayout = new ActorGuestLayout32(0xAA03, 16,
            new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64) });

        using var runtime = new ActorRuntime(Options());
        using var done = new CountdownEvent(2);
        Exception? failure = null;
        uint root = 0;
        var actor = runtime.Spawn(ActorKind.Private, (context, data) =>
        {
            try
            {
                if (data.Span[0] == 1)
                {
                    root = context.AllocateTypedGuestObject32(node);
                    ExpectInvalid(() => context.AllocateTypedGuestObject32(colliding),
                        "same nominal type ID cannot bind a different field shape");
                    uint equivalentRef = context.AllocateTypedGuestObject32(equivalent);
                    context.DropGuestObject32(equivalentRef);
                    ExpectInvalid(() => context.AllocateTypedGuestObject32(oversized),
                        "failed oversized layout admission must not consume type ID");
                    uint retried = context.AllocateTypedGuestObject32(retryLayout);
                    context.DropGuestObject32(retried);
                    uint child = context.AllocateTypedGuestObject32(node);
                    uint wrong = context.AllocateTypedGuestObject32(other);
                    uint raw = context.AllocateGuestObject32(8);

                    Span<byte> scalar = stackalloc byte[8];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(scalar, 42);
                    context.WriteGuestObject32(child, 0, scalar);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(scalar, 99);
                    context.WriteGuestObject32(root, 0, scalar);
                    ExpectInvalid(() => context.WriteGuestObject32(root, 0,
                        new byte[] { 0xFF }),
                        "typed I64 write cannot mutate a partial scalar");
                    ExpectInvalid(() => context.ReadGuestObject32(root, 0,
                        new byte[1]),
                        "typed I64 read cannot observe a partial scalar");
                    ExpectInvalid(() => context.WriteGuestObject32(root, 4,
                        new byte[4]),
                        "typed I64 write must start at its declared offset");
                    context.WriteGuestReference32(root, 8, child);
                    Require(context.ReadGuestReference32(root, 8) == child,
                        "typed Node.next points to child Node");

                    ExpectInvalid(() => context.WriteGuestReference32(root, 0, child),
                        "cannot write reference into i64 field");
                    ExpectInvalid(() => context.WriteGuestReference32(root, 8, wrong),
                        "nominal mismatch cannot enter Node.next");
                    ExpectInvalid(() => context.WriteGuestReference32(root, 8, raw),
                        "untyped allocation cannot enter nominal Node.next");
                    ExpectInvalid(() => context.WriteGuestObject32(root, 8,
                        new byte[] { 0, 0, 0, 0 }),
                        "raw bytes cannot erase typed reference field");
                    ExpectInvalid(() => context.WriteGuestObject32(root, 6,
                        new byte[] { 1, 2, 3, 4 }),
                        "raw writes cannot straddle scalar and ref fields");
                    ExpectInvalid(() => context.ReadGuestObject32(root, 8,
                        new byte[4]),
                        "raw reads cannot reinterpret typed refs as bytes");
                    Require(context.ReadGuestReference32(root, 8) == child,
                        "rejected overwrites preserve valid child pointer");

                    context.DropGuestObject32(wrong);
                    context.DropGuestObject32(raw);
                    // The parent remains live across a physical turn, with
                    // its child reference encoded in the native slab.
                }
                else
                {
                    Span<byte> scalar = stackalloc byte[8];
                    context.ReadGuestObject32(root, 0, scalar);
                    Require(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
                        scalar) == 99, "typed root value survives actor turn");
                    uint child = context.ReadGuestReference32(root, 8);
                    context.ReadGuestObject32(child, 0, scalar);
                    Require(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
                        scalar) == 42, "typed child value survives actor turn");
                    context.DropGuestObject32(child);
                    ExpectInvalid(() => context.ReadGuestReference32(root, 8),
                        "stale typed link rejected after child drop");
                    context.WriteGuestReference32(root, 8, 0);
                    Require(context.ReadGuestReference32(root, 8) == 0,
                        "typed null is the canonical four-byte zero");
                    context.DropGuestObject32(root);
                    ExpectInvalid(() => context.AllocateTypedGuestObject32(colliding),
                        "type ID remains sealed after its final guest instance drops");
                }
            }
            catch (Exception error) { failure = error; }
            finally { done.Signal(); }
        });

        runtime.Send(actor, new byte[] { 1 });
        runtime.Send(actor, new byte[] { 2 });
        Require(done.Wait(TimeSpan.FromSeconds(10)),
            "typed graph executes in two real host actor turns");
        if (failure is not null)
            throw new Exception("typed guest object graph failed", failure);
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 2,
            TimeSpan.FromSeconds(10)) && runtime.FaultedTurns == 0,
            "two typed graph host actor turns completed");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(10)),
            "typed graph frees native actor heap on STOP");
    }

    private static void CompiledGuestFixtureMustRemainUncertified()
    {
        // The host-runtime graph is NOT an executable guest. The real .atto
        // source must currently fail before CIL/NativeAOT emission, rather
        // than being silently interpreted as scalar i64 or host C# objects.
        string path = Path.Combine(AppContext.BaseDirectory,
            "contracts", "actor-guest-heap", "v1", "node_graph.atto");
        string source = File.ReadAllText(path);
        Require(source.Contains("define class Node", StringComparison.Ordinal) &&
                source.Contains("define actor HeapGraphProbe", StringComparison.Ordinal),
            "guest fixture must retain nominal Node and actor declarations");

        bool denied = false;
        try
        {
            _ = OresStructuredParser.Parse(source);
        }
        catch (FormatException exception) when
            (exception.Message.Contains(
                "Nominal class/actor methods require", StringComparison.Ordinal))
        {
            denied = true;
        }
        Require(denied,
            "unsupported .atto nominal object graph must fail before CIL lowering");
    }

    private static void ExpectArgument(Action action, string why)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("ActorGuestHeap32 expected malformed layout rejection: " + why);
    }

    // This measures the actual ActorRuntime -> ActorContext -> ActorGuestHeap32
    // native payload path, not the separate ActorNativeArena allocator.
    // It excludes per-turn ActorContext creation and borrowing/loan creation:
    // those are metadata operations and are not zero-GC-certified here.
    private static void GuestReadLoanDelaysArenaReclaimAfterStop()
    {
        using var runtime = new ActorRuntime(Options(slots: 4));
        using var entered = new ManualResetEventSlim();
        ActorArenaReadLoan? outstanding = null;
        Exception? failure = null;
        ActorRef actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            try
            {
                uint guest = context.AllocateGuestObject32(8);
                context.WriteGuestObject32(guest, 0, new byte[] { 0x5A });
                outstanding = context.BorrowGuestObjectRead32(guest);
            }
            catch (Exception error) { failure = error; }
            finally { entered.Set(); }
        });

        long reserved = runtime.NativeReservedBytes;
        Require(reserved == 2048 + 128, "single actor uses one native arena budget");
        runtime.Send(actor, new byte[] { 1 });
        Require(entered.Wait(TimeSpan.FromSeconds(10)),
            "read loan was acquired in admitted host actor turn");
        if (failure is not null)
            throw new Exception("guest loan STOP fixture failed", failure);
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 1,
            TimeSpan.FromSeconds(10)), "loan holder physical turn exited");
        Require(outstanding is not null, "read loan created by guest payload");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0,
            TimeSpan.FromSeconds(10)), "actor logically retired after STOP");
        Require(runtime.NativeReservedBytes == reserved,
            "guest read loan pins native backing after logical actor STOP");
        Task.Run(outstanding!.Dispose).GetAwaiter().GetResult();
        outstanding.Dispose();
        Require(SpinWait.SpinUntil(() => runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(10)),
            "off-carrier guest read-loan release permits exactly-once native refund");
    }

    private static void TypedGuestPayloadFastPathDoesNotAllocateClrObjects()
    {
        var layout = new ActorGuestLayout32(0xB601, 16,
            new[]
            {
                new ActorGuestField32(0, ActorGuestFieldKind32.I64),
                new ActorGuestField32(8, ActorGuestFieldKind32.Ref32, 0xB601)
            });
        using var runtime = new ActorRuntime(Options(slots: 4));
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            try
            {
                Span<byte> data = stackalloc byte[8];
                // Exclude one-time actor metadata, type-ID dictionary
                // admission, JIT/NativeAOT static initialization and locks.
                for (int warmup = 0; warmup < 128; warmup++)
                {
                    uint reference = context.AllocateTypedGuestObject32(layout);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                        data, warmup);
                    context.WriteGuestObject32(reference, 0, data);
                    context.WriteGuestReference32(reference, 8, 0);
                    context.DropGuestObject32(reference);
                }

                bool correct = true;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 4096; i++)
                {
                    uint reference = context.AllocateTypedGuestObject32(layout);
                    if (reference == 0)
                    {
                        correct = false;
                        break;
                    }
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                        data, i);
                    context.WriteGuestObject32(reference, 0, data);
                    data.Clear();
                    context.ReadGuestObject32(reference, 0, data);
                    if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
                            data) != i)
                        correct = false;
                    context.WriteGuestReference32(reference, 8, 0);
                    context.DropGuestObject32(reference);
                    if (!correct)
                        break;
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(correct, "typed native I64/ref32 payload retained values");
                Require(allocated == 0,
                    "warmed typed guest object path must allocate zero CLR bytes");
            }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        });
        runtime.Send(actor, new byte[] { 1 });
        Require(finished.Wait(TimeSpan.FromSeconds(20)),
            "typed native guest fast-path probe finished");
        if (failure is not null)
            throw new Exception("typed native guest allocation probe failed", failure);
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 1,
            TimeSpan.FromSeconds(10)) && runtime.FaultedTurns == 0,
            "typed native fast-path actor turn completed");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0 &&
            runtime.NativeReservedBytes == 0, TimeSpan.FromSeconds(10)),
            "typed fast-path native actor slab reclaimed");
    }

    private static void GuestPayloadFastPathDoesNotAllocateClrObjects()
    {
        using var runtime = new ActorRuntime(Options(slots: 4));
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            try
            {
                Span<byte> data = stackalloc byte[8];
                // Warm JIT, lazy guest metadata, locks, and primitive APIs.
                for (int i = 0; i < 128; i++)
                {
                    uint warm = context.AllocateGuestObject32(8);
                    data[0] = (byte)i;
                    context.WriteGuestObject32(warm, 0, data);
                    context.ReadGuestObject32(warm, 0, data);
                    context.DropGuestObject32(warm);
                }

                bool correct = true;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 4096; i++)
                {
                    uint reference = context.AllocateGuestObject32(8);
                    if (reference == 0)
                    {
                        correct = false;
                        break;
                    }
                    data[0] = (byte)i;
                    context.WriteGuestObject32(reference, 0, data);
                    data[0] = 0;
                    context.ReadGuestObject32(reference, 0, data);
                    if (data[0] != (byte)i)
                        correct = false;
                    context.DropGuestObject32(reference);
                    if (!correct)
                        break;
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(correct, "guest payload read/write/drop preserved values");
                Require(allocated == 0,
                    "steady-state guest payload allocation must not allocate CLR objects");
            }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        });
        runtime.Send(actor, new byte[] { 1 });
        Require(finished.Wait(TimeSpan.FromSeconds(20)),
            "primitive guest payload allocation probe completed");
        if (failure is not null)
            throw new Exception("guest payload allocation probe failed", failure);
        Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 1,
            TimeSpan.FromSeconds(10)) && runtime.FaultedTurns == 0,
            "fast-path actor turn completed without host fault");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0 &&
            runtime.NativeReservedBytes == 0, TimeSpan.FromSeconds(10)),
            "native fast-path arena reclaimed without retained payloads");
    }

    private static void Require(bool condition, string why)
    {
        if (!condition) throw new Exception("ActorGuestHeap32 FAILED: " + why);
    }

    private static void ExpectOutOfRange(Action action, string why)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("ActorGuestHeap32 expected bounds rejection: " + why);
    }

    private static void ExpectInvalid(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("ActorGuestHeap32 expected rejection: " + why);
    }
}
