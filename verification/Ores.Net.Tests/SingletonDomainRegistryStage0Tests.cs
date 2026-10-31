using Ores.Net.Runtime;

// Executed host ActorRuntime turns (once a .NET runner is available).
// This is NOT compiled Oreslang guest access, tenant heap isolation, or
// cross-isolate actor authority. All registration and grants are trusted-host.
internal static class SingletonDomainRegistryStage0Tests
{
    private readonly record struct Reading(bool Ready, long Value, string? Error);

    private static TaskCompletionSource<T> Pending<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Exception("singleton domain stage0: " + reason);
    }

    private static void Deny(Action action, string category)
    {
        try { action(); }
        catch (InvalidOperationException error) when (
            error.Message.Contains(category, StringComparison.Ordinal)) { return; }
        throw new Exception("singleton domain stage0: missing denial " + category);
    }

    private static ActorRuntimeOptions Options() => new()
    {
        MaxActors = 8,
        MessagesPerDispatch = 8,
        MailboxMessages = 32,
        MailboxBytes = 4096,
        ActorPersistentBytes = 4096,
        ActorScratchBytes = 512,
        ActorMaxAllocations = 16,
        ActorMaxLifetimePins = 4,
        ActorMaxReadLoans = 4,
        NativeBudgetBytes = 1024 * 1024,
        Dispatcher = new ActorDispatcherOptions
        {
            PrivateWorkers = 1,
            SharedWorkers = 1,
            UntrustedWorkers = 1,
            QueueCapacityPerDomain = 16
        }
    };

    private static ActorRef Reader(
        ActorRuntime runtime,
        SingletonDomainRegistryStage0<long> domain,
        TaskCompletionSource<Reading>[] receipts,
        Action<ActorContext>? capture = null) =>
        runtime.Spawn(ActorKind.Shared, (context, payload) =>
        {
            int slot = payload.Span[0];
            try
            {
                capture?.Invoke(context);
                bool ready = domain.TryReadPublished(
                    context, "app/settings.ores", "Settings", out long value);
                receipts[slot].TrySetResult(new Reading(ready, value, null));
            }
            catch (Exception error)
            {
                receipts[slot].TrySetResult(new Reading(false, 0, error.Message));
            }
        });

    private static TaskCompletionSource<Reading>[] Receipts(int count) =>
        Enumerable.Range(0, count).Select(_ => Pending<Reading>()).ToArray();

    private static async Task<Reading> Send(
        ActorRuntime runtime, ActorRef actor,
        TaskCompletionSource<Reading>[] receipts, int slot)
    {
        runtime.Send(actor, new[] { (byte)slot });
        return await receipts[slot].Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task CompletedTurns(ActorRuntime runtime, long count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (runtime.CompletedMessages < count)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    public static async Task Run()
    {
        using var runtime = new ActorRuntime(Options());
        using var foreignRuntime = new ActorRuntime(Options());
        using var domain = new SingletonDomainRegistryStage0<long>(
            runtime, maxDeclarations: 1, maxGrantsPerDeclaration: 1);
        using var healthyDomain = new SingletonDomainRegistryStage0<long>(
            runtime, maxDeclarations: 1, maxGrantsPerDeclaration: 1);

        var entered = Pending<bool>();
        var release = Pending<long>();
        int initializers = 0;
        domain.RegisterCompilerDeclaration(
            "app/settings.ores", "Settings", async () =>
            {
                Interlocked.Increment(ref initializers);
                entered.TrySetResult(true);
                return await release.Task.ConfigureAwait(false);
            });
        healthyDomain.RegisterCompilerDeclaration(
            "app/settings.ores", "Settings", () => ValueTask.FromResult(99L));
        Require(domain.RegisteredDeclarations == 1 &&
                healthyDomain.RegisteredDeclarations == 1,
            "domain registration count wrong");

        Deny(() => domain.RegisterCompilerDeclaration(
            "app/settings.ores", "Settings", () => ValueTask.FromResult(7L)),
            "ORES-SINGLETON-DUPLICATE");
        Deny(() => domain.RegisterCompilerDeclaration(
            "app/other.ores", "Other", () => ValueTask.FromResult(7L)),
            "ORES-SINGLETON-DOMAIN-CAPACITY");

        bool invalidKey = false;
        try
        {
            domain.RegisterCompilerDeclaration(
                "../escape.ores", "Settings", () => ValueTask.FromResult(3L));
        }
        catch (ArgumentException error) when (
            error.Message.Contains("ORES-SINGLETON-KEY", StringComparison.Ordinal))
        { invalidKey = true; }
        Require(invalidKey, "unsafe compiler module identity was admitted");

        var allowedReceipts = Receipts(5);
        var deniedReceipts = Receipts(3);
        var foreignReceipts = Receipts(1);
        ActorContext? capturedContext = null;
        ActorRef allowed = Reader(runtime, domain, allowedReceipts,
            context => capturedContext = context);
        ActorRef denied = Reader(runtime, domain, deniedReceipts);
        ActorRef foreign = Reader(foreignRuntime, domain, foreignReceipts);

        domain.GrantRead(allowed, "app/settings.ores", "Settings");
        domain.GrantRead(allowed, "app/settings.ores", "Settings"); // idempotent
        Deny(() => domain.GrantRead(denied, "app/settings.ores", "Settings"),
            "ORES-SINGLETON-GRANT-CAPACITY");
        bool foreignGrantRejected = false;
        try { domain.GrantRead(foreign, "app/settings.ores", "Settings"); }
        catch (InvalidOperationException) { foreignGrantRejected = true; }
        Require(foreignGrantRejected, "foreign runtime ActorRef was granted access");

        Reading before = await Send(runtime, allowed, allowedReceipts, 0);
        Require(!before.Ready && before.Error is null,
            "not-started singleton exposed a value or an error");

        Task<long> started = domain.StartForHost("app/settings.ores", "Settings");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Reading pending = await Send(runtime, allowed, allowedReceipts, 1);
        Require(!pending.Ready && pending.Error is null && initializers == 1,
            "incomplete value was published or initializer duplicated");

        Reading unauthorized = await Send(runtime, denied, deniedReceipts, 0);
        Require(unauthorized.Error?.Contains("ORES-SINGLETON-ACTOR-DENIED",
            StringComparison.Ordinal) == true,
            "missing read grant did not fail closed");
        Reading foreignAttempt = await Send(foreignRuntime, foreign, foreignReceipts, 0);
        Require(foreignAttempt.Error?.Contains("ORES-SINGLETON-FOREIGN-RUNTIME",
            StringComparison.Ordinal) == true,
            "foreign runtime physical turn was accepted");

        release.SetResult(42);
        Require(await started.WaitAsync(TimeSpan.FromSeconds(10)) == 42 &&
                initializers == 1, "host initializer did not publish once");
        Reading published = await Send(runtime, allowed, allowedReceipts, 2);
        Require(published.Ready && published.Value == 42 && published.Error is null,
            "authorized turn did not read fully published value");

        // Receiving a Task would let an actor retain a future after its turn.
        // This bridge deliberately exposes a synchronous ready-only read.
        await CompletedTurns(runtime, 4);
        Require(capturedContext is not null, "actor context was never captured");
        bool staleContextDenied = false;
        try
        {
            domain.TryReadPublished(capturedContext!, "app/settings.ores",
                "Settings", out _);
        }
        catch (InvalidOperationException) { staleContextDenied = true; }
        Require(staleContextDenied,
            "retained ActorContext was valid outside its physical dispatch turn");

        Require(domain.RevokeRead(allowed, "app/settings.ores", "Settings"),
            "grant revocation failed");
        Reading revoked = await Send(runtime, allowed, allowedReceipts, 3);
        Require(revoked.Error?.Contains("ORES-SINGLETON-ACTOR-DENIED",
            StringComparison.Ordinal) == true,
            "revoked actor retained singleton authority");
        domain.GrantRead(denied, "app/settings.ores", "Settings");
        Reading newlyGranted = await Send(runtime, denied, deniedReceipts, 1);
        Require(newlyGranted.Ready && newlyGranted.Value == 42,
            "released grant capacity could not be reassigned");

        // A second registry with the same module/declaration key is an
        // independent domain; the first domain's grants do not carry across.
        var healthyReceipts = Receipts(2);
        ActorRef healthyActor = Reader(runtime, healthyDomain, healthyReceipts);
        Reading crossDomainDenied = await Send(runtime, healthyActor, healthyReceipts, 0);
        Require(crossDomainDenied.Error?.Contains("ORES-SINGLETON-ACTOR-DENIED",
            StringComparison.Ordinal) == true,
            "domain key identity implicitly conferred authority");
        healthyDomain.GrantRead(healthyActor, "app/settings.ores", "Settings");
        Require(await healthyDomain.StartForHost("app/settings.ores", "Settings") == 99,
            "independent domain initialized incorrectly");

        Require(domain.Retire() && !domain.Retire(),
            "domain retirement was not single-winner");
        Reading retired = await Send(runtime, denied, deniedReceipts, 2);
        Require(retired.Error?.Contains("ORES-SINGLETON-DOMAIN-RETIRED",
            StringComparison.Ordinal) == true,
            "retired domain exposed prior publication");
        Deny(() => domain.StartForHost("app/settings.ores", "Settings"),
            "ORES-SINGLETON-DOMAIN-RETIRED");
        Reading healthy = await Send(runtime, healthyActor, healthyReceipts, 1);
        Require(healthy.Ready && healthy.Value == 99,
            "retiring one application domain stopped an independent domain");

        runtime.Stop(denied);
        bool staleGrantRejected = false;
        try { healthyDomain.GrantRead(denied, "app/settings.ores", "Settings"); }
        catch (InvalidOperationException) { staleGrantRejected = true; }
        Require(staleGrantRejected, "stopped incarnation acquired a new grant");

        using var retiringDomain = new SingletonDomainRegistryStage0<long>(runtime);
        var lateEntered = Pending<bool>();
        var lateRelease = Pending<long>();
        retiringDomain.RegisterCompilerDeclaration("app/late.ores", "Late", async () =>
        {
            lateEntered.TrySetResult(true);
            return await lateRelease.Task.ConfigureAwait(false);
        });
        Task<long> pendingLate = retiringDomain.StartForHost("app/late.ores", "Late");
        await lateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        retiringDomain.Retire();
        bool fenced = false;
        try { _ = await pendingLate.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (InvalidOperationException error) when (
            error.Message.Contains("ORES-SINGLETON-RETIRED", StringComparison.Ordinal))
        { fenced = true; }
        Require(fenced, "domain shutdown did not fail a pending publication");
        lateRelease.SetResult(12);
        Deny(() => retiringDomain.StartForHost("app/late.ores", "Late"),
            "ORES-SINGLETON-DOMAIN-RETIRED");

        Console.WriteLine("singleton domain stage0: actor-turn issuer, scoped grants, ready-only publication, revoke/retirement and healthy-domain progress checks PASS (not guest/tenant-isolate parity)");
    }
}
