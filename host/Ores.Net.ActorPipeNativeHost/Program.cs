using System.Buffers.Binary;
using System.Globalization;
using Ores.Net.Runtime;
using Oreslang.Generated;

int count = checked((int)OresProgram.ActorPipeStageCount());
if (count is < 2 or > 16)
    throw new InvalidOperationException("Invalid compiled actor pipe stage count");

using var runtime = new ActorRuntime(new ActorRuntimeOptions
{
    MaxActors = count,
    MessagesPerDispatch = 1,
    MailboxMessages = 1,
    MailboxBytes = sizeof(long),
    ActorPersistentBytes = 4096,
    ActorScratchBytes = 512,
    NativeBudgetBytes = 1024 * 1024,
    Dispatcher = new ActorDispatcherOptions
    {
        PrivateWorkers = 2,
        SharedWorkers = 1,
        UntrustedWorkers = 1,
        QueueCapacityPerDomain = 32
    }
});

var actors = new ActorRef[count];
long result = 0;
// Bind every stage before admitting the one input. Each handler captures
// only its stage number and the already-bound downstream capability.
for (int i = count - 1; i >= 0; i--)
{
    int stage = i;
    bool isSink = stage == count - 1;
    ActorRef downstream = isSink ? default : actors[stage + 1];
    actors[stage] = runtime.Spawn(ActorKind.Private, (_, message) =>
    {
        if (message.Length != sizeof(long))
            throw new InvalidOperationException("Invalid compiled actor pipe message");
        long value = OresProgram.ActorPipeStage(stage,
            BinaryPrimitives.ReadInt64LittleEndian(message.Span));
        if (isSink)
            Interlocked.Exchange(ref result, value);
        else
            Send(downstream, value);
    });
}

Send(actors[0], OresProgram.ActorPipeInput());
try
{
    foreach (var actor in actors)
        await runtime.DrainAndStop(actor);
}
catch (InvalidOperationException) when (runtime.FaultedTurns != 0)
{
    // A fault can settle before the drain call finds the actor, or surface
    // from its retirement task. Both paths fail the guest with no result.
    Console.Error.WriteLine("E_ACTOR_PIPE_TURN_FAULT: a pipeline stage failed");
    return 70;
}
if (runtime.CompletedMessages != count || runtime.FaultedTurns != 0 ||
    runtime.LiveActors != 0 || runtime.NativeReservedBytes != 0)
    throw new InvalidOperationException("Actor pipe delivery or retirement invariant failed");

Console.WriteLine(Interlocked.Read(ref result).ToString(CultureInfo.InvariantCulture));
Console.Error.WriteLine($"ACTOR_PIPE_RECEIPT; turns={runtime.CompletedMessages}; live={runtime.LiveActors}; native_bytes={runtime.NativeReservedBytes}");
return 0;

void Send(ActorRef to, long value)
{
    Span<byte> message = stackalloc byte[sizeof(long)];
    BinaryPrimitives.WriteInt64LittleEndian(message, value);
    runtime.Send(to, message);
}
