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

var options = new ActorRuntimeOptions
{
    MaxActors = 3, MessagesPerDispatch = 1,
    MailboxMessages = 4, MailboxBytes = 64,
    ActorPersistentBytes = 128, ActorScratchBytes = 64,
    ActorMaxAllocations = 8, ActorMaxReadLoans = 8,
    ActorMaxLifetimePins = 8, NativeBudgetBytes = 4096,
    Dispatcher = new ActorDispatcherOptions
    {
        PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
        QueueCapacityPerDomain = 3
    }
};
using var runtime = new ActorRuntime(options);
using var initialized = new CountdownEvent(3);
using var verified = new CountdownEvent(3);
var errors = new ConcurrentQueue<Exception>();
var handles = new ActorArenaHandle?[3];
var references = new ActorRef[3];
ActorArenaLifetimePin? pin = null;
ActorContext? retained = null;
var kinds = new[] { ActorKind.Private, ActorKind.Shared, ActorKind.Untrusted };
for (int i = 0; i < 3; ++i)
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
                for (int j = 0; j < 3; ++j)
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
Require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 6,
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
Console.WriteLine("PASS actor-memory-native-probe: three private heaps, foreign handles denied, pooled carriers, stale context denied, pinned STOP and physical reclaim");
