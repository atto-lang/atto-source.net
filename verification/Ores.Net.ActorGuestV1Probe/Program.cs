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

// Negative mode is compiled from a second, deliberately failing .atto
// fixture. No host-side substitute may silently count as guest execution.
bool expectAssertionFailure = args.Length == 1 &&
    args[0] == "--expect-assert-failure";
require(args.Length == 0 || expectAssertionFailure,
    "usage: ActorGuestV1Probe [--expect-assert-failure]");

using var runtime = new ActorRuntime(options);
using var completed = new CountdownEvent(expectAssertionFailure ? 1 : 6);
int observedExpectedAssertion = 0;

ActorMessageHandler handler = (context, message) =>
{
    try
    {
        require(message.Length == 1, "generated actor probe message shape");
        // This method must be compiled from the checked .atto source. The
        // checked-in project contains no C# implementation of guest Node.
        Ores.Net.Generated.ActorHeapV1Guest.receive(context, message.Span[0]);
    }
    catch (InvalidOperationException error) when
        (expectAssertionFailure && error.Message == "E_ACTOR_HEAP_V1_ASSERT")
    {
        Interlocked.Increment(ref observedExpectedAssertion);
        throw; // Let the runtime's actual actor-fault retirement path execute.
    }
    finally
    {
        completed.Signal();
    }
};

if (expectAssertionFailure)
{
    var doomed = runtime.Spawn(ActorKind.Private, handler);
    runtime.Send(doomed, new byte[] { 1 });
    require(completed.Wait(TimeSpan.FromSeconds(20)),
        "negative generated guest assertion must execute in real actor turn");
    require(SpinWait.SpinUntil(() =>
            runtime.FaultedTurns == 1 && runtime.LiveActors == 0 &&
            runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(20)),
        "generated assertion fault must retire its actor and native heap");
    require(Volatile.Read(ref observedExpectedAssertion) == 1 &&
            runtime.CompletedMessages == 0,
        "only the exact generated assertion failure is accepted");
    Console.WriteLine(
        "PASS attolang-actor-heap-v1-nativeaot-negative: compiled .atto assertion failure retired actor and reclaimed native heap");
}
else
{
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
}
