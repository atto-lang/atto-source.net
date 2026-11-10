using Ores.Net.Runtime;

static void require(bool ok, string why)
{
    if (!ok) throw new InvalidOperationException(why);
}

var options = new ActorRuntimeOptions
{
    MaxActors = 4,
    MessagesPerDispatch = 4,
    MailboxMessages = 32,
    MailboxBytes = 4096,
    ActorPersistentBytes = 2048,
    ActorScratchBytes = 256,
    ActorMaxAllocations = 32,
    ActorMaxLifetimePins = 8,
    ActorMaxReadLoans = 8,
    NativeBudgetBytes = 1024 * 1024,
    Dispatcher = new ActorDispatcherOptions
    {
        PrivateWorkers = 1,
        SharedWorkers = 1,
        UntrustedWorkers = 1,
        QueueCapacityPerDomain = 8
    }
};

using var runtime = new ActorRuntime(options);
using var completed = new CountdownEvent(6);
ActorMessageHandler handler = (context, message) =>
{
    try
    {
        require(message.Length == 1, "generated actor probe message shape");
        // The generated method is compiled from .atto into this executable
        // before dotnet publish: it calls the real typed native heap API.
        Ores.Net.Generated.ActorHeapV1Guest.receive(context, message.Span[0]);
    }
    finally { completed.Signal(); }
};

var privateActor = runtime.Spawn(ActorKind.Private, handler);
var sharedActor = runtime.Spawn(ActorKind.Shared, handler);
require(runtime.LiveActors == 2 &&
        runtime.NativeReservedBytes == 2L * (2048 + 256),
    "two separate actor-owned slabs are admitted before execution");

for (int i = 0; i < 3; i++)
{
    runtime.Send(privateActor, new byte[] { checked((byte)(i + 1)) });
    runtime.Send(sharedActor, new byte[] { checked((byte)(i + 1)) });
}
require(completed.Wait(TimeSpan.FromSeconds(20)),
    "generated AttoLang native graph must complete six actual actor turns");
require(SpinWait.SpinUntil(() => runtime.CompletedMessages == 6,
        TimeSpan.FromSeconds(10)) && runtime.FaultedTurns == 0,
    "compiled guest object graph may not fault or silently skip a turn");

runtime.Stop(privateActor);
runtime.Stop(sharedActor);
require(SpinWait.SpinUntil(() =>
        runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
        TimeSpan.FromSeconds(10)),
    "generated guest objects must reclaim both actor native slabs on stop");
Console.WriteLine(
    "PASS attolang-actor-heap-v1-nativeaot: generated .atto Node graph executed in two independent actor heaps (narrow guest subset)");
