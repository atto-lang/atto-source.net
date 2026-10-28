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
var errors = new ConcurrentQueue<Exception>();
var handles = new ActorArenaHandle?[actorCount];
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
        bool first = message.Span[0] == 0;
        try
        {
            if (first)
            {
                handles[index] = context.AllocatePersistent(8);
                context.Write(handles[index]!, 0,
                    new byte[] { (byte)(11 + index), 0, 0, 0, 0, 0, 0, 0 });
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
                if (index == 0)
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
            else verified.Signal();
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
foreach (var actor in references) runtime.Stop(actor);
Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0,
    TimeSpan.FromSeconds(10)), "logical retirement did not complete");
Require(runtime.NativeReservedBytes > 0, "lifetime pin lost physical memory");
Task.Run(() => pin!.Dispose()).GetAwaiter().GetResult();
pin!.Dispose();
Require(SpinWait.SpinUntil(() => runtime.NativeReservedBytes == 0,
    TimeSpan.FromSeconds(10)), "physical actor memory was not refunded");
Console.WriteLine("PASS actor-memory-native-probe: four private heaps (two shared-pool actors), foreign handles denied, pooled carriers, stale context denied, pinned STOP and physical reclaim");
