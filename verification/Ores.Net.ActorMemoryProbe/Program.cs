using System.Collections.Concurrent;
using Ores.Net.Runtime;

static void Require(bool ok, string why)
{
    if (!ok) throw new InvalidOperationException(why);
}
static void Deny(Action operation)
{
    try { operation(); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("foreign actor memory authority admitted");
}

// Keep all probe actor cardinalities and dispatcher admission capacity in lockstep.
const int actorCount = 4;
var options = new ActorRuntimeOptions
{
    MaxActors = actorCount, MessagesPerDispatch = 1,
    MailboxMessages = 4, MailboxBytes = 64,
    ActorPersistentBytes = 128, ActorScratchBytes = 64,
    ActorStackBytes = 32, ActorMaxStackFrames = 4,
    ActorMaxAllocations = 8, ActorMaxReadLoans = 8,
    ActorMaxLifetimePins = 8, NativeBudgetBytes = 4096,
    Dispatcher = new ActorDispatcherOptions
    {
        PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
        QueueCapacityPerDomain = actorCount
    }
};
using var runtime = new ActorRuntime(options);
using var initialized = new CountdownEvent(actorCount);
using var verified = new CountdownEvent(actorCount);
using var survivorVerified = new AutoResetEvent(false);
var errors = new ConcurrentQueue<Exception>();
var handles = new ActorArenaHandle?[actorCount];
var frames = new ActorStackFrameHandle?[actorCount];
var references = new ActorRef[actorCount];
ActorArenaLifetimePin? pin = null;
ActorContext? retained = null;
// Two SHARED actors intentionally use the same carrier pool; kind/domain
// identity must never substitute for per-actor heap identity.
var kinds = new[] { ActorKind.Private, ActorKind.Shared, ActorKind.Shared, ActorKind.Untrusted };
Require(kinds.Length == actorCount, "probe actor-kind count disagrees with admission capacity");
for (int i = 0; i < actorCount; ++i)
{
    int index = i;
    references[i] = runtime.Spawn(kinds[i], (context, message) =>
    {
        byte phase = message.Span[0];
        bool first = phase == 0;
        try
        {
            if (first)
            {
                handles[index] = context.AllocatePersistent(8);
                context.Write(handles[index]!, 0,
                    new byte[] { (byte)(11 + index), 0, 0, 0, 0, 0, 0, 0 });
                frames[index] = context.PushStackFrame(8);
                context.WriteStackFrame(frames[index]!, 0,
                    new byte[] { (byte)(31 + index) });
            }
            else if (phase >= 2)
            {
                Require(index == 2 && (phase == 2 || phase == 3),
                    "unexpected survivor probe phase or actor");
                var output = new byte[8];
                context.Read(handles[index]!, 0, output);
                Require(output[0] == (phase == 2 ? 13 : 14),
                    "survivor state changed during peer retirement");
                var frameValue = new byte[1];
                context.ReadStackFrame(frames[index]!, 0, frameValue);
                Require(frameValue[0] == (byte)(31 + index),
                    "survivor logical frame changed during peer retirement");
                Deny(() => context.Read(handles[1]!, 0, output));
                Deny(() => context.Write(handles[1]!, 0, output));
                output[0]++;
                context.Write(handles[index]!, 0, output);
                context.Read(handles[index]!, 0, output);
                Require(output[0] == (phase == 2 ? 14 : 15),
                    "survivor write failed during peer retirement");
            }
            else
            {
                var output = new byte[8];
                context.Read(handles[index]!, 0, output);
                Require(output[0] == 11 + index, "persistent actor value changed");
                var frameValue = new byte[1];
                context.ReadStackFrame(frames[index]!, 0, frameValue);
                Require(frameValue[0] == (byte)(31 + index),
                    "actor-owned logical frame did not survive physical turn");
                for (int j = 0; j < actorCount; ++j)
                {
                    if (j == index) continue;
                    int foreign = j;
                    Deny(() => context.Read(handles[foreign]!, 0, output));
                    Deny(() => context.Write(handles[foreign]!, 0, output));
                    Deny(() => context.ReadStackFrame(frames[foreign]!, 0, frameValue));
                    Deny(() => context.WriteStackFrame(frames[foreign]!, 0, frameValue));
                }
                context.Read(handles[index]!, 0, output);
                Require(output[0] == 11 + index, "foreign access changed live bytes");
                if (index == 1)
                {
                    retained = context;
                    pin = context.PinLifetime();
                }
            }
        }
        catch (Exception failure) { errors.Enqueue(failure); }
        finally
        {
            if (first) initialized.Signal();
            else if (phase == 1) verified.Signal();
            else survivorVerified.Set();
        }
    });
}
Require(runtime.StartedCarrierThreads == 0, "spawn created actor carrier threads");
foreach (var actor in references) runtime.Send(actor, new byte[] { 0 });
Require(initialized.Wait(TimeSpan.FromSeconds(10)), "initialization timed out");
Require(errors.IsEmpty, "initial actor allocation failed");
foreach (var actor in references) runtime.Send(actor, new byte[] { 1 });
Require(verified.Wait(TimeSpan.FromSeconds(10)), "isolation checks timed out");
Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 2 * actorCount,
    TimeSpan.FromSeconds(10)), "physical turns did not complete");
