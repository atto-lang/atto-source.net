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

        Console.WriteLine(
            "PASS .NET three lazy actor dispatch domains with no per-actor pools");
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
