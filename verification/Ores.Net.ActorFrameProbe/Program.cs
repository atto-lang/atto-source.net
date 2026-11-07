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

// Exercise the canonical typed native slab ABI inside a published NativeAOT
// binary, not just a CoreCLR self-test. Caller mutation cannot rewrite slots.
var types = new[] { ActorFrameScalarKind.I64, ActorFrameScalarKind.Bool };
var typedLayout = new ActorFrameTypedLayout(types);
types[0] = ActorFrameScalarKind.Bool;
Require(typedLayout.GetKind(0) == ActorFrameScalarKind.I64 &&
        typedLayout.GetKind(1) == ActorFrameScalarKind.Bool,
    "NativeAOT typed frame layout is not an immutable snapshot");
static void DenyFrame(Action action, string reason)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("NativeAOT typed actor frame admission failure: " + reason);
}

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
    var typed = context.PushTypedFrame(execution, typedLayout);
    context.WriteFrameI64(execution, typed, 0, long.MinValue);
    context.WriteFrameBool(execution, typed, 1, true);
    Require(context.ReadFrameI64(execution, typed, 0) == long.MinValue &&
            context.ReadFrameBool(execution, typed, 1),
        "NativeAOT typed scalar spill round-trip failed");
    context.WriteFrameBool(execution, typed, 1, false);
    Require(!context.ReadFrameBool(execution, typed, 1),
        "NativeAOT typed bool false round-trip failed");
    context.WriteFrameBool(execution, typed, 1, true);
    DenyFrame(() => context.ReadFrame(execution, typed, 0, new byte[8]),
        "typed frame bytes cannot be read through raw API");
    DenyFrame(() => context.WriteFrame(execution, typed, 8, new byte[] { 2 }),
        "raw byte API cannot forge a bool typed slot");
    DenyFrame(() => context.WriteFrameI64(execution, typed, 1, 7),
        "bool slot cannot receive i64 writes");
    DenyFrame(() => _ = context.ReadFrameBool(execution, typed, 0),
        "i64 slot cannot impersonate bool");
    context.PopFrame(execution, typed);
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
Console.WriteLine("PASS actor-frame-nativeaot-typed-spills: i64 and bool native slab values; raw access and wrong-type denied");

// Trusted scheduler seam only: a continuation may resume in a later
// authenticated physical turn, never within the suspending turn. This does
// not claim that the compiler emits guest suspension for .atto actors.
using (var owner = new ActorArena(7940, 1, 256, 128, 4, 2, 2))
using (var stack = new ActorFrameStack(owner, new ActorArenaBudget(128), 4, 128))
{
    ActorFrameExecution execution;
    ActorFrameHandle frame;
    ActorFrameContinuation continuation;
    using (var first = owner.EnterTurn(7940, 1))
    {
        execution = stack.Begin(first);
        frame = stack.PushTyped(first, execution, typedLayout);
        stack.WriteI64(first, execution, frame, 0, long.MaxValue);
        stack.WriteBool(first, execution, frame, 1, true);
        continuation = stack.Suspend(first, execution);
        stack.ValidateTurnExit(first);
    }
    using (var second = owner.EnterTurn(7940, 1))
    {
        stack.Resume(second, execution, continuation);
        Require(stack.ReadI64(second, execution, frame, 0) == long.MaxValue &&
                stack.ReadBool(second, execution, frame, 1),
            "NativeAOT later-turn continuation lost typed frame contents");
        stack.Pop(second, execution, frame);
        stack.Finish(second, execution);
        stack.ValidateTurnExit(second);
    }
    Require(stack.Depth == 0 && stack.ChargedBytes == 0,
        "NativeAOT native continuation did not unwind its logical frame");
}
using (var owner = new ActorArena(7950, 1, 256, 128, 4, 2, 2))
using (var stack = new ActorFrameStack(owner, new ActorArenaBudget(128), 4, 128))
using (var sameTurn = owner.EnterTurn(7950, 1))
{
    var execution = stack.Begin(sameTurn);
    _ = stack.PushTyped(sameTurn, execution, typedLayout);
    var continuation = stack.Suspend(sameTurn, execution);
    DenyFrame(() => stack.Resume(sameTurn, execution, continuation),
        "same physical turn must never re-claim its own continuation");
    Require(stack.IsPoisoned,
        "invalid same-turn resume is a terminal actor-local protocol fault");
}
Console.WriteLine("PASS actor-frame-nativeaot-resume-epoch: cross-turn ticket accepted once; same-turn denial poisons only logical actor stack");
