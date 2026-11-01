using Ores.Net.Runtime;

internal static class ActorDispatchDomainsSelfTest
{
    public static void Run()
    {
        RejectOversizedSchedulerConfiguration();
        var options = new ActorDispatcherOptions
        {
            PrivateWorkers = 2,
            SharedWorkers = 2,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 32
        };

        using var domains = new ActorDispatchDomains(options);
        Require(domains.TotalStartedWorkers == 0,
            "dispatch domain structures must start with zero carrier threads");

        string? privateThread = null;
        string? sharedThread = null;
        string? untrustedThread = null;
        using var started = new CountdownEvent(3);

        Require(domains.TryDispatch(ActorKind.Private, () =>
        {
            privateThread = Thread.CurrentThread.Name;
            started.Signal();
        }), "private dispatch admission");
        Require(domains.TryDispatch(ActorKind.Shared, () =>
        {
            sharedThread = Thread.CurrentThread.Name;
            started.Signal();
        }), "shared dispatch admission");
        Require(domains.TryDispatch(ActorKind.Untrusted, () =>
        {
            untrustedThread = Thread.CurrentThread.Name;
            started.Signal();
        }), "untrusted dispatch admission");

        Require(started.Wait(TimeSpan.FromSeconds(5)),
            "three actor domains execute admitted work");
        Require(domains.PrivateStartedWorkers >= 1 &&
            domains.SharedStartedWorkers >= 1 &&
            domains.UntrustedStartedWorkers >= 1,
            "each used domain lazily starts a carrier");
        Require(privateThread?.StartsWith("ores-net-private-",
                    StringComparison.Ordinal) == true,
            "private actor uses private carrier domain");
        Require(sharedThread?.StartsWith("ores-net-shared-",
                    StringComparison.Ordinal) == true,
            "shared actor uses shared carrier domain");
        Require(untrustedThread?.StartsWith("ores-net-untrusted-",
                    StringComparison.Ordinal) == true,
            "untrusted actor uses untrusted carrier domain");

        using var afterFault = new ManualResetEventSlim();
        Require(domains.TryDispatch(ActorKind.Private,
            static () => throw new InvalidOperationException("synthetic actor turn fault")),
            "faulting work admitted");
        Require(domains.TryDispatch(ActorKind.Private, afterFault.Set),
            "work after a fault admitted");
        Require(afterFault.Wait(TimeSpan.FromSeconds(5)),
            "carrier survives one failed work item");

        Require(SpinWait.SpinUntil(
            () => domains.CompletedWorkItems >= 5 &&
                domains.FaultedWorkItems == 1,
            TimeSpan.FromSeconds(5)),
            "dispatch completion/fault accounting converges");

        using var selfDisposeDone = new ManualResetEventSlim();
        bool selfDisposeRejected = false;
        Require(domains.TryDispatch(ActorKind.Shared, () =>
        {
            try
            {
                domains.Dispose();
            }
            catch (InvalidOperationException)
            {
                selfDisposeRejected = true;
            }
            finally
            {
                selfDisposeDone.Set();
            }
        }), "self-dispose probe admitted");
        Require(selfDisposeDone.Wait(TimeSpan.FromSeconds(5)) &&
            selfDisposeRejected,
            "carrier self-dispose is rejected instead of self-joining");

        ConcurrentDisposeWithoutStartedCarriers();
        // Each physical actor domain must participate in the same join fence.
        foreach (var kind in new[] {
            ActorKind.Private, ActorKind.Shared, ActorKind.Untrusted
        })
            ConcurrentDisposeWaitsForCarrierJoin(kind);

        Console.WriteLine(
            "PASS .NET three lazy actor dispatch domains with no per-actor pools");
    }

    private static void ConcurrentDisposeWithoutStartedCarriers()
    {
        // Empty pools must resolve all simultaneous shutdown callers without
        // creating a background thread or leaving a waiter parked forever.
        using var domains = new ActorDispatchDomains(new ActorDispatcherOptions
        {
            PrivateWorkers = 1,
            SharedWorkers = 1,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 4
        });
        const int callers = 8;
        using var start = new ManualResetEventSlim();
        using var begun = new CountdownEvent(callers);
        var tasks = new Task[callers];
        for (int i = 0; i < tasks.Length; ++i)
        {
            tasks[i] = Task.Factory.StartNew(() =>
            {
                begun.Signal();
                if (!start.Wait(TimeSpan.FromSeconds(5)))
                    throw new Exception("zero-worker shutdown start gate timed out");
                domains.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
               TaskScheduler.Default);
        }
        try
        {
            Require(begun.Wait(TimeSpan.FromSeconds(5)),
                "all zero-worker disposal callers are scheduled");
        }
        finally
        {
            start.Set();
        }
        Require(Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(5)),
            "all zero-worker disposers must complete");
        Require(domains.TotalStartedWorkers == 0,
            "Dispose must not start idle carriers");
        foreach (var kind in new[] {
            ActorKind.Private, ActorKind.Shared, ActorKind.Untrusted
        })
            Require(!domains.TryDispatch(kind, static () => { }),
                "zero-worker disposed domain rejects late work: " + kind);
        domains.Dispose();
    }

