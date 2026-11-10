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
var compressed = new uint[actorCount];
var typedRoots = new uint[actorCount];
var typedNode = new ActorGuestLayout32(0xA701, 16,
    new[] { new ActorGuestField32(0, ActorGuestFieldKind32.I64),
            new ActorGuestField32(8, ActorGuestFieldKind32.Ref32, 0xA701) });
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
                compressed[index] = context.AllocateGuestObject32(8);
                context.WriteGuestObject32(compressed[index], 0,
                    new byte[] { (byte)(41 + index) });
                if (index == 0)
                {
                    // Measured inside a real NativeAOT actor turn, after the
                    // one-time lazy metadata/locking paths have been warmed.
                    Span<byte> value = stackalloc byte[8];
                    for (int warm = 0; warm < 128; warm++)
                    {
                        uint temporary = context.AllocateGuestObject32(8);
                        value[0] = (byte)warm;
                        context.WriteGuestObject32(temporary, 0, value);
                        context.ReadGuestObject32(temporary, 0, value);
                        context.DropGuestObject32(temporary);
                    }
                    bool correct = true;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int cycle = 0; cycle < 4096; cycle++)
                    {
                        uint temporary = context.AllocateGuestObject32(8);
                        value[0] = (byte)cycle;
                        context.WriteGuestObject32(temporary, 0, value);
                        value[0] = 0;
                        context.ReadGuestObject32(temporary, 0, value);
                        if (value[0] != (byte)cycle) correct = false;
                        context.DropGuestObject32(temporary);
                        if (!correct) break;
                    }
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Require(correct && allocated == 0,
                        "NativeAOT actor guest payload steady-state must allocate zero CLR bytes");
                }
                typedRoots[index] = context.AllocateTypedGuestObject32(typedNode);
                uint typedChild = context.AllocateTypedGuestObject32(typedNode);
                context.WriteGuestObject32(typedChild, 0,
                    new byte[8] { (byte)(51 + index), 0, 0, 0, 0, 0, 0, 0 });
                Deny(() => context.WriteGuestObject32(typedChild, 0,
                    new byte[] { 0xFF }));
                Deny(() => context.ReadGuestObject32(typedChild, 0,
                    new byte[1]));
                context.WriteGuestReference32(typedRoots[index], 8, typedChild);
                Deny(() => context.WriteGuestObject32(typedRoots[index], 8,
                    new byte[] { 0, 0, 0, 0 }));
                frames[index] = context.PushStackFrame(8);
                context.WriteStackFrame(frames[index]!, 0,
                    new byte[] { (byte)(31 + index) });
            }
            else if (phase >= 2)
            {
                Require(index == 2 && (phase == 2 || phase == 3),
                    "unexpected survivor probe phase or actor");
                var guestValue = new byte[1];
                context.ReadGuestObject32(compressed[index], 0, guestValue);
                Require(guestValue[0] == 41 + index,
                    "survivor compressed ref lost private guest payload");
                uint child = context.ReadGuestReference32(typedRoots[index], 8);
                var typedValue = new byte[8];
                context.ReadGuestObject32(child, 0, typedValue);
                Require(typedValue[0] == 51 + index,
                    "survivor typed guest child reference lost private payload");
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
                var guestValue = new byte[1];
                context.ReadGuestObject32(compressed[index], 0, guestValue);
                Require(guestValue[0] == 41 + index,
                    "compressed actor-local ref must survive turn rollover");
                uint typedChild = context.ReadGuestReference32(typedRoots[index], 8);
                var typedValue = new byte[8];
                context.ReadGuestObject32(typedChild, 0, typedValue);
                Require(typedValue[0] == 51 + index,
                    "typed guest reference graph survives physical actor turn");
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
Deny(() => retained!.AllocateGuestObject32(8));
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
Console.WriteLine("PASS actor-ref32-native-probe: four actor-local uint32 guest refs, distinct native payloads, retained-turn denial (host only; not compiled guest)");
Console.WriteLine("PASS actor-ref32-native-no-clr-allocation: 4096 warmed guest payload cycles allocated zero managed bytes (host only)");
Console.WriteLine("PASS actor-ref32-native-typed-nodegraph: four typed native graphs survive actor turns with guarded reference fields (host only; not compiled guest)");
