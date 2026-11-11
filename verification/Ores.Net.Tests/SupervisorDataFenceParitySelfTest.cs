using Ores.Net.Runtime;

// Executed host-runtime contract gate. NOT an AttoLang source compiler test;
// successful CLR execution is NOT independent NativeAOT execution.
internal static class SupervisorDataFenceParitySelfTest
{
    private static ActorRuntimeOptions Options() => new()
    {
        MaxActors = 4,
        MessagesPerDispatch = 1,
        MailboxMessages = 32,
        MailboxBytes = 2048,
        ActorPersistentBytes = 4096,
        ActorScratchBytes = 512,
        ActorMaxAllocations = 64,
        ActorMaxLifetimePins = 16,
        ActorMaxReadLoans = 16,
        NativeBudgetBytes = 1024 * 1024,
        Dispatcher = new ActorDispatcherOptions
        {
            PrivateWorkers = 2, SharedWorkers = 2, UntrustedWorkers = 1,
            QueueCapacityPerDomain = 32
        }
    };

    public static void Run()
    {
        const int rounds = 24, senders = 4, attemptsPerSender = 64;
        int attemptedTotal = 0, admittedTotal = 0;
        for (int trial = 0; trial < rounds; trial++)
        {
            using var runtime = new ActorRuntime(Options());
            int siblingProcessed = 0;
            var target = runtime.Spawn(ActorKind.Private, static (_, _) => { });
            var sibling = runtime.Spawn(ActorKind.Private,
                (_, _) => Interlocked.Increment(ref siblingProcessed));
            using var start = new ManualResetEventSlim(false);
            int attempted = 0, admitted = 0, denied = 0, stopReturned = 0;
            Task[] tasks = new Task[senders + 1];
            for (int i = 0; i < senders; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    if (!start.Wait(TimeSpan.FromSeconds(5)))
                        throw new Exception("DATA race start gate timed out");
                    for (int message = 0; message < attemptsPerSender; message++)
                    {
                        bool afterStop = Volatile.Read(ref stopReturned) != 0;
                        Interlocked.Increment(ref attempted);
                        try
                        {
                            runtime.Send(target, new byte[] { 1 });
                            if (afterStop)
                                throw new Exception("DATA accepted after STOP completed");
                            Interlocked.Increment(ref admitted);
                        }
                        catch (InvalidOperationException)
                        {
                            Interlocked.Increment(ref denied);
                        }
                    }
                });
            }
            tasks[senders] = Task.Run(() =>
            {
                if (!start.Wait(TimeSpan.FromSeconds(5)))
                    throw new Exception("CONTROL race start gate timed out");
                runtime.Stop(target);
                Volatile.Write(ref stopReturned, 1);
            });
            start.Set();
            if (!Task.WaitAll(tasks, TimeSpan.FromSeconds(15)))
                throw new Exception("DATA/STOP race did not settle");
            Require(attempted == senders * attemptsPerSender &&
                    admitted + denied == attempted,
                "each raced Send must commit or fail admission exactly once");
            Require(SpinWait.SpinUntil(
                () => runtime.LiveActors == 1, TimeSpan.FromSeconds(5)),
                "stopped incarnation did not retire before sibling");
            bool staleDenied = false;
            try { runtime.Send(target, new byte[] { 9 }); }
            catch (InvalidOperationException) { staleDenied = true; }
            Require(staleDenied, "retired actor admitted a late DATA message");

            runtime.Send(sibling, new byte[] { 7 });
            Task done = runtime.DrainAndStop(sibling);
            Require(done.Wait(TimeSpan.FromSeconds(5)) &&
                    SpinWait.SpinUntil(
                        () => runtime.LiveActors == 0, TimeSpan.FromSeconds(5)),
                "unrelated sibling did not drain and retire");
            Require(siblingProcessed == 1,
                "unrelated sibling did not process its admitted DATA");
            Require(runtime.NativeReservedBytes == 0,
                "actor physical arena bytes survived terminal quiescence");
            attemptedTotal += attempted;
            admittedTotal += admitted;
        }
        Require(attemptedTotal == 6144,
            "expected 24 * 4 * 64 contested admission attempts");
        Console.WriteLine("NET_SUPERVISOR_DATA_FENCE_HOST_PASS" +
            "|attempts=" + attemptedTotal +
            "|admitted=" + admittedTotal +
            "|sibling=1|nativeaot_guest=0|supervision_tree=0");
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception(
            "Supervisor DATA fence parity failure: " + message);
    }
}
