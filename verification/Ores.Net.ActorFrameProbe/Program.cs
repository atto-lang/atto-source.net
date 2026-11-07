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
// Native AOT trimming must not turn the async-void admission guard into an
// accidental no-op. Check it in the published native host, not only CoreCLR.
ActorMessageHandler asyncVoid = async (_, _) => await Task.Yield();
ActorMessageHandler synchronous = static (_, _) => { };
static void RequireAsyncDenied(ActorRuntime runtime, ActorMessageHandler candidate)
{
    try { runtime.Spawn(ActorKind.Shared, candidate); }
    catch (ArgumentException ex) when
        (ex.ParamName == "handler" &&
         ex.Message.Contains("scheduler-owned", StringComparison.Ordinal)) { return; }
    throw new InvalidOperationException("Native AOT async-void actor handler escaped admission");
}
RequireAsyncDenied(runtime, asyncVoid);
RequireAsyncDenied(runtime, synchronous + asyncVoid);
RequireAsyncDenied(runtime, asyncVoid + synchronous);
Require(runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0 &&
    runtime.StartedCarrierThreads == 0,
    "rejected native async-void handlers allocated actor resources");

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
Console.WriteLine("PASS actor-frame-nativeaot-async-guard: direct and multicast async-void admission denied before native allocation");
