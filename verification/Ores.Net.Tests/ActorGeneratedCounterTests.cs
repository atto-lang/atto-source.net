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
                return self.handle_state(critical-write |s| -> {
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

    private static long Deliver(OresVmHost.ActorScheduler scheduler,
        OresVmHost.IsolateHandle isolate, OresVmHost.ActorTicket actor,
        Func<OresVmHost.ActorDispatchScope, long> generatedMethod)
    {
        scheduler.Enqueue(isolate, actor);
        return scheduler.DispatchNext(isolate, actor, Actor_Counter.ActorLayoutIdentity, generatedMethod);
    }

    public static void Run()
    {
        ActorGeneratedRuntimeCounterChecks.Run();
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
        var overflowSource = CounterSource.Replace("actor Counter as", "actor OverflowCounter as", StringComparison.Ordinal)
            .Replace("count: int = 0;", "count: int = 9223372036854775807;", StringComparison.Ordinal);
        var overflowGolden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GeneratedOverflowCounterActor.g.cs"));
        Require(ActorIncrementSourceEmitter.EmitCSharp(OresParser.InspectSynchronousActorIncrement(overflowSource))
                .Replace("\r\n", "\n", StringComparison.Ordinal) == overflowGolden.Replace("\r\n", "\n", StringComparison.Ordinal),
            "overflow source/golden mismatch");
        // This is a genuine source-checked message-payload guest slice:
        // a single owned `int incoming` drives the actor-local checked add,
        // not a trusted-host fabricated value or legacy fixed +1 control tick.
        var messageSource = CounterSource
            .Replace("actor Counter as", "actor MessageCounter as", StringComparison.Ordinal)
            .Replace("increment(): int", "increment(int incoming): int", StringComparison.Ordinal)
            .Replace("s.count + 1", "s.count + incoming", StringComparison.Ordinal);
        var messageIr = OresParser.InspectSynchronousActorIncrement(messageSource);
        Require(messageIr.ScalarMailParameter == "incoming" && messageIr.Delta == 0 &&
            messageIr.Schema.ActorName == "MessageCounter",
            "typed mailbox input did not survive checked source IR");
        var messageGolden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "GeneratedMessageCounterActor.g.cs"));
        var messageGenerated = ActorIncrementSourceEmitter.EmitCSharp(messageIr);
        Require(!generated.Contains('\r') && !messageGenerated.Contains('\r'),
            "actor emitter must produce canonical LF-only NativeAOT source");
        Require(messageGenerated == messageGolden.Replace("\r\n", "\n", StringComparison.Ordinal),
            "typed actor source must be reproducible byte-identically across host OS");
        Require(messageGenerated.Replace("\r\n", "\n", StringComparison.Ordinal) ==
                messageGolden.Replace("\r\n", "\n", StringComparison.Ordinal),
            "typed-mail generated NativeAOT source differs from checked golden");
        // Omitted self is only a source syntax alias for the same compiler
        // intrinsic and must generate identical static NativeAOT code.
        var implicitSelfSource = CounterSource.Replace("return self.handle_state(",
            "return handle_state(", StringComparison.Ordinal);
        var implicitSelfIr = OresParser.InspectSynchronousActorIncrement(implicitSelfSource);
        Require(ActorIncrementSourceEmitter.EmitCSharp(implicitSelfIr) == generated,
            "omitted-self literal source diverged from explicit self");
        var implicitMessageSource = messageSource.Replace("return self.handle_state(",
            "return handle_state(", StringComparison.Ordinal);
        var implicitMessageIr = OresParser.InspectSynchronousActorIncrement(implicitMessageSource);
        Require(implicitMessageIr.ScalarMailParameter == "incoming" &&
                ActorIncrementSourceEmitter.EmitCSharp(implicitMessageIr) == messageGenerated,
            "omitted-self typed-mail source must preserve owned input");
        Reject(implicitMessageSource.Replace("incoming", "handle_state", StringComparison.Ordinal),
            "method parameter shadows omitted-self state intrinsic");
        Reject(messageSource.Replace("incoming", "self", StringComparison.Ordinal),
            "message parameter shadows actor self");
        Reject(implicitMessageSource.Replace("critical-write |s|", "critical-write |self|",
                StringComparison.Ordinal).Replace("s.count", "self.count", StringComparison.Ordinal),
            "borrowed state parameter shadows actor self");
        Reject(implicitSelfSource.Replace("return handle_state(",
                "return state.handle_state(", StringComparison.Ordinal),
            "arbitrary receiver cannot invoke sealed state intrinsic");
        Require(messageGenerated.Contains("ReadInt64LittleEndian(message.Span)", StringComparison.Ordinal) &&
            messageGenerated.Contains("state.AddInt64(context, 0, delta)", StringComparison.Ordinal) &&
            !messageGenerated.Contains("Call_increment(", StringComparison.Ordinal) &&
            !messageGenerated.Contains("Dispatch_increment(", StringComparison.Ordinal),
            "typed source mail must never fall back to no-arg host dispatch");
        Reject(messageSource.Replace("s.count + incoming", "s.count + unknown", StringComparison.Ordinal),
            "unbound message name");
        Reject(messageSource.Replace("increment(int incoming)", "increment(int incoming, int extra)",
            StringComparison.Ordinal), "multiple message captures");
        Reject(messageSource.Replace("int incoming", "borrow int incoming", StringComparison.Ordinal),
            "borrowed message captured across mailbox");
        Reject(messageSource.Replace("s.count + incoming", "s.count + 1", StringComparison.Ordinal),
            "message parameter cannot be ignored");
        Reject(messageSource.Replace("critical-write", "critical-read", StringComparison.Ordinal),
            "typed mail must not authorize reader mutation");
        foreach (var reserved in new[] {
            "receive", "init", "send", "end", "tear_down", "on_start",
            "on_stop", "on_restart", "on_panic", "on_throw",
            "on_throws", "on_raise", "on_recover", "handle_state"
        })
        {
            Reject(messageSource.Replace("increment(int incoming)",
                reserved + "(int incoming)", StringComparison.Ordinal),
                "privileged actor hook selector: " + reserved);
        }

        Reject(CounterSource.Replace("critical-write", "critical-read", StringComparison.Ordinal), "read loan cannot increment");
        Reject(CounterSource.Replace("critical-write", "critical", StringComparison.Ordinal),
            "bare legacy critical must not be granted an exclusive write loan");
        Reject(CounterSource.Replace("critical-write", "critical -write", StringComparison.Ordinal), "split critical mode");
        Reject(CounterSource.Replace("critical-write", "critical- write", StringComparison.Ordinal),
            "spaced critical mode suffix");
        Reject(CounterSource.Replace("critical-write", "critical--write", StringComparison.Ordinal),
            "double-hyphen critical mode");
        Require(generated.Contains("public static class Actor_Counter", StringComparison.Ordinal) &&
            generated.Contains("public const string ActorLayoutIdentity = \"Counter|1:I\";", StringComparison.Ordinal) &&
            generated.Contains("public static long Call_increment(OresVmHost.ActorDispatchScope scope)", StringComparison.Ordinal) &&
            generated.Contains("isolate, actor, ActorName, ActorLayoutIdentity,", StringComparison.Ordinal) &&
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

        Reject(CounterSource.Replace("critical-write |s|", "|s|", StringComparison.Ordinal),
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
        var unconfigured = vm.RegisterActor(tenantA, 99);
        scheduler.Enqueue(tenantA, unconfigured);
        Require(scheduler.PendingMessages(tenantA, unconfigured) == 1,
            "unconfigured actor control setup failed");
        bool unconfiguredDenied = false;
        try { _ = scheduler.DispatchNext(tenantA, unconfigured, Actor_Counter.ActorLayoutIdentity,
            Actor_Counter.Call_increment); }
        catch (InvalidOperationException e) when (
            e.Message.Contains("state layout", StringComparison.Ordinal)) {
            unconfiguredDenied = true;
        }
        Require(unconfiguredDenied &&
                scheduler.PendingMessages(tenantA, unconfigured) == 1,
            "unconfigured actor consumed its FIFO control receipt");
        vm.ConfigureNamedActorState(tenantA, unconfigured, Actor_Counter.ActorName, Actor_Counter.ActorLayoutIdentity,
            new OresVmHost.ActorStateField[] {
                new(OresVmHost.ActorStateScalarKind.Int64, Int64Default: 0L)
            });
        Require(scheduler.DispatchNext(tenantA, unconfigured, Actor_Counter.ActorLayoutIdentity,
                    Actor_Counter.Call_increment) == 1 &&
                scheduler.PendingMessages(tenantA, unconfigured) == 0,
            "configured actor could not redeem preserved control receipt");
        vm.RetireActor(tenantA, unconfigured);
        var a = Actor_Counter.Create(vm, tenantA, 7);
        var a2 = Actor_Counter.Create(vm, tenantA, 8);
        var b = Actor_Counter.Create(vm, tenantB, 7);
        bool missingTickDenied = false;
        try { _ = scheduler.DispatchNext(tenantA, a, Actor_Counter.ActorLayoutIdentity, Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { missingTickDenied = true; }
        Require(missingTickDenied, "scheduler admitted a turn without a mailbox tick");
        Require(Deliver(scheduler, tenantA, a, Actor_Counter.Call_increment) == 1, "A1");
        bool replayTickDenied = false;
        try { _ = scheduler.DispatchNext(tenantA, a, Actor_Counter.ActorLayoutIdentity, Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { replayTickDenied = true; }
        Require(replayTickDenied, "one mailbox tick admitted multiple turns");
        Require(Deliver(scheduler, tenantA, a, Actor_Counter.Call_increment) == 2, "A2");
        Require(Deliver(scheduler, tenantB, b, Actor_Counter.Call_increment) == 1, "B1");
        Require(Deliver(scheduler, tenantA, a, Actor_Counter.Call_increment) == 3, "A3");
        Require(Deliver(scheduler, tenantB, b, Actor_Counter.Call_increment) == 2, "B2");
        Require(Deliver(scheduler, tenantA, a2, Actor_Counter.Call_increment) == 1,
            "same-tenant actor must be independent");

        var deniedReentry = false;
        _ = Deliver(scheduler, tenantA, a, scope => {
            try { _ = scheduler.DispatchNext(tenantA, a, Actor_Counter.ActorLayoutIdentity, Actor_Counter.Call_increment); }
            catch (InvalidOperationException) { deniedReentry = true; }
            return scope.ReadInt64(0);
        });
        Require(deniedReentry, "actor reentered inside its own state loan");
        bool foreignThreadDenied = false;
        _ = Deliver(scheduler, tenantA, a, scope => {
            foreignThreadDenied = Task.Run(() => {
                try { _ = scope.AddInt64(0, 1); return false; }
                catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            return scope.ReadInt64(0);
        });
        Require(foreignThreadDenied, "scope crossed a foreign physical thread");
        OresVmHost.ActorDispatchScope? failedScope = null;
        try {
            _ = Deliver(scheduler, tenantA, a, scope => {
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
        Require(Deliver(scheduler, tenantA, a, Actor_Counter.Call_increment) == 4,
            "faulting actor turn was not cancelled");

        OresVmHost.ActorDispatchScope? escaped = null;
        _ = Deliver(scheduler, tenantA, a, scope => {
            escaped = scope;
            return scope.ReadInt64(0);
        });
        bool replayDenied = false;
        try { _ = escaped!.AddInt64(0, 1); }
        catch (InvalidOperationException) { replayDenied = true; }
        Require(replayDenied, "retired dispatch scope could mutate after completion");

        // Two concurrent carriers must not redeem one FIFO control tick
        // twice. The loser either sees the active logical lane or no tick.
        scheduler.Enqueue(tenantA, a);
        long TryCompetingDispatch()
        {
            try { return scheduler.DispatchNext(tenantA, a, Actor_Counter.ActorLayoutIdentity,
                Actor_Counter.Call_increment); }
            catch (InvalidOperationException) { return -1L; }
        }
        var competing = Task.WhenAll(
            Task.Run(TryCompetingDispatch),
            Task.Run(TryCompetingDispatch)).GetAwaiter().GetResult();
        Require(competing.Count(x => x == 5L) == 1 &&
                competing.Count(x => x == -1L) == 1 &&
                scheduler.PendingMessages(tenantA, a) == 0,
            "one mailbox tick admitted two competing actor turns");

        // The specialized dispatcher cannot steal arbitrary user bytes
        // from the same mailbox or skip past a nonempty FIFO head.
        vm.SendActor(tenantA, a2, scheduler.Address(tenantA, a2),
            new byte[] { 0xA5 });
        scheduler.Enqueue(tenantA, a2);
        Require(scheduler.PendingMessages(tenantA, a2) == 2,
            "wrong FIFO test setup");
        bool wrongHeadDenied = false;
        try { _ = scheduler.DispatchNext(tenantA, a2, Actor_Counter.ActorLayoutIdentity,
            Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { wrongHeadDenied = true; }
        Require(wrongHeadDenied &&
                scheduler.PendingMessages(tenantA, a2) == 2,
            "nonempty FIFO message was consumed or bypassed");
        var userBytes = scheduler.DequeueUser(tenantA, a2);
        Require(userBytes is { Length: 1 } && userBytes[0] == 0xA5 &&
                scheduler.PendingMessages(tenantA, a2) == 1,
            "trusted host failed to drain the FIFO-head user payload");
        Require(scheduler.DequeueUser(tenantA, a2) is null,
            "trusted user dequeue stole a scheduler control receipt");
        Require(scheduler.DispatchNext(tenantA, a2, Actor_Counter.ActorLayoutIdentity, Actor_Counter.Call_increment) == 2 &&
                scheduler.PendingMessages(tenantA, a2) == 0,
            "draining user payload failed to unblock the original control tick");

        // An ordinary zero-byte user message must never masquerade as
        // a scheduler-issued control envelope. It also blocks later ticks.
        vm.SendActor(tenantB, b, scheduler.Address(tenantB, b),
            Array.Empty<byte>());
        scheduler.Enqueue(tenantB, b);
        Require(scheduler.PendingMessages(tenantB, b) == 2,
            "zero-byte user message test setup failed");
        bool emptyUserDenied = false;
        try { _ = scheduler.DispatchNext(tenantB, b, Actor_Counter.ActorLayoutIdentity,
            Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { emptyUserDenied = true; }
        Require(emptyUserDenied &&
                scheduler.PendingMessages(tenantB, b) == 2,
            "zero-byte user data forged dispatch or consumed queued messages");
        var zeroBytes = scheduler.DequeueUser(tenantB, b);
        Require(zeroBytes is { Length: 0 } &&
                scheduler.PendingMessages(tenantB, b) == 1,
            "zero-byte user payload was lost or misclassified");
        Require(scheduler.DequeueUser(tenantB, b) is null,
            "trusted user dequeue consumed a typed control receipt");
        Require(scheduler.DispatchNext(tenantB, b, Actor_Counter.ActorLayoutIdentity, Actor_Counter.Call_increment) == 3 &&
                scheduler.PendingMessages(tenantB, b) == 0,
            "zero-byte user drain did not restore FIFO control progress");
        // Interleaved FIFO user/control/user/control must never skip data.
        vm.SendActor(tenantA, a2, scheduler.Address(tenantA, a2),
            new byte[] { 0x61, 0x62 });
        scheduler.Enqueue(tenantA, a2);
        vm.SendActor(tenantA, a2, scheduler.Address(tenantA, a2),
            new byte[] { 0x63 });
        scheduler.Enqueue(tenantA, a2);
        Require(scheduler.PendingMessages(tenantA, a2) == 4,
            "interleaved FIFO setup lost an envelope");
        bool firstHeadBlocked = false;
        try { _ = scheduler.DispatchNext(tenantA, a2, Actor_Counter.ActorLayoutIdentity, Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { firstHeadBlocked = true; }
        Require(firstHeadBlocked && scheduler.PendingMessages(tenantA, a2) == 4,
            "interleaved control skipped first user payload");
        var first = scheduler.DequeueUser(tenantA, a2);
        Require(first is { Length: 2 } && first[0] == 0x61 &&
                first[1] == 0x62 && scheduler.PendingMessages(tenantA, a2) == 3,
            "first user payload lost, changed or reordered");
        Require(scheduler.DequeueUser(tenantA, a2) is null,
            "user drain stole interleaved first control");
        Require(scheduler.DispatchNext(tenantA, a2, Actor_Counter.ActorLayoutIdentity,
                Actor_Counter.Call_increment) == 3 &&
                scheduler.PendingMessages(tenantA, a2) == 2,
            "interleaved first control lost or replayed");
        bool secondHeadBlocked = false;
        try { _ = scheduler.DispatchNext(tenantA, a2, Actor_Counter.ActorLayoutIdentity, Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { secondHeadBlocked = true; }
        Require(secondHeadBlocked && scheduler.PendingMessages(tenantA, a2) == 2,
            "interleaved control skipped second user payload");
        var second = scheduler.DequeueUser(tenantA, a2);
        Require(second is { Length: 1 } && second[0] == 0x63 &&
                scheduler.PendingMessages(tenantA, a2) == 1,
            "second user payload lost, changed or reordered");
        Require(scheduler.DispatchNext(tenantA, a2, Actor_Counter.ActorLayoutIdentity,
                Actor_Counter.Call_increment) == 4 &&
                scheduler.PendingMessages(tenantA, a2) == 0,
            "interleaved second control lost or replayed");
        bool exhaustedReceiptDenied = false;
        try { _ = scheduler.DispatchNext(tenantA, a2, Actor_Counter.ActorLayoutIdentity, Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { exhaustedReceiptDenied = true; }
        Require(exhaustedReceiptDenied, "interleaved FIFO admitted an unqueued turn");

        bool foreignDrainDenied = false;
        try { _ = scheduler.DequeueUser(tenantA, b); }
        catch (InvalidOperationException) { foreignDrainDenied = true; }
        Require(foreignDrainDenied, "trusted user drain crossed tenant ownership");
        bool activeDrainDenied = false;
        _ = Deliver(scheduler, tenantA, a, scope => {
            try { _ = scheduler.DequeueUser(tenantA, a); }
            catch (InvalidOperationException) { activeDrainDenied = true; }
            return scope.ReadInt64(0);
        });
        Require(activeDrainDenied, "trusted user drain reentered an active actor turn");

        vm.RetireActor(tenantA, a); vm.RetireActor(tenantA, a2);
        vm.RetireActor(tenantB, b);
        vm.BeginShutdown(tenantA); vm.CompleteShutdown(tenantA); vm.Destroy(tenantA);
        vm.BeginShutdown(tenantB); vm.CompleteShutdown(tenantB); vm.Destroy(tenantB);
        var replacement = vm.CreateIsolate("counter-a", maxActors: 1);
        vm.Start(replacement);
        var again = Actor_Counter.Create(vm, replacement, 7);
        Require(Deliver(scheduler, replacement, again, Actor_Counter.Call_increment) == 1,
            "reincarnated actor reused stale state");
        vm.RetireActor(replacement, again);
        vm.BeginShutdown(replacement); vm.CompleteShutdown(replacement);
        vm.Destroy(replacement);

        // A control tick arriving before layout installation must not be
        // lost. Dispatch rejects the unconfigured actor without dequeue,
        // then the original tick becomes eligible after trusted bootstrap.
        var rawTenant = vm.CreateIsolate("raw-counter", maxActors: 2);
        vm.Start(rawTenant);
        var raw = vm.RegisterActor(rawTenant, 13);
        scheduler.Enqueue(rawTenant, raw);
        Require(scheduler.PendingMessages(rawTenant, raw) == 1,
            "pre-layout control tick not admitted");
        bool layoutDenied = false;
        try { _ = scheduler.DispatchNext(rawTenant, raw, Actor_Counter.ActorLayoutIdentity,
            Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { layoutDenied = true; }
        Require(layoutDenied &&
                scheduler.PendingMessages(rawTenant, raw) == 1 &&
                !vm.ActorTurnSnapshot(rawTenant, raw).Active,
            "unconfigured actor consumed tick or started a logical turn");
        vm.ConfigureNamedActorState(rawTenant, raw, Actor_Counter.ActorName, Actor_Counter.ActorLayoutIdentity, new[] {
            new OresVmHost.ActorStateField(
                OresVmHost.ActorStateScalarKind.Int64)
        });
        Require(scheduler.DispatchNext(rawTenant, raw, Actor_Counter.ActorLayoutIdentity,
                Actor_Counter.Call_increment) == 1 &&
                scheduler.PendingMessages(rawTenant, raw) == 0,
            "configured actor could not redeem original pending tick");
        bool originalTickReplayed = false;
        try { _ = scheduler.DispatchNext(rawTenant, raw, Actor_Counter.ActorLayoutIdentity,
            Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { originalTickReplayed = true; }
        Require(originalTickReplayed, "pre-layout tick executed twice");
        // Default host actor mailboxes cap at 1024 queued envelopes.
        // Empty user messages consume slots despite zero byte accounting.
        // A typed control cannot bypass that quota or preempt user data.
        const int boundedMailboxMessages = 1024;
        for (var i = 0; i < boundedMailboxMessages; i++)
            vm.SendActor(rawTenant, raw, scheduler.Address(rawTenant, raw),
                Array.Empty<byte>());
        Require(scheduler.PendingMessages(rawTenant, raw) == boundedMailboxMessages,
            "zero-byte user sends escaped bounded mailbox message accounting");
        bool fullControlDenied = false;
        try { scheduler.Enqueue(rawTenant, raw); }
        catch (InvalidOperationException) { fullControlDenied = true; }
        Require(fullControlDenied &&
                scheduler.PendingMessages(rawTenant, raw) == boundedMailboxMessages,
            "full user mailbox admitted an extra privileged control");
        var refunded = scheduler.DequeueUser(rawTenant, raw);
        Require(refunded is { Length: 0 } &&
                scheduler.PendingMessages(rawTenant, raw) == boundedMailboxMessages - 1,
            "zero-byte user dequeue did not refund message quota");
        scheduler.Enqueue(rawTenant, raw);
        Require(scheduler.PendingMessages(rawTenant, raw) == boundedMailboxMessages,
            "control could not use exactly one refunded mailbox slot");
        bool saturatedHeadDenied = false;
        try { _ = scheduler.DispatchNext(rawTenant, raw, Actor_Counter.ActorLayoutIdentity, Actor_Counter.Call_increment); }
        catch (InvalidOperationException) { saturatedHeadDenied = true; }
        Require(saturatedHeadDenied &&
                scheduler.PendingMessages(rawTenant, raw) == boundedMailboxMessages,
            "control bypassed a saturated user-data FIFO head");
        for (var i = 1; i < boundedMailboxMessages; i++)
            Require(scheduler.DequeueUser(rawTenant, raw) is { Length: 0 },
                "saturated FIFO lost or reordered zero-byte user data");
        Require(scheduler.DequeueUser(rawTenant, raw) is null &&
                scheduler.PendingMessages(rawTenant, raw) == 1,
            "saturated FIFO user dequeue stole control receipt");
        Require(scheduler.DispatchNext(rawTenant, raw, Actor_Counter.ActorLayoutIdentity,
                Actor_Counter.Call_increment) == 2 &&
                scheduler.PendingMessages(rawTenant, raw) == 0,
            "saturated control ticket failed exactly-once recovery");

        var other = vm.RegisterActor(rawTenant, 14);
        vm.ConfigureNamedActorState(rawTenant, other, "OtherCounter", "OtherCounter|1:I", new[] {
            new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64)
        });
        scheduler.Enqueue(rawTenant, other);
        bool nominalMismatchDenied = false;
        try { _ = Actor_Counter.Dispatch_increment(scheduler, rawTenant, other); }
        catch (InvalidOperationException) { nominalMismatchDenied = true; }
        Require(nominalMismatchDenied &&
                scheduler.PendingMessages(rawTenant, other) == 1 &&
                !vm.ActorTurnSnapshot(rawTenant, other).Active,
            "same-shaped foreign actor consumed Counter control receipt");
        Require(scheduler.DispatchNext(rawTenant, other, "OtherCounter|1:I",
                    scope => scope.AddInt64(0, 1L)) == 1 &&
                scheduler.PendingMessages(rawTenant, other) == 0,
            "nominal mismatch did not preserve foreign actor receipt");
        vm.RetireActor(rawTenant, other);

        var wrongShape = vm.RegisterActor(rawTenant, 15);
        vm.ConfigureNamedActorState(rawTenant, wrongShape,
            Actor_Counter.ActorName, "Counter|1:B", new[] {
                new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Boolean)
            });
        scheduler.Enqueue(rawTenant, wrongShape);
        bool layoutShapeDenied = false;
        try { _ = Actor_Counter.Dispatch_increment(scheduler, rawTenant, wrongShape); }
        catch (InvalidOperationException) { layoutShapeDenied = true; }
        Require(layoutShapeDenied &&
                scheduler.PendingMessages(rawTenant, wrongShape) == 1 &&
                !vm.ActorTurnSnapshot(rawTenant, wrongShape).Active,
            "same-name wrong-shape actor consumed Counter control receipt");
        vm.RetireActor(rawTenant, wrongShape);
        vm.RetireActor(rawTenant, raw);
        vm.BeginShutdown(rawTenant); vm.CompleteShutdown(rawTenant);
        vm.Destroy(rawTenant);
    }
}
