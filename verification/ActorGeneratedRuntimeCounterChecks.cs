using System.Buffers.Binary;
using System.Collections.Concurrent;
using Ores.Net.Runtime;
using Oreslang.Generated;

// Shared managed/NativeAOT execution of statically compiled emitter output.
// Observers are host test plumbing; actor-to-supervisor delivery is real mail.
internal static class ActorGeneratedRuntimeCounterChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Native actor Counter: " + message); }

    private static void Wait(Func<bool> condition, string message)
    { Require(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(15)), message); }

    public static void Run()
    {
        using var runtime = new ActorRuntime(new ActorRuntimeOptions {
            MaxActors = 16, ActorPersistentBytes = 256, ActorScratchBytes = 64,
            ActorStackBytes = 16, NativeBudgetBytes = 64 * 1024
        });
        var replies = new ConcurrentQueue<(ulong Actor, long Value)>();
        ActorRef supervisor = runtime.Spawn(ActorKind.Shared, (_, bytes) => {
            Require(bytes.Length == 16, "bad result envelope");
            replies.Enqueue((BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.Span[8..])));
        });
        ActorRef first = Actor_Counter.SpawnRuntime(runtime, supervisor);
        ActorRef second = Actor_Counter.SpawnRuntime(runtime, supervisor);
        for (int i = 0; i < 3; i++) runtime.Send(first, ReadOnlySpan<byte>.Empty);
        for (int i = 0; i < 2; i++) runtime.Send(second, ReadOnlySpan<byte>.Empty);
        Wait(() => replies.Count == 5, "source counters did not reply through supervisor mailbox");
        Require(replies.Where(x => x.Actor == first.Id).Select(x => x.Value).SequenceEqual(new long[] { 1, 2, 3 }), "first private state");
        Require(replies.Where(x => x.Actor == second.Id).Select(x => x.Value).SequenceEqual(new long[] { 1, 2 }), "second private state");

        // Actual source-generated checked overflow faults only this actor.
        ActorRef overflow = Actor_OverflowCounter.SpawnRuntime(runtime, supervisor);
        runtime.Send(overflow, ReadOnlySpan<byte>.Empty);
        Wait(() => runtime.FaultedTurns == 1, "overflow did not fault source actor");
        runtime.Send(second, ReadOnlySpan<byte>.Empty);
        Wait(() => replies.Count == 6, "peer failed to progress after source actor overflow");
        Require(replies.Last().Actor == second.Id && replies.Last().Value == 3, "peer state changed by failed actor");
        ActorRef fresh = Actor_Counter.SpawnRuntime(runtime, supervisor);
        runtime.Send(fresh, ReadOnlySpan<byte>.Empty);
        Wait(() => replies.Count == 7, "fresh source actor failed after peer retirement");
        Require(replies.Last().Actor == fresh.Id && replies.Last().Value == 1, "fresh actor inherited state");

        // A distinct compiler-checked .atto source form takes one owned i64
        // mailbox argument. The statically generated method decodes the wire
        // before entering actor-owned native state; replies are produced by
        // actual private ActorRuntime DATA turns (never by a host replay loop).
        ActorRef messageA = Actor_MessageCounter.SpawnRuntime(runtime, supervisor);
        ActorRef messageB = Actor_MessageCounter.SpawnRuntime(runtime, supervisor);
        Span<byte> incoming = stackalloc byte[8];
        foreach (long value in new long[] { 1, 2, 3, 4 })
        {
            BinaryPrimitives.WriteInt64LittleEndian(incoming, value);
            runtime.Send(messageA, incoming);
        }
        foreach (long value in new long[] { 7, 8 })
        {
            BinaryPrimitives.WriteInt64LittleEndian(incoming, value);
            runtime.Send(messageB, incoming);
        }
        Wait(() => replies.Count == 13, "typed-mail actors did not issue all real mailbox replies");
        Require(replies.Where(x => x.Actor == messageA.Id).Select(x => x.Value)
                .SequenceEqual(new long[] { 1, 3, 6, 10 }),
            "typed-mail counter must produce exact canonical 1|3|6|10 prefix");
        Require(replies.Where(x => x.Actor == messageB.Id).Select(x => x.Value)
                .SequenceEqual(new long[] { 7, 15 }),
            "typed-mail second actor reused another actor's native state");

        // Signed i64 arithmetic overflows without a partial state write. An
        // overflowing source-generated actor must not poison its sibling.
        BinaryPrimitives.WriteInt64LittleEndian(incoming, long.MaxValue);
        runtime.Send(messageA, incoming);
        Wait(() => runtime.FaultedTurns >= 2, "typed-mail overflow was not fail-stop");
        BinaryPrimitives.WriteInt64LittleEndian(incoming, 1);
        runtime.Send(messageB, incoming);
        Wait(() => replies.Count == 14, "typed-mail peer stopped after sibling overflow");
        Require(replies.Where(x => x.Actor == messageB.Id).Select(x => x.Value)
                .SequenceEqual(new long[] { 7, 15, 16 }),
            "typed-mail overflow damaged sibling's private native state");

        // Signed wire decoding must not accidentally coerce negative input
        // into an unsigned delta. This remains one checked actor-local turn.
        BinaryPrimitives.WriteInt64LittleEndian(incoming, -4);
        runtime.Send(messageB, incoming);
        Wait(() => replies.Count == 15, "negative signed mailbox value was not processed");
        Require(replies.Where(x => x.Actor == messageB.Id).Select(x => x.Value)
                .SequenceEqual(new long[] { 7, 15, 16, 12 }),
            "signed mailbox input was not decoded as i64");

        // Every invalid frame length must terminate only its receiving actor;
        // one malformed message may not turn into a partial state write.
        foreach (var length in new[] { 0, 7, 9 })
        {
            ActorRef malformedMail = Actor_MessageCounter.SpawnRuntime(runtime, supervisor);
            runtime.Send(malformedMail, new byte[length]);
        }
        Wait(() => runtime.FaultedTurns >= 5,
            "malformed typed-mail lengths must fail before native state mutation");
        Require(replies.Count == 15, "malformed mailbox frames generated spurious replies");

        // Direct runtime negative: rollback and finally must release the exact
        // 16-byte stack budget while preserving this execution context.
        var checkedState = new ActorNativeScalarState(new long[] { long.MaxValue }, new byte[] { 1 });
        var checkedResult = new ConcurrentQueue<long>();
        ActorRef checkedActor = runtime.Spawn(ActorKind.Private, (context, _) => {
            ActorExecutionContext execution = context.Execution;
            try { checkedState.AddInt64(context, 0, 1); throw new Exception("overflow admitted"); }
            catch (OverflowException) { }
            checkedResult.Enqueue(checkedState.AddInt64(context, 0, 0));
            Require(ReferenceEquals(execution, context.Execution), "critical operation replaced execution context");
        });
        runtime.Send(checkedActor, ReadOnlySpan<byte>.Empty);
        Wait(() => checkedResult.Count == 1, "rollback or stack cleanup failed");
        Require(checkedResult.Single() == long.MaxValue, "overflow partially committed state");

        var rejected = new ConcurrentQueue<bool>();
        ActorRef foreign = runtime.Spawn(ActorKind.Shared, (context, _) => {
            try { checkedState.AddInt64(context, 0, -1); rejected.Enqueue(false); }
            catch (InvalidOperationException) { rejected.Enqueue(true); }
        });
        runtime.Send(foreign, ReadOnlySpan<byte>.Empty);
        Wait(() => rejected.Count == 1, "foreign actor check did not execute");
        Require(rejected.Single(), "foreign actor acquired private native state");
        runtime.Send(checkedActor, ReadOnlySpan<byte>.Empty);
        Wait(() => checkedResult.Count == 2, "foreign attempt damaged rightful owner");
        Require(checkedResult.All(value => value == long.MaxValue), "foreign attempt mutated state");
        // Canonical multi-file external receive source, compiled to a closed
        // NativeAOT handler rather than a host-synthesized fixed counter tick.
        // ActorMail<int> is eight little-endian bytes; on input 4 the private
        // receive calls self.end(), which retires its actor generation.
        int actorsBefore = runtime.LiveActors;
        ActorRef declaredA = DeclaredActor_Counter.SpawnRuntime(runtime, supervisor);
        ActorRef declaredB = DeclaredActor_Counter.SpawnRuntime(runtime, supervisor);
        foreach (long input in new long[] { 1, 2, 3, 4 })
        {
            BinaryPrimitives.WriteInt64LittleEndian(incoming, input);
            runtime.Send(declaredA, incoming);
        }
        foreach (long input in new long[] { 7, 8, 4 })
        {
            BinaryPrimitives.WriteInt64LittleEndian(incoming, input);
            runtime.Send(declaredB, incoming);
        }
        Wait(() => replies.Count(x => x.Actor == declaredA.Id) == 4 &&
                   replies.Count(x => x.Actor == declaredB.Id) == 3,
            "compiled external receive did not deliver all source-computed replies");
        Require(replies.Where(x => x.Actor == declaredA.Id).Select(x => x.Value)
                .SequenceEqual(new long[] { 1, 3, 6, 10 }),
            "declared external receiver A computed wrong state");
        Require(replies.Where(x => x.Actor == declaredB.Id).Select(x => x.Value)
                .SequenceEqual(new long[] { 7, 15, 19 }),
            "declared external receiver B shared actor-private state");
        Wait(() => runtime.LiveActors == actorsBefore,
            "compiled self.end did not retire both private actor incarnations");
        bool staleStopDenied = false;
        try { runtime.Send(declaredA, incoming); }
        catch (InvalidOperationException) { staleStopDenied = true; }
        Require(staleStopDenied, "retired external receive actor accepted stale send");

        long faultsBeforeDeclared = runtime.FaultedTurns;
        ActorRef malformedDeclared = DeclaredActor_Counter.SpawnRuntime(runtime, supervisor);
        runtime.Send(malformedDeclared, ReadOnlySpan<byte>.Empty);
        Wait(() => runtime.FaultedTurns > faultsBeforeDeclared,
            "malformed ActorMail<int> did not fault its private receiver");
        Wait(() => runtime.LiveActors == actorsBefore,
            "malformed external receiver was not retired");
        ActorRef declaredFresh = DeclaredActor_Counter.SpawnRuntime(runtime, supervisor);
        BinaryPrimitives.WriteInt64LittleEndian(incoming, 1);
        runtime.Send(declaredFresh, incoming);
        Wait(() => replies.Any(x => x.Actor == declaredFresh.Id),
            "fresh external receiver did not survive sibling fault");
        Require(replies.Where(x => x.Actor == declaredFresh.Id).Single().Value == 1,
            "fresh declared receiver inherited stale state");
        runtime.Stop(declaredFresh);
        Wait(() => runtime.LiveActors == actorsBefore,
            "fresh external receiver did not reclaim its actor generation");
        Console.WriteLine("DECLARED_EXTERNAL_RECEIVE_NATIVE_PASS source_handler=true two_actor_isolation=true self_end=true stale_send_denied=true malformed_mail_fault=true");

        runtime.Dispose();
        Require(runtime.NativeReservedBytes == 0, "native state/frame budget leaked at retirement");
        CheckSchedulerQuotaRefund();
        Console.WriteLine("ACTOR_GENERATED_PRODUCTION_RUNTIME_PASS source_mailbox=true native_state=true execution_stack=true overflow_peer_survival=true retired_native_bytes=0 scalar_i64_mail=true mailbox_counter_1_3_6_10=true");
    }

    private static void CheckSchedulerQuotaRefund()
    {
        var host = new OresVmHost(maxQueuedMailBytes: 3);
        var tenant = host.CreateIsolate("quota-refund", maxQueuedMailBytes: 3);
        host.Start(tenant);
        var actor = Actor_Counter.Create(host, tenant, 1);
        var scheduler = host.CreateTrustedScheduler();
        host.SendActor(tenant, actor, actor.CurrentRef, new byte[] { 1, 2, 3 });
        scheduler.Enqueue(tenant, actor);
        Require(host.QueuedMail(tenant) == (3, 3), "queued data was not charged");
        byte[]? data = scheduler.DequeueUser(tenant, actor);
        Require(data is not null && data.SequenceEqual(new byte[] { 1, 2, 3 }), "scheduled user dequeue changed data");
        Require(host.QueuedMail(tenant) == (0, 0), "scheduled dequeue did not refund tenant/host quotas");
        Require(scheduler.DequeueUser(tenant, actor) is null && scheduler.PendingMessages(tenant, actor) == 1,
            "user dequeue consumed control authority");
        Require(Actor_Counter.Dispatch_increment(scheduler, tenant, actor) == 1, "control lost after refund");
        host.SendActor(tenant, actor, actor.CurrentRef, new byte[] { 4, 5, 6 });
        Require(scheduler.DequeueUser(tenant, actor)?.Length == 3 && host.QueuedMail(tenant) == (0, 0),
            "refunded byte capacity was not reusable");
        host.RetireActor(tenant, actor);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
    }
}
