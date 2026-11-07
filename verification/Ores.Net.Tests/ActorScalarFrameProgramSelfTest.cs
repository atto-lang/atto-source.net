using Ores.Net.Compiler.ActorFrames;
using Ores.Net.Runtime;

/// <summary>
/// The real structured source parser + verified CFG drives the actor-owned
/// native frame trampoline here, inside actual actor mailbox dispatch.
/// This is not define actor parsing, generated CIL, or physical CLR stacks.
/// </summary>
internal static class ActorScalarFrameProgramSelfTest
{
    public static void Run()
    {
        SourceBackedNestedFunctionsRunInActorFrames();
        CheckedOverflowFaultsOnlyItsActor();
        StackDepthQuotaFaultsOnlyItsActor();
        UnsupportedActorSourceIsRejectedBeforeExecution();
        Console.WriteLine(
            "PASS source-verified .atto scalar calls execute in actor native frames (experimental host trampoline)");
    }

    private static ActorRuntimeOptions Options(int stackBytes = 1024) => new()
    {
        MaxActors = 8,
        MessagesPerDispatch = 4,
        MailboxMessages = 16,
        MailboxBytes = 4096,
        ActorPersistentBytes = 512,
        ActorScratchBytes = 256,
        ActorStackBytes = stackBytes,
        ActorMaxStackFrames = 8,
        ActorMaxAllocations = 8,
        ActorMaxLifetimePins = 8,
        ActorMaxReadLoans = 8,
        NativeBudgetBytes = 32768,
        Dispatcher = new ActorDispatcherOptions
        {
            PrivateWorkers = 1,
            SharedWorkers = 1,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 8
        }
    };

    private static bool Wait(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));

    private static void Require(bool value, string reason)
    {
        if (!value)
            throw new InvalidOperationException("Actor scalar frame bridge: " + reason);
    }

    private static void SourceBackedNestedFunctionsRunInActorFrames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "actor-frame-v1",
            "source-scalar-bridge.atto");
        var program = ActorScalarFrameProgram.Compile(File.ReadAllText(path));
        using var runtime = new ActorRuntime(Options());
        using var done = new ManualResetEventSlim();
        long answer = 0;
        var actor = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
        {
            answer = program.Execute(ctx);
            done.Set();
        });
        runtime.Send(actor, new byte[] { 7 });
        Require(done.Wait(TimeSpan.FromSeconds(5)), "nested source calls completed");
        Require(answer == 42, "verified source call results are preserved");
        Require(Wait(() => runtime.CompletedMessages == 1 && runtime.FaultedTurns == 0),
            "source-compiled actor frame execution completes as a valid turn");
        runtime.Stop(actor);
        Require(Wait(() => runtime.LiveActors == 0 &&
            runtime.NativeReservedBytes == 0),
            "compiled scalar call frames refund native actor memory");
    }

    private static void CheckedOverflowFaultsOnlyItsActor()
    {
        var overflow = ActorScalarFrameProgram.Compile(
            "fnc main():int { return 9223372036854775807 + 1; }");
        var healthy = ActorScalarFrameProgram.Compile(
            "fnc main():int { return 40 + 2; }");
        using var runtime = new ActorRuntime(Options());
        using var done = new ManualResetEventSlim();
        long answer = -1;
        var bad = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
        {
            overflow.Execute(ctx);
        });
        var good = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
        {
            answer = healthy.Execute(ctx);
            done.Set();
        });
        runtime.Send(bad, new byte[] { 1 });
        runtime.Send(good, new byte[] { 2 });
        Require(done.Wait(TimeSpan.FromSeconds(5)) && answer == 42,
            "sibling actor survives checked scalar source trap");
        Require(Wait(() => runtime.FaultedTurns == 1 &&
            runtime.CompletedMessages == 1 && runtime.LiveActors == 1),
            "only overflowing actor is fault-retired");
        runtime.Stop(good);
        Require(Wait(() => runtime.NativeReservedBytes == 0),
            "checked overflow unwinds all native frames and refunds quotas");
    }

    private static void StackDepthQuotaFaultsOnlyItsActor()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "actor-frame-v1",
            "source-scalar-bridge.atto");
        var nested = ActorScalarFrameProgram.Compile(File.ReadAllText(path));
        var simple = ActorScalarFrameProgram.Compile(
            "fnc main():int { return 42; }");
        using var runtime = new ActorRuntime(Options(stackBytes: 192));
        using var done = new ManualResetEventSlim();
        long answer = -1;
        bool caught = false;
        var bad = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
        {
            try { _ = nested.Execute(ctx); }
            catch (InvalidOperationException) { caught = true; }
        });
        var good = runtime.Spawn(ActorKind.Shared, (ctx, _) =>
        {
            answer = simple.Execute(ctx);
            done.Set();
        });
        runtime.Send(bad, new byte[] { 1 });
        runtime.Send(good, new byte[] { 2 });
        Require(done.Wait(TimeSpan.FromSeconds(5)) && answer == 42,
            "sibling actor executes while nested source calls exceed frame quota");
        Require(Wait(() => runtime.FaultedTurns == 1 &&
            runtime.CompletedMessages == 1 && runtime.LiveActors == 1),
            "caught native frame exhaustion still retires only the poisoned actor");
        Require(caught, "nested source graph reached actor frame quota");
        runtime.Stop(good);
        Require(Wait(() => runtime.NativeReservedBytes == 0),
            "source-backed bounded native call stacks reclaim independently");
    }

    private static void UnsupportedActorSourceIsRejectedBeforeExecution()
    {
        foreach (var source in new[]
        {
            "define actor Worker as receive(int value):int { return value; } end",
            "fnc main():int { return missing(42); }",
            "fnc recursive(int x):int { return recursive(x); } fnc main():int { return recursive(1); }",
            "fnc main():int { return 1 / 0; }"
        })
        {
            bool rejected = false;
            try { _ = ActorScalarFrameProgram.Compile(source); }
            catch (FormatException) { rejected = true; }
            Require(rejected,
                "unsupported actor/call/recursive/effect source must fail before execution");
        }
    }
}
