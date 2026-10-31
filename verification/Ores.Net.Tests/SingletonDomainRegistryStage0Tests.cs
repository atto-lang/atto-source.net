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

    private static void RequireUnsupportedStage0Publication<T>(ActorRuntime runtime)
    {
        try
        {
            using var rejected = runtime.CreateSingletonDomainHostControlStage0<T>();
        }
        catch (NotSupportedException error) when (
            error.Message.Contains("ORES-SINGLETON-STAGE0-TYPE-UNSUPPORTED",
                StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception(
            "singleton stage0 accepted arbitrary reference/custom publication type: " +
            typeof(T).FullName);
    }

    // This test forces the first disposer to hold _disposeGate while blocked
    // on _gate, then verifies a second disposer cannot return prematurely.
    // Reflection only inspects host-owned test hooks; no guest gets either lock.
    private static void ConcurrentDisposersWaitForRetirement(ActorRuntime runtime)
    {
        using var controller = runtime.CreateSingletonDomainHostControlStage0<long>();
        controller.RegisterCompilerDeclaration("app/dispose.ores", "Dispose",
            () => ValueTask.FromResult(1L));

        Type type = typeof(SingletonDomainHostControlStage0<long>);
        object registryGate = type.GetField("_gate",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.GetValue(controller)!;
        object disposeGate = type.GetField("_disposeGate",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.GetValue(controller)!;

        Task? first = null;
        Task? second = null;
        using var secondStarted = new ManualResetEventSlim();
        Monitor.Enter(registryGate);
        try
        {
            first = Task.Run(() => controller.Dispose());

            // Confirm the first disposer is holding the disposal lock while
            // waiting for the registry gate that this thread deliberately owns.
            bool firstAcquired = SpinWait.SpinUntil(() =>
            {
                if (!Monitor.TryEnter(disposeGate)) return true;
                Monitor.Exit(disposeGate);
                return false;
            }, TimeSpan.FromSeconds(10));
            Require(firstAcquired, "first disposer never acquired serialization gate");

            second = Task.Run(() =>
            {
                secondStarted.Set();
                controller.Dispose();
            });
            Require(secondStarted.Wait(TimeSpan.FromSeconds(10)),
                "second disposer did not start");
            Require(!second.Wait(TimeSpan.FromMilliseconds(100)),
                "concurrent Dispose returned before retirement completed");
        }
        finally { Monitor.Exit(registryGate); }

        Require(first is not null && second is not null &&
            Task.WaitAll(new Task[] { first!, second! }, TimeSpan.FromSeconds(10)),
            "concurrent disposal did not finish after releasing the registry lock");
        Deny(() => { _ = controller.StartForHost("app/dispose.ores", "Dispose"); },
            "ORES-SINGLETON-DOMAIN-RETIRED");
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

        ConcurrentDisposersWaitForRetirement(runtime);

        // Host-only generic signatures must not permit mutable CLR object
        // identity to escape between physically isolated actor-owned arenas.
        // Stage-0 verified scalar initializer plans currently support i64 only.
        RequireUnsupportedStage0Publication<object>(runtime);
        RequireUnsupportedStage0Publication<long[]>(runtime);
        RequireUnsupportedStage0Publication<int>(runtime);
        RequireUnsupportedStage0Publication<DateTime>(runtime);
        using var domain = runtime.CreateSingletonDomainHostControlStage0<long>(
            maxDeclarations: 1, maxGrantsPerDeclaration: 1);
        using var healthyDomain = runtime.CreateSingletonDomainHostControlStage0<long>(
            maxDeclarations: 1, maxGrantsPerDeclaration: 1);

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
        ActorRef allowed = Reader(runtime, domain.Reader, allowedReceipts,
            context => capturedContext = context);
        ActorRef denied = Reader(runtime, domain.Reader, deniedReceipts);
        ActorRef foreign = Reader(foreignRuntime, domain.Reader, foreignReceipts);

        // Give the actor ONLY a read facade. Even if an unsafe C# host
        // callback accidentally captures the control object itself, calls
        // from the physical actor or its forked tasks must fail closed.
        Require(typeof(SingletonDomainRegistryStage0<long>).GetMethods()
                .Where(m => m.DeclaringType == typeof(SingletonDomainRegistryStage0<long>))
                .All(m => m.Name == "TryReadPublished"),
            "actor reader unexpectedly exposes administrative methods");
        Require(typeof(SingletonDomainHostControlStage0<long>).GetConstructors(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance).Length == 0,
            "host control constructor is still publicly forgeable");
        var carrierDenials = Pending<bool>();
        var offCarrierDenials = Pending<bool>();
        var delayedDenials = Pending<bool>();
        var afterTurn = Pending<bool>();

        ActorRef adversarial = default;
        bool AllAdminDenied()
        {
            int deniedOperations = 0;
            void RequireHostDeny(Action action)
            {
                try { action(); }
                catch (InvalidOperationException error) when (
                    error.Message.Contains("ORES-SINGLETON-HOST-ONLY",
                        StringComparison.Ordinal) ||
                    error.Message.Contains("ORES-SINGLETON-HOST-CONTROL-DENIED",
                        StringComparison.Ordinal) ||
                    error.Message.Contains("ORES-SINGLETON-HOST-ISSUANCE-DENIED",
                        StringComparison.Ordinal))
                {
                    deniedOperations++;
                }
            }
            RequireHostDeny(() => domain.RegisterCompilerDeclaration(
                "app/forged.ores", "Forged", () => ValueTask.FromResult(1L)));
            RequireHostDeny(() => domain.GrantRead(
                adversarial, "app/settings.ores", "Settings"));
            RequireHostDeny(() => domain.RevokeRead(
                allowed, "app/settings.ores", "Settings"));
            RequireHostDeny(() => { _ = domain.StartForHost(
                "app/settings.ores", "Settings"); });
            RequireHostDeny(() => { _ = domain.Retire(); });
            // Capturing the owning runtime must not allow issuance of an
            // entirely new host controller from a guest-created Task.Run.
            RequireHostDeny(() =>
            {
                using var forged = runtime.CreateSingletonDomainHostControlStage0<long>();
            });
            return deniedOperations == 6;
        }

        static async Task CheckBoth(Task<bool> flowed, Task<bool> suppressed,
            TaskCompletionSource<bool> result)
        {
            try
            {
                bool[] verdicts = await Task.WhenAll(flowed, suppressed)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                result.TrySetResult(verdicts.All(x => x));
            }
            catch (Exception error) { result.TrySetException(error); }
        }

        adversarial = runtime.Spawn(ActorKind.Untrusted, (_, _) =>
        {
            // Ordinary Task.Run inherits the actor provenance marker.
            Task<bool> flowed = Task.Run(AllAdminDenied);

            // SuppressFlow discards the marker, but ALSO discards the
            // unforgeable host issuer: controller reference alone is denied.
            Task<bool> suppressed;
            using (ExecutionContext.SuppressFlow())
                suppressed = Task.Run(AllAdminDenied);
            _ = CheckBoth(flowed, suppressed, offCarrierDenials);

            Task<bool> delayedFlowed = Task.Run(async () =>
            {
                await afterTurn.Task.ConfigureAwait(false);
                return AllAdminDenied();
            });
            Task<bool> delayedSuppressed;
            using (ExecutionContext.SuppressFlow())
                delayedSuppressed = Task.Run(async () =>
                {
                    await afterTurn.Task.ConfigureAwait(false);
                    return AllAdminDenied();
                });
            _ = CheckBoth(delayedFlowed, delayedSuppressed, delayedDenials);
            carrierDenials.TrySetResult(AllAdminDenied());
        });
        runtime.Send(adversarial, new byte[] { 1 });
        Require(await carrierDenials.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "actor carrier acquired host registry administration");
        Require(await offCarrierDenials.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "actor-created Task.Run administered registry with a captured controller");
        // Assert physical turn is closed before letting deferred tasks attempt
        // post-turn administration (also without ExecutionContext flow).
        await CompletedTurns(runtime, 1);
        afterTurn.TrySetResult(true);
        Require(await delayedDenials.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "post-turn child task regained host control using a captured reference");
        Require(domain.RegisteredDeclarations == 1,
            "failed actor mutation corrupted host registry state");

        // SuppressFlow drops the host issuer as well as the actor marker. A
        // task with both captured controller AND runtime must remain denied.
        Task<(bool controlDenied, bool issuanceDenied)> unissuedHost;
        using (ExecutionContext.SuppressFlow())
            unissuedHost = Task.Run(() =>
            {
                bool controlDenied = false, issuanceDenied = false;
                try { _ = domain.RegisteredDeclarations; }
                catch (InvalidOperationException error) when (
                    error.Message.Contains("ORES-SINGLETON-HOST-CONTROL-DENIED",
                        StringComparison.Ordinal)) { controlDenied = true; }
                try
                {
                    using var forged = runtime.CreateSingletonDomainHostControlStage0<long>();
                }
                catch (InvalidOperationException error) when (
                    error.Message.Contains("ORES-SINGLETON-HOST-ISSUANCE-DENIED",
                        StringComparison.Ordinal)) { issuanceDenied = true; }
                return (controlDenied, issuanceDenied);
            });
        var detached = await unissuedHost.WaitAsync(TimeSpan.FromSeconds(10));
        Require(detached.controlDenied && detached.issuanceDenied,
            "detached unissued task acquired captured controller or runtime issuance");

        // Deliberate host delegation is preserved across Task.Run/await.
        Require(await Task.Run(() => domain.RegisteredDeclarations) == 1,
            "legitimate host control delegation did not survive Task.Run");
        Require(await Task.Run(() =>
        {
            using var ephemeral = runtime.CreateSingletonDomainHostControlStage0<long>();
            ephemeral.RegisterCompilerDeclaration(
                "app/healthy.ores", "Healthy", () => ValueTask.FromResult(7L));
            return ephemeral.RegisteredDeclarations == 1;
        }), "legitimate host-bootstrap controller issuance failed");

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
            domain.Reader.TryReadPublished(capturedContext!, "app/settings.ores",
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
        ActorRef healthyActor = Reader(runtime, healthyDomain.Reader, healthyReceipts);
        Reading crossDomainDenied = await Send(runtime, healthyActor, healthyReceipts, 0);
        Require(crossDomainDenied.Error?.Contains("ORES-SINGLETON-ACTOR-DENIED",
            StringComparison.Ordinal) == true,
            "domain key identity implicitly conferred authority");
        healthyDomain.GrantRead(healthyActor, "app/settings.ores", "Settings");
        Require(await healthyDomain.StartForHost("app/settings.ores", "Settings") == 99,
            "independent domain initialized incorrectly");

        // A stopped incarnation must not permanently exhaust a bounded grant
        // list. The controller reclaims stale slots only on a host grant
        // request at capacity; it never transfers the old actor's authority.
        using var reclaimDomain = runtime.CreateSingletonDomainHostControlStage0<long>(
            maxDeclarations: 1, maxGrantsPerDeclaration: 1);
        reclaimDomain.RegisterCompilerDeclaration("app/settings.ores", "Settings",
            () => ValueTask.FromResult(73L));
        var reclaimReceipts = Receipts(2);
        ActorRef firstIncarnation = Reader(runtime, reclaimDomain.Reader, reclaimReceipts);
        ActorRef replacementIncarnation = Reader(runtime, reclaimDomain.Reader, reclaimReceipts);
        reclaimDomain.GrantRead(firstIncarnation, "app/settings.ores", "Settings");
        Deny(() => reclaimDomain.GrantRead(replacementIncarnation,
                "app/settings.ores", "Settings"),
            "ORES-SINGLETON-GRANT-CAPACITY");
        Require(await reclaimDomain.StartForHost("app/settings.ores", "Settings") == 73L,
            "replacement-domain singleton initializer failed");
        runtime.Stop(firstIncarnation);
        bool stoppedGrantDenied = false;
        try
        {
            reclaimDomain.GrantRead(firstIncarnation, "app/settings.ores", "Settings");
        }
        catch (InvalidOperationException) { stoppedGrantDenied = true; }
        Require(stoppedGrantDenied, "stopped incarnation was re-authorized");
        reclaimDomain.GrantRead(replacementIncarnation, "app/settings.ores", "Settings");
        Reading replacedValue = await Send(runtime, replacementIncarnation, reclaimReceipts, 1);
        Require(replacedValue.Ready && replacedValue.Value == 73L &&
                replacedValue.Error is null,
            "bounded stale grant was not reclaimed for a healthy incarnation");
        Require(!reclaimDomain.RevokeRead(
                firstIncarnation, "app/settings.ores", "Settings"),
            "stale incarnation remained in bounded grant table after reclamation");
        Require(reclaimDomain.RevokeRead(
                replacementIncarnation, "app/settings.ores", "Settings"),
            "replacement incarnation could not be explicitly revoked");
        Reading deniedAfterRevoke = await Send(
            runtime, replacementIncarnation, reclaimReceipts, 0);
        Require(deniedAfterRevoke.Error?.Contains("ORES-SINGLETON-ACTOR-DENIED",
            StringComparison.Ordinal) == true,
            "reclaimed replacement grant remained valid after revocation");

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

        using var retiringDomain = runtime.CreateSingletonDomainHostControlStage0<long>();
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
