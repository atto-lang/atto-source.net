using Ores.Net.Runtime;

static void Require(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

using var runtime = new ActorRuntime(new ActorRuntimeOptions
{
    MaxActors = 2,
    MessagesPerDispatch = 1,
    MailboxMessages = 8,
    MailboxBytes = 128,
    ActorPersistentBytes = 128,
    ActorScratchBytes = 64,
    ActorMaxAllocations = 4,
    ActorMaxReadLoans = 2,
    ActorMaxLifetimePins = 2,
    ActorFrameBytes = 96,
    ActorMaxFrames = 2,
    NativeBudgetBytes = 512 * 1024,
    Dispatcher = new ActorDispatcherOptions
    {
        PrivateWorkers = 1,
        SharedWorkers = 1,
        UntrustedWorkers = 1,
        QueueCapacityPerDomain = 2
    }
});
using var siblingDone = new ManualResetEventSlim();
long observed = 0;
var failed = runtime.Spawn(ActorKind.Shared, (context, _) =>
{
    var execution = context.BeginFrames();
    _ = context.PushFrame(execution, 80);
    try { _ = context.PushFrame(execution, 24); }
    catch (ActorFrameBudgetExceededException) { }
    // A swallowed quota failure MUST still fault this actor at turn exit.
});
var sibling = runtime.Spawn(ActorKind.Shared, (context, _) =>
{
    var execution = context.BeginFrames();
    var outer = context.PushFrame(execution, 32);
    var inner = context.PushFrame(execution, 8);
    context.WriteFrame(execution, inner, 0, new byte[] { 42 });
    byte[] result = new byte[1];
    context.ReadFrame(execution, inner, 0, result);
    observed = result[0];
    context.PopFrame(execution, inner);
    context.PopFrame(execution, outer);
    context.FinishFrames(execution);
    siblingDone.Set();
});
Require(runtime.StartedCarrierThreads == 0, "spawn started carrier before delivery");
runtime.Send(failed, new byte[] { 1 });
runtime.Send(sibling, new byte[] { 2 });
Require(siblingDone.Wait(TimeSpan.FromSeconds(10)) && observed == 42,
    "same-carrier sibling failed to execute independently");
Require(SpinWait.SpinUntil(
    () => runtime.LiveActors == 1 && runtime.FaultedTurns >= 1,
    TimeSpan.FromSeconds(10)), "faulted actor did not retire independently");
Require(runtime.CompletedMessages == 1, "poisoned actor counted as a completed turn");
runtime.Stop(sibling);
Require(SpinWait.SpinUntil(
    () => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
    TimeSpan.FromSeconds(10)), "actor frames/heap native budget leaked after STOP");
Console.WriteLine("PASS actor-frame-nativeaot-host: per-actor slabs, terminal budget fault, sibling survives on shared carrier, native quota reclaimed; NOT compiled .atto");