Require(errors.IsEmpty, "actor isolation check failed");
Require(runtime.StartedCarrierThreads == 3, "carrier pools exceeded configured bounds");
Deny(() => retained!.AllocateScratch(8));
// Retire one SHARED actor while its same-pool peer continues taking turns.
long allReserved = runtime.NativeReservedBytes;
Require(allReserved > 0 && allReserved % actorCount == 0,
    "identical actors must have equal native reservations");
long oneReserved = allReserved / actorCount;
runtime.Stop(references[1]);
Require(SpinWait.SpinUntil(() => runtime.LiveActors == actorCount - 1,
    TimeSpan.FromSeconds(10)), "pinned actor logical retirement did not complete");
Require(runtime.NativeReservedBytes == allReserved,
    "logical STOP must retain the pinned actor reservation");
runtime.Send(references[2], new byte[] { 2 });
Require(survivorVerified.WaitOne(TimeSpan.FromSeconds(10)),
    "survivor did not execute while peer was pinned");
Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 2 * actorCount + 1,
    TimeSpan.FromSeconds(10)), "pinned-peer survivor turn did not finish");
Require(errors.IsEmpty, "survivor failed while peer was pinned");
Task.Run(() => pin!.Dispose()).GetAwaiter().GetResult();
pin!.Dispose();
Require(SpinWait.SpinUntil(() => runtime.NativeReservedBytes == allReserved - oneReserved,
    TimeSpan.FromSeconds(10)), "physical reclaim must refund only the stopped actor");
runtime.Send(references[2], new byte[] { 3 });
Require(survivorVerified.WaitOne(TimeSpan.FromSeconds(10)),
    "survivor did not execute after peer physical reclaim");
Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 2 * actorCount + 2,
    TimeSpan.FromSeconds(10)), "post-reclaim survivor turn did not finish");
Require(errors.IsEmpty, "survivor failed after peer physical reclaim");
for (int i = 0; i < actorCount; ++i)
    if (i != 1) runtime.Stop(references[i]);
Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0 &&
    runtime.NativeReservedBytes == 0, TimeSpan.FromSeconds(10)),
    "remaining actors did not independently retire and refund");
Console.WriteLine("PASS actor-memory-survivor: same-pool peer reads and writes during pinned STOP and after physical reclaim");
Console.WriteLine("PASS actor-memory-native-probe: four private heaps (two shared-pool actors), foreign handles denied, pooled carriers, stale context denied, pinned STOP and physical reclaim");
Console.WriteLine("PASS actor-logical-stack-native-probe: four distinct actor-owned frames, cross-turn reads and foreign-handle denial");

