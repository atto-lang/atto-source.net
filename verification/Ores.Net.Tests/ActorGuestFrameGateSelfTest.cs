using Ores.Net.Runtime;
using System.Threading;

internal static class ActorGuestFrameGateSelfTest
{
    public static void Run()
    {
        GuestFrameFaultsAreTerminalButHostValidationRemainsRecoverable();
        Console.WriteLine(
            "PASS .NET terminal compiled guest-frame boundary host tests (NOT guest .atto or physical stack proof)");
    }

    private static ActorRuntimeOptions Options() => new()
    {
        MaxActors = 8,
        MessagesPerDispatch = 4,
        ActorPersistentBytes = 4096,
        ActorScratchBytes = 256,
        ActorStackBytes = 512,
        ActorMaxStackFrames = 3,
        ActorMaxAllocations = 16,
        ActorMaxLifetimePins = 2,
        ActorMaxReadLoans = 2,
        NativeBudgetBytes = 128 * 1024,
        Dispatcher = new ActorDispatcherOptions
        {
            PrivateWorkers = 1,
            SharedWorkers = 1,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 8
        }
    };

    private static void GuestFrameFaultsAreTerminalButHostValidationRemainsRecoverable()
    {
        using var runtime = new ActorRuntime(Options());
        using var healthyDone = new ManualResetEventSlim();
        int healthyResult = 0;
        ActorContext? retainedContext = null;

        var badBounds = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var frame = context.GuestPushStackFrame(8);
            try
            {
                context.GuestWriteStackFrame(frame, 7, new byte[] { 1, 2 });
                throw new Exception("Guest frame overflow was accepted");
            }
            catch (ArgumentOutOfRangeException) { }

            // Even when caught, the frame fault is permanent for this turn.
            ExpectInvalid(() => context.GuestPopStackFrame(frame),
                "guest frame operations must stay poisoned after bounds fault");
        });

        var leaked = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            _ = context.GuestPushStackFrame(8);
            // Synchronous guest handler cannot return with its frame live.
        });

        var foreign = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var hostFrame = context.PushStackFrame(8);
            ExpectInvalid(() => context.GuestReadStackFrame(
                hostFrame, 0, new byte[1]),
                "guest access cannot borrow a host-owned frame");
            context.PopStackFrame(hostFrame);
            // The foreign handle attempt still makes the guest turn terminal.
        });

        var wrongLifo = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            var outer = context.GuestPushStackFrame(8);
            _ = context.GuestPushStackFrame(8);
            ExpectInvalid(() => context.GuestPopStackFrame(outer),
                "guest frame pop must be strict LIFO");
        });

        var healthy = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            retainedContext = context;
            var hostFrame = context.PushStackFrame(8);
            try
            {
                context.WriteStackFrame(hostFrame, 7, new byte[] { 1, 2 });
                throw new Exception("Host bounds check was bypassed");
            }
            catch (ArgumentOutOfRangeException) { }

            // Mainline host frame validation stays explicitly recoverable.
            var guest = context.GuestPushStackFrame(8);
            context.GuestWriteStackFrame(guest, 0, new byte[] { 42 });
            byte[] output = new byte[1];
            context.GuestReadStackFrame(guest, 0, output);
            Volatile.Write(ref healthyResult, output[0]);

            // An off-carrier stolen context must be rejected BEFORE it can
            // poison its legitimate owner, which is still in its live turn.
            bool crossCarrierDenied = false;
            var outsider = new Thread(() =>
            {
                try { context.GuestReadStackFrame(guest, 0, new byte[1]); }
                catch (InvalidOperationException) { crossCarrierDenied = true; }
            }) { IsBackground = true };
            outsider.Start();
            Require(outsider.Join(TimeSpan.FromSeconds(5)) && crossCarrierDenied,
                "off-carrier stolen guest context must fail without poisoning owner");

            context.GuestPopStackFrame(guest);
            context.PopStackFrame(hostFrame);
            healthyDone.Set();
        });

        runtime.Send(badBounds, new byte[] { 1 });
        runtime.Send(leaked, new byte[] { 2 });
        runtime.Send(foreign, new byte[] { 3 });
        runtime.Send(wrongLifo, new byte[] { 4 });
        runtime.Send(healthy, new byte[] { 5 });
        Require(healthyDone.Wait(TimeSpan.FromSeconds(8)) &&
                Volatile.Read(ref healthyResult) == 42,
            "actor B could not return 42 after four guest-frame faults");
        Require(SpinWait.SpinUntil(
            () => runtime.FaultedTurns >= 4 && runtime.LiveActors == 1,
            TimeSpan.FromSeconds(8)),
            "four terminal guest frame faults must stop only their own actors");
        Require(runtime.CompletedMessages == 1,
            "caught guest frame faults must not count as completed messages");

        // A retained old physical turn is never a new compiler credential.
        ExpectInvalid(() => retainedContext!.GuestPushStackFrame(8),
            "context used after its host physical turn finished");
        runtime.Stop(healthy);
        Require(SpinWait.SpinUntil(
            () => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(8)),
            "all actor heap/frame credits must be refunded on STOP");
    }

    private static void ExpectInvalid(Action action, string description)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected guest-frame rejection: " + description);
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new Exception("Guest-frame fail-closed regression: " + description);
    }
}