    private static void ConcurrentDisposeWaitsForCarrierJoin(ActorKind kind)
    {
        // No reflection and no production instrumentation: the public
        // TryDispatch boundary proves that the first disposer has actually
        // closed admission while an admitted carrier is still running.
        using var domains = new ActorDispatchDomains(new ActorDispatcherOptions
        {
            PrivateWorkers = 1,
            SharedWorkers = 1,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 1024
        });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        bool carrierSelfDisposeRejected = false;
        Require(domains.TryDispatch(kind, () =>
        {
            entered.Set();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new Exception("blocked carrier exceeded shutdown test budget");
                // Reject self-Dispose even after global shutdown has started.
                // Waiting on the completion fence from this carrier would
                // deadlock the first disposer, which is joining this thread.
                try { domains.Dispose(); }
                catch (InvalidOperationException) { carrierSelfDisposeRejected = true; }
            }
            finally
            {
                exited.Set();
            }
        }), "blocked carrier is admitted: " + kind);
        Require(entered.Wait(TimeSpan.FromSeconds(5)),
            "blocked carrier reaches the physical turn: " + kind);

        Task first = Task.Factory.StartNew(() =>
        {
            firstEntered.Set();
            domains.Dispose();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Require(firstEntered.Wait(TimeSpan.FromSeconds(5)),
                "first disposer has started: " + kind);
            bool closed = false;
            // Poll at a bounded rate. The capacity is 1024, so at most 500
            // accepted no-op items cannot themselves exhaust the queue and
            // create a false-positive "closed" observation.
            for (int attempt = 0; attempt < 500 && !closed; ++attempt)
            {
                closed = !domains.TryDispatch(kind, static () => { });
                if (!closed) Thread.Sleep(1);
            }
            Require(closed, "first disposer closes admission: " + kind);

            Task second = Task.Factory.StartNew(() =>
            {
                secondEntered.Set();
                domains.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Require(secondEntered.Wait(TimeSpan.FromSeconds(5)),
                "concurrent second disposer entered: " + kind);
            Require(!second.Wait(TimeSpan.FromMilliseconds(75)),
                "second Dispose returned before carrier join: " + kind);
            Require(!first.IsCompleted && !exited.IsSet,
                "first Dispose and carrier must still be active: " + kind);

            // A burst of independent shutdown callers must all observe
            // physical quiescence, not just the first and second caller.
            const int burstSize = 4;
            using var burstStarted = new CountdownEvent(burstSize);
            var burst = new Task[burstSize];
            for (int i = 0; i < burst.Length; ++i)
            {
                burst[i] = Task.Factory.StartNew(() =>
                {
                    burstStarted.Signal();
                    domains.Dispose();
                }, CancellationToken.None, TaskCreationOptions.LongRunning,
                   TaskScheduler.Default);
            }
            Require(burstStarted.Wait(TimeSpan.FromSeconds(5)),
                "all concurrent Dispose callers have started: " + kind);
            Require(!Task.WhenAll(burst).Wait(TimeSpan.FromMilliseconds(75)),
                "parallel Dispose callers must wait for carrier join: " + kind);

            release.Set();
            Require(first.Wait(TimeSpan.FromSeconds(5)) &&
                    second.Wait(TimeSpan.FromSeconds(5)) &&
                    Task.WhenAll(burst).Wait(TimeSpan.FromSeconds(5)) &&
                    exited.IsSet && carrierSelfDisposeRejected,
                "all callers join and carrier rejects self-Dispose: " + kind);
            Require(!domains.TryDispatch(kind, static () => { }),
                "no dispatch admission after completed Dispose: " + kind);
            // Repeated shutdown must also be safe on the caller thread.
            domains.Dispose();
        }
        finally
        {
            // Always release the carrier, including on assertion failure.
            release.Set();
        }
    }

    private static void RejectOversizedSchedulerConfiguration()
    {
        static bool Denied(ActorDispatcherOptions options)
        {
            try
            {
                using var invalid = new ActorDispatchDomains(options);
                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                return true;
            }
        }

        Require(Denied(new ActorDispatcherOptions
        {
            PrivateWorkers = ActorDispatcherOptions.MaxWorkersPerDomain + 1
        }), "unbounded private carrier configuration rejected");
        Require(Denied(new ActorDispatcherOptions
        {
            SharedWorkers = ActorDispatcherOptions.MaxWorkersPerDomain + 1
        }), "unbounded shared carrier configuration rejected");
        Require(Denied(new ActorDispatcherOptions
        {
            UntrustedWorkers = ActorDispatcherOptions.MaxWorkersPerDomain + 1
        }), "unbounded untrusted carrier configuration rejected");
        Require(Denied(new ActorDispatcherOptions
        {
            QueueCapacityPerDomain = ActorDispatcherOptions.MaxQueueCapacityPerDomain + 1
        }), "unbounded dispatch queue capacity rejected");
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor dispatch-domain regression: " + scenario);
    }
}

