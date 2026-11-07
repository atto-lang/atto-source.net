using Ores.Net.Runtime;
using Oreslang.Generated;

// This program is statically linked against OresGenerated.dll, emitted by
// Ores.Net.ActorFrameFixtureGen from a checked .atto scalar fixture *before*
// NativeAOT publish. This is not Assembly.Load, Reflection.Emit or a host
// implementation imitating the compiler output.
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static ActorRuntimeOptions Options(int frameBytes) => new()
{
    MaxActors = 2,
    MessagesPerDispatch = 2,
    MailboxMessages = 8,
    MailboxBytes = 128,
    ActorPersistentBytes = 512,
    ActorScratchBytes = 128,
    ActorMaxAllocations = 8,
    ActorMaxLifetimePins = 4,
    ActorMaxReadLoans = 4,
    ActorFrameBytes = frameBytes,
    ActorMaxFrames = 4,
    NativeBudgetBytes = 16 * 1024,
    Dispatcher = new ActorDispatcherOptions
    {
        SharedWorkers = 1,
        PrivateWorkers = 1,
        UntrustedWorkers = 1,
        QueueCapacityPerDomain = 2
    }
};

// Phase one: real compiler-emitted nested CIL uses a 16-byte entry frame
// followed by a 24-byte helper frame, so 32-byte actor limit must terminally
// reject the nested compiled call. Sibling B uses the very same shared carrier
// and remains responsive: the bounded *logical* stack fault is not a CLR SOE.
using (var runtime = new ActorRuntime(Options(frameBytes: 32)))
using (var siblingDone = new ManualResetEventSlim())
{
    long siblingResult = 0;
    int compiledQuotaObserved = 0;
    int unexpectedCompiledReturn = 0;
    var faulted = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
    {
        try
        {
            _ = OresProgram.Run(ctx);
            Interlocked.Exchange(ref unexpectedCompiledReturn, 1);
        }
        catch (ActorFrameBudgetExceededException)
        {
            Interlocked.Exchange(ref compiledQuotaObserved, 1);
            throw; // must remain terminal to this actor, not recovered
        }
    });
    var sibling = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
    {
        var execution = ctx.BeginFrames();
        var frame = ctx.PushFrame(execution, 8);
        ctx.WriteFrameI64(execution, frame, 0, 42);
        siblingResult = ctx.ReadFrameI64(execution, frame, 0);
        ctx.PopFrame(execution, frame);
        ctx.FinishFrames(execution);
        siblingDone.Set();
    });
    Require(runtime.StartedCarrierThreads == 0,
        "actor spawn unexpectedly created a carrier");
    runtime.Send(faulted, new byte[] { 1 });
    runtime.Send(sibling, new byte[] { 2 });
    Require(siblingDone.Wait(TimeSpan.FromSeconds(10)) && siblingResult == 42,
        "actor B failed to survive compiled CIL frame exhaustion on shared carrier");
    Require(SpinWait.SpinUntil(
        () => runtime.LiveActors == 1 && runtime.FaultedTurns == 1,
        TimeSpan.FromSeconds(10)),
        "compiled call did not fault and retire actor A with bounded frame memory");
    Require(Volatile.Read(ref compiledQuotaObserved) == 1 &&
            Volatile.Read(ref unexpectedCompiledReturn) == 0,
        "actor A fault must specifically be compiled helper frame-budget overflow");
    Require(runtime.CompletedMessages == 1,
        "failed compiled actor was incorrectly marked a completed message");
    runtime.Stop(sibling);
    Require(SpinWait.SpinUntil(
        () => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
        TimeSpan.FromSeconds(10)),
        "per-actor compiled-frame quota not refunded after actor fault/STOP");
}

// Phase two: the exact same static generated PE must actually execute its
// checked helper call and return 42 when given a sufficient actor frame slab.
using (var runtime = new ActorRuntime(Options(frameBytes: 128)))
using (var done = new ManualResetEventSlim())
{
    long observed = 0;
    var actor = runtime.Spawn(ActorKind.Shared, (context, _) =>
    {
        observed = OresProgram.Run(context);
        done.Set();
    });
    runtime.Send(actor, new byte[] { 1 });
    Require(done.Wait(TimeSpan.FromSeconds(10)) && observed == 42,
        "Native AOT compiled guest scalar call did not return expected 42");
    Require(runtime.FaultedTurns == 0 && runtime.CompletedMessages == 1,
        "successful compiled guest call must leave one healthy completed turn");
    runtime.Stop(actor);
    Require(SpinWait.SpinUntil(
        () => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
        TimeSpan.FromSeconds(10)),
        "compiled guest frame memory leaked after clean actor STOP");
}

Console.WriteLine("PASS actor-compiled-cfg-nativeaot: emitted CIL Run(ActorContext) executed; bounded actor A overflow, shared-carrier B survives, actor C returns 42; NOT define actor");
