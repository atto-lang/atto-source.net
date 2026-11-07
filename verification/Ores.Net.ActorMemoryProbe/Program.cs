using System.Collections.Concurrent;
using Ores.Net.Runtime;
using Ores.Net.Compiler.ActorFrames;

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
    ActorStackBytes = 512, ActorMaxStackFrames = 4,
    ActorMaxAllocations = 8, ActorMaxReadLoans = 8,
    ActorMaxLifetimePins = 8, NativeBudgetBytes = 4096,
    Dispatcher = new ActorDispatcherOptions
    {
        PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
        QueueCapacityPerDomain = actorCount
    }
};
// Real typed .atto source is parsed and verified, then executed by the
// explicit actor-frame trampoline; it is not compiled to actor CIL yet.
var sourceFile = Path.Combine(AppContext.BaseDirectory,
    "actor-frame-v1", "source-scalar-bridge.atto");
var scalarGuest = ActorScalarFrameProgram.Compile(File.ReadAllText(sourceFile));
using var runtime = new ActorRuntime(options);
using var initialized = new CountdownEvent(actorCount);
using var verified = new CountdownEvent(actorCount);
using var survivorVerified = new AutoResetEvent(false);
var errors = new ConcurrentQueue<Exception>();
var handles = new ActorArenaHandle?[actorCount];
var expiredFrames = new ActorFrame?[actorCount];
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
            // Actor-owned frame bytes are independently allocated and
            // capped even though all handlers execute on pooled CLR carriers.
            using (var frame = context.PushFrame(16))
            {
                context.WriteFrame(frame, 0, new byte[] { (byte)(71 + index) });
                var frameData = new byte[1];
                context.ReadFrame(frame, 0, frameData);
                Require(frameData[0] == 71 + index,
                    "actor frame roundtrip in native executable");
                if (first)
                    expiredFrames[index] = frame;
            }
            if (!first)
            {
                // These stored frame capabilities are retired even when
                // the next turn uses the same carrier and incarnation.
                for (int other = 0; other < actorCount; other++)
                {
                    int candidate = other;
                    Deny(() => context.ReadFrame(expiredFrames[candidate]!, 0,
                        new byte[1]));
                }
            }
            if (first)
            {
                Require(scalarGuest.Execute(context) == 42,
                    "source-verified native frame interpreter executed inside actor");
                handles[index] = context.AllocatePersistent(8);
                context.Write(handles[index]!, 0,
                    new byte[] { (byte)(11 + index), 0, 0, 0, 0, 0, 0, 0 });
            }
            else if (phase >= 2)
            {
                Require(index == 2 && (phase == 2 || phase == 3),
                    "unexpected survivor probe phase or actor");
                var output = new byte[8];
                context.Read(handles[index]!, 0, output);
                Require(output[0] == (phase == 2 ? 13 : 14),
                    "survivor state changed during peer retirement");
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
                for (int j = 0; j < actorCount; ++j)
                {
                    if (j == index) continue;
                    int foreign = j;
                    Deny(() => context.Read(handles[foreign]!, 0, output));
                    Deny(() => context.Write(handles[foreign]!, 0, output));
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
Require(runtime.NativeReservedBytes == allReserved - options.ActorStackBytes,
    "logical STOP scrubs its actor frame stack while the pinned heap remains");
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
