using Ores.Net.Compiler;
using Ores.Net.Runtime;
using Oreslang.Generated;

internal static class ActorGeneratedCounterTests
{
    private const string CounterSource = """
        define isolated actor Counter as
            state {
                count: int = 0;
            }

            pub increment(): int using state.read, state.write {
                return self.handle_state(critical |s| -> {
                    s.count = s.count + 1;
                    return s.count;
                });
            }
        end
        """;

    private static void Require(bool cond, string message)
    {
        if (!cond) throw new Exception("generated Counter: " + message);
    }

    private static void Reject(string source, string message)
    {
        try { _ = OresParser.InspectSynchronousActorIncrement(source); }
        catch (FormatException) { return; }
        throw new Exception("actor source was admitted: " + message);
    }

    public static void Run()
    {
        var ir = OresParser.InspectSynchronousActorIncrement(CounterSource);
        Require(ir.Schema.ActorName == "Counter" && ir.MethodName == "increment" &&
            ir.FieldName == "count" && ir.FieldIndex == 0 && ir.Delta == 1,
            "source method was not lowered to typed field index");
        var generated = ActorIncrementSourceEmitter.EmitCSharp(ir);
        // The AOT-compiled golden must match the compiler output exactly.
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "GeneratedCounterActor.g.cs"));
        Require(generated.Replace("\r\n", "\n", StringComparison.Ordinal) ==
                golden.Replace("\r\n", "\n", StringComparison.Ordinal),
            "static compiled golden diverged from actor source emitter");
        Require(generated.Contains("public static class Actor_Counter", StringComparison.Ordinal) &&
            generated.Contains("public static long Call_increment(OresVmHost.ActorDispatchScope scope)", StringComparison.Ordinal) &&
            generated.Contains("host.ConfigureActorState(isolate, actor,", StringComparison.Ordinal) &&
            generated.Contains("Int64Default: 0L", StringComparison.Ordinal) &&
            generated.Contains("return scope.AddInt64(0, 1L);", StringComparison.Ordinal) &&
            !generated.Contains("BeginActorTurn(", StringComparison.Ordinal) &&
            !generated.Contains("CreateTrustedScheduler(", StringComparison.Ordinal) &&
            !generated.Contains("ValidateActorStateLoan(", StringComparison.Ordinal),
            "generated AOT source must use the checked host turn/loan operation");
        bool scalarDenied = false;
        try { _ = OresParser.Parse(CounterSource); }
        catch (FormatException e) when (e.Message.Contains("generation-fenced logical-turn lowering is unavailable",
            StringComparison.Ordinal)) { scalarDenied = true; }
        Require(scalarDenied, "scalar source path must not bypass actor gate");

        Reject(CounterSource.Replace("critical |s|", "|s|", StringComparison.Ordinal),
            "unmarked critical callback");
        Reject(CounterSource.Replace("state.read, state.write", "state.read", StringComparison.Ordinal),
            "read-only effects cannot permit write");
        Reject(CounterSource.Replace("count = s.count + 1", "count = s.count + 2",
            StringComparison.Ordinal), "unsupported increment delta");
        Reject(CounterSource.Replace("return s.count;", "return s;",
            StringComparison.Ordinal), "state capability return escape");
        Reject(CounterSource.Replace("return s.count;", "return s.enabled;",
            StringComparison.Ordinal), "different return field");
        Reject(CounterSource.Replace("s.count = s.count + 1", "s.count = t.count + 1",
            StringComparison.Ordinal), "unauthorized state alias");
        Reject(CounterSource.Replace("count: int", "count: bool",
            StringComparison.Ordinal).Replace("= 0;", "= true;", StringComparison.Ordinal),
            "increment of boolean state");
        Reject(CounterSource.Replace("end", "pub other(): int { return 1; } end",
            StringComparison.Ordinal), "extra actor method must reject");

        // These are statically checked-in generated C# fixture methods.
        // They execute only if the .NET test host actually compiles/runs.
        var vm = new OresVmHost(maxTenants: 2);
        var tenantA = vm.CreateIsolate("counter-a", maxActors: 2);
        var tenantB = vm.CreateIsolate("counter-b", maxActors: 1);
        vm.Start(tenantA); vm.Start(tenantB);
        var scheduler = vm.CreateTrustedScheduler();
        var a = Actor_Counter.Create(vm, tenantA, 7);
        var a2 = Actor_Counter.Create(vm, tenantA, 8);
        var b = Actor_Counter.Create(vm, tenantB, 7);
        Require(scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment) == 1, "A1");
        Require(scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment) == 2, "A2");
        Require(scheduler.Dispatch(tenantB, b, Actor_Counter.Call_increment) == 1, "B1");
        Require(scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment) == 3, "A3");
        Require(scheduler.Dispatch(tenantB, b, Actor_Counter.Call_increment) == 2, "B2");
        Require(scheduler.Dispatch(tenantA, a2, Actor_Counter.Call_increment) == 1,
            "same-tenant actor must be independent");

        var deniedReentry = false;
        _ = scheduler.Dispatch(tenantA, a, scope => {
            try { _ = scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment); }
            catch (InvalidOperationException) { deniedReentry = true; }
            return scope.ReadInt64(0);
        });
        Require(deniedReentry, "actor reentered inside its own state loan");
        bool foreignThreadDenied = false;
        _ = scheduler.Dispatch(tenantA, a, scope => {
            foreignThreadDenied = Task.Run(() => {
                try { _ = scope.AddInt64(0, 1); return false; }
                catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            return scope.ReadInt64(0);
        });
        Require(foreignThreadDenied, "scope crossed a foreign physical thread");
        OresVmHost.ActorDispatchScope? failedScope = null;
        try {
            _ = scheduler.Dispatch(tenantA, a, scope => {
                failedScope = scope;
                throw new InvalidOperationException("guest failure");
            });
            throw new Exception("expected test guest failure");
        }
        catch (InvalidOperationException e) when (e.Message == "guest failure") { }
        bool cancelledScopeRejected = false;
        try { _ = failedScope!.AddInt64(0, 1); }
        catch (InvalidOperationException) { cancelledScopeRejected = true; }
        Require(cancelledScopeRejected, "faulted dispatch scope remained live");
        Require(scheduler.Dispatch(tenantA, a, Actor_Counter.Call_increment) == 4,
            "faulting actor turn was not cancelled");

        OresVmHost.ActorDispatchScope? escaped = null;
        _ = scheduler.Dispatch(tenantA, a, scope => {
            escaped = scope;
            return scope.ReadInt64(0);
        });
        bool replayDenied = false;
        try { _ = escaped!.AddInt64(0, 1); }
        catch (InvalidOperationException) { replayDenied = true; }
        Require(replayDenied, "retired dispatch scope could mutate after completion");

        vm.RetireActor(tenantA, a); vm.RetireActor(tenantA, a2);
        vm.RetireActor(tenantB, b);
        vm.BeginShutdown(tenantA); vm.CompleteShutdown(tenantA); vm.Destroy(tenantA);
        vm.BeginShutdown(tenantB); vm.CompleteShutdown(tenantB); vm.Destroy(tenantB);
        var replacement = vm.CreateIsolate("counter-a", maxActors: 1);
        vm.Start(replacement);
        var again = Actor_Counter.Create(vm, replacement, 7);
        Require(scheduler.Dispatch(replacement, again, Actor_Counter.Call_increment) == 1,
            "reincarnated actor reused stale state");
        vm.RetireActor(replacement, again);
        vm.BeginShutdown(replacement); vm.CompleteShutdown(replacement);
        vm.Destroy(replacement);
    }
}