// The first production typed-frame backend uses this same native actor-owned
// stack slab. Check the immutable *trusted compiler schema* in a published
// NativeAOT binary across two independently admitted physical turns; never
// confuse this host ABI probe with compiled .atto actor/await execution.
using var typedRuntime = new ActorRuntime(new ActorRuntimeOptions
{
    MaxActors = 1, MessagesPerDispatch = 1,
    MailboxMessages = 2, MailboxBytes = 64,
    ActorPersistentBytes = 256, ActorScratchBytes = 64,
    ActorStackBytes = 96, ActorMaxStackFrames = 4,
    ActorMaxAllocations = 8, ActorMaxReadLoans = 8,
    ActorMaxLifetimePins = 8, NativeBudgetBytes = 4096,
    Dispatcher = new ActorDispatcherOptions
    {
        PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
        QueueCapacityPerDomain = 1
    }
});
ActorStackFrameHandle? typedActivation = null;
var guardedActor = typedRuntime.Spawn(ActorKind.Private, (ctx, message) =>
{
    if (message.Span[0] == 1)
    {
        byte[] mutableTypes = { 1, 2 };
        typedActivation = ActorScalarFrameAbiV1.Push(ctx, mutableTypes, blockCount: 1);
        mutableTypes[0] = 2; // an external caller must never retype the frame
        ActorScalarFrameAbiV1.WriteI64(ctx, typedActivation, 0, long.MinValue);
        ActorScalarFrameAbiV1.WriteBool(ctx, typedActivation, 1, true);
        return;
    }

    Require(message.Span[0] == 2, "unexpected scalar ABI probe phase");
    Require(ActorScalarFrameAbiV1.ReadI64(ctx, typedActivation!, 0) ==
            long.MinValue && ActorScalarFrameAbiV1.ReadBool(ctx, typedActivation!, 1),
        "typed scalar frame failed cross-turn read with sealed source kinds");
    byte[] original = new byte[ActorScalarFrameAbiV1.SlotBytes];
    ctx.ReadStackFrame(typedActivation!, ActorScalarFrameAbiV1.HeaderBytes, original);
    byte[] forged = new byte[ActorScalarFrameAbiV1.SlotBytes];
    forged[0] = 2;
    forged[1] = 2;
    forged[8] = 1;
    ctx.WriteStackFrame(typedActivation!, ActorScalarFrameAbiV1.HeaderBytes, forged);
    Deny(() => { _ = ActorScalarFrameAbiV1.ReadBool(ctx, typedActivation!, 0); });
    Deny(() => ActorScalarFrameAbiV1.WriteBool(ctx, typedActivation!, 0, true));
    ctx.WriteStackFrame(typedActivation!, ActorScalarFrameAbiV1.HeaderBytes, original);

    ctx.WriteStackFrame(typedActivation!, 8, new byte[] { 2, 0, 0, 0 });
    Deny(() => { _ = ActorScalarFrameAbiV1.ReadProgramCounter(ctx, typedActivation!); });
    ctx.WriteStackFrame(typedActivation!, 8, new byte[] { 1, 0, 0, 0 });
    Require(ActorScalarFrameAbiV1.ReadI64(ctx, typedActivation!, 0) ==
            long.MinValue,
        "restored typed bytes did not preserve canonical immutable type authority");
    ActorScalarFrameAbiV1.Pop(ctx, typedActivation!);
});
typedRuntime.Send(guardedActor, new byte[] { 1 });
Require(SpinWait.SpinUntil(() => typedRuntime.CompletedMessages == 1,
    TimeSpan.FromSeconds(10)), "native immutable scalar layout first turn failed");
typedRuntime.Send(guardedActor, new byte[] { 2 });
Require(SpinWait.SpinUntil(() => typedRuntime.CompletedMessages == 2,
    TimeSpan.FromSeconds(10)), "native immutable scalar layout replay/check turn failed");
Require(typedRuntime.FaultedTurns == 0,
    "native immutable scalar layout unexpectedly faulted its host actor");
typedRuntime.Stop(guardedActor);
Require(SpinWait.SpinUntil(() =>
    typedRuntime.LiveActors == 0 && typedRuntime.NativeReservedBytes == 0,
    TimeSpan.FromSeconds(10)), "native typed scalar frame was not reclaimed");
Console.WriteLine("PASS actor-scalar-sealed-schema-native-probe: immutable i64/bool layout, forged tags and block counts denied across turns; NOT compiled .atto");
