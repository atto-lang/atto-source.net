using Ores.Net.Runtime;

internal static class VmIsolateHostTests
{
    private static void ExpectFailure(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("IsolateVM gate failed: " + label);
    }

    private static void CheckSupervisorTerminalReservations()
    {
        var host = new OresVmHost(maxTenants: 2, maxQueuedMailBytes: 1);
        var tenant = host.CreateIsolate("control", maxActors: 3, maxQueuedMailBytes: 1);
        var foreign = host.CreateIsolate("foreign-control", maxActors: 1);
        host.Start(tenant); host.Start(foreign);
        var parent = host.RegisterActor(tenant, 1);
        var unrelated = host.RegisterActor(tenant, 2);
        var foreignParent = host.RegisterActor(foreign, 1);
        ExpectFailure(() => host.RegisterSupervisedActor(tenant, foreignParent, 3),
            "foreign supervisor authority");
        ExpectFailure(() => host.RegisterSupervisedActor(tenant, parent, 2),
            "duplicate child must not leak a terminal reservation");
        var child = host.RegisterSupervisedActor(tenant, parent, 3);
        var wake = host.WaitForSupervisorTerminalAsync(tenant, parent);
        if (wake.IsCompleted || !ReferenceEquals(wake, host.WaitForSupervisorTerminalAsync(tenant, parent)))
            throw new Exception("pending terminal wakeup must be shared and incomplete");
        var unrelatedWake = host.WaitForSupervisorTerminalAsync(tenant, unrelated);
        ExpectFailure(() => host.WaitForSupervisorTerminalAsync(foreign, parent), "foreign readiness authority");

        // Saturate both DATA message count and byte budget, including empty
        // user messages. Terminal delivery must not consume a DATA slot.
        host.SendActor(tenant, parent, parent.CurrentRef, new byte[] { 42 });
        for (var i = 1; i < 1024; i++)
            host.SendActor(tenant, parent, parent.CurrentRef, Array.Empty<byte>());
        ExpectFailure(() => host.SendActor(tenant, parent, parent.CurrentRef,
            Array.Empty<byte>()), "full DATA count");
        ExpectFailure(() => host.RetireActor(tenant, parent), "live supervised child");
        if (host.TryTakeSupervisorTerminal(tenant, parent, out _))
            throw new Exception("terminal notification before retirement");

        var turn = host.BeginActorTurn(tenant, child);
        ExpectFailure(() => host.RetireFaultedActor(tenant, child), "live child turn");
        host.CompleteActorTurn(tenant, child, turn);
        var pin = host.PinResource(tenant, child);
        ExpectFailure(() => host.RetireFaultedActor(tenant, child), "live resource pin");
        if (host.TryTakeSupervisorTerminal(tenant, parent, out _))
            throw new Exception("failed retirement published a terminal");
        host.RetireResource(tenant, child, pin);
        host.RetireFaultedActor(tenant, child);
        if (!wake.IsCompletedSuccessfully || unrelatedWake.IsCompleted ||
            !host.WaitForSupervisorTerminalAsync(tenant, parent).IsCompletedSuccessfully)
            throw new Exception("terminal readiness not level-triggered for exact parent");
        ExpectFailure(() => host.RetireFaultedActor(tenant, child), "terminal replay");
        ExpectFailure(() => host.RetireActor(tenant, parent), "unredeemed child terminal");
        if (host.TryTakeSupervisorTerminal(tenant, unrelated, out _))
            throw new Exception("unrelated actor stole terminal notice");
        ExpectFailure(() => host.TryTakeSupervisorTerminal(foreign, parent, out _),
            "foreign tenant redemption");

        // Reuse the numeric ID without confusing old and new incarnations.
        var replacement = host.RegisterSupervisedActor(tenant, parent, 3);
        host.RetireActor(tenant, replacement);
        var third = host.RegisterSupervisedActor(tenant, parent, 3);
        host.RetireActor(tenant, third);
        ExpectFailure(() => host.RegisterSupervisedActor(tenant, parent, 3),
            "unredeemed terminal quota must be bounded across ID reuse");
        var received = new Dictionary<ulong, OresVmHost.SupervisorTerminalKind>();
        while (host.TryTakeSupervisorTerminal(tenant, parent, out var notice)) {
            if (notice.ChildId != 3 || !received.TryAdd(notice.ChildEpoch, notice.Outcome))
                throw new Exception("invalid or duplicate terminal identity");
        }
        if (received.Count != 3 ||
            received[child.Epoch] != OresVmHost.SupervisorTerminalKind.Faulted ||
            received[replacement.Epoch] != OresVmHost.SupervisorTerminalKind.Stopped ||
            received[third.Epoch] != OresVmHost.SupervisorTerminalKind.Stopped)
            throw new Exception("missing or misrouted terminal");
        if (parent.QueuedMessages != 1024 || host.QueuedMail(tenant).Tenant != 1)
            throw new Exception("terminal delivery changed DATA accounting");

        var nextWake = host.WaitForSupervisorTerminalAsync(tenant, parent);
        if (nextWake.IsCompleted || ReferenceEquals(wake, nextWake))
            throw new Exception("drained terminal readiness did not rearm");
        var last = host.RegisterSupervisedActor(tenant, parent, 3);
        host.BeginShutdown(tenant);
        host.RetireActor(tenant, last);
        if (!nextWake.IsCompletedSuccessfully)
            throw new Exception("terminal wakeup lost during quiescence");
        if (!host.TryTakeSupervisorTerminal(tenant, parent, out var finalNotice) ||
            finalNotice.ChildEpoch != last.Epoch ||
            host.TryTakeSupervisorTerminal(tenant, parent, out _))
            throw new Exception("quiescent terminal drain or replay failed");
        if (!host.TryReceiveActor(tenant, parent, out var payload) ||
            payload is null || payload.Length != 1 || payload[0] != 42)
            throw new Exception("DATA head corrupted by terminal delivery");
        var finalWake = host.WaitForSupervisorTerminalAsync(tenant, parent);
        host.RetireActor(tenant, unrelated);
        host.RetireActor(tenant, parent);
        if (!unrelatedWake.IsCanceled || !finalWake.IsCanceled)
            throw new Exception("retired parent left an unresolved readiness task");
        ExpectFailure(() => host.WaitForSupervisorTerminalAsync(tenant, parent), "stale readiness parent");
        host.CompleteShutdown(tenant); host.Destroy(tenant);
        host.RetireActor(foreign, foreignParent);
        host.BeginShutdown(foreign); host.CompleteShutdown(foreign); host.Destroy(foreign);
        Console.WriteLine("NET_SUPERVISOR_TERMINAL_RESERVATION_HOST_TESTS_PASS");
    }

    private static void CheckNestedStateAuthority()
    {
        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate("nested-authority");
        host.Start(tenant);
        var actor = host.RegisterActor(tenant, 1);
        var turn = host.BeginActorTurn(tenant, actor);
        var read = host.BeginActorStateLoan(tenant, actor, turn);
        ExpectFailure(() => host.BeginActorStateLoan(tenant, actor, turn, writable: true),
            "read-only parent cannot mint writable child");
        host.ValidateActorStateLoan(tenant, actor, turn, read);
        var nested = host.BeginActorStateLoan(tenant, actor, turn);
        if (nested.Generation != read.Generation + 1)
            throw new Exception("denied write reborrow consumed generation");
        host.EndActorStateLoan(tenant, actor, turn, nested);
        host.EndActorStateLoan(tenant, actor, turn, read);
        host.CompleteActorTurn(tenant, actor, turn);
        host.RetireActor(tenant, actor);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
        Console.WriteLine("NET_NESTED_STATE_AUTHORITY_CURRENT_MAIN_TEST_PASS");
    }

    private static void CheckCancellationRetirement()
    {
        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate("cancel-retirement", maxActors: 2);
        host.Start(tenant);
        var parent = host.RegisterActor(tenant, 1);
        var child = host.RegisterSupervisedActor(tenant, parent, 2);
        var turn = host.BeginActorTurn(tenant, child);
        var outer = host.BeginActorStateLoan(tenant, child, turn, writable: true);
        var inner = host.BeginActorStateLoan(tenant, child, turn);
        ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, child, turn),
            "ack without request");
        var signal = host.WaitForSupervisorTerminalAsync(tenant, parent);
        // Request from another host thread, while the execution owner still holds
        // nested state authority. Joining this host request is not guest retirement.
        Task.Run(() => host.CancelActorTurn(tenant, child, turn)).GetAwaiter().GetResult();
        host.CancelActorTurn(tenant, child, turn); // idempotent while live
        ExpectFailure(() => host.BeginActorTurn(tenant, child), "cancelled lane still occupied");
        ExpectFailure(() => host.RetireFaultedActor(tenant, child), "retirement before owner acknowledgment");
        ExpectFailure(() => host.BeginActorStateLoan(tenant, child, turn), "new state loan after cancel");
        ExpectFailure(() => host.ValidateActorStateLoan(tenant, child, turn, inner), "state access after cancel");
        ExpectFailure(() => host.CompleteActorTurn(tenant, child, turn), "cancel cannot complete successfully");
        ExpectFailure(() => host.EndActorStateLoan(tenant, child, turn, outer), "cleanup remains LIFO");
        ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, parent, turn), "foreign ack");
        if (signal.IsCompleted) throw new Exception("cancel request published terminal notice");
        ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, child, turn), "ack with two live loans");
        host.EndActorStateLoan(tenant, child, turn, inner);
        ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, child, turn), "ack with outer loan still live");
        host.EndActorStateLoan(tenant, child, turn, outer);
        host.AcknowledgeActorTurnCancellation(tenant, child, turn);
        ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, child, turn), "duplicate ack");
        var next = host.BeginActorTurn(tenant, child);
        if (next.Generation <= turn.Generation) throw new Exception("turn generation reused");
        host.SuspendActorTurn(tenant, child, next);
        host.CancelActorTurn(tenant, child, next);
        ExpectFailure(() => host.ResumeActorTurn(tenant, child, next), "cancelled suspended turn resume");
        ExpectFailure(() => host.RetireActor(tenant, child), "cancelled suspended turn before ack");
        host.AcknowledgeActorTurnCancellation(tenant, child, next);
        host.RetireFaultedActor(tenant, child);
        if (!signal.IsCompletedSuccessfully ||
            !host.TryTakeSupervisorTerminal(tenant, parent, out var notice) ||
            notice.ChildEpoch != child.Epoch)
            throw new Exception("terminal not published after cancellation retirement");
        host.RetireActor(tenant, parent);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
        Console.WriteLine("NET_ACTOR_CANCEL_RETIREMENT_HOST_TESTS_PASS");
    }

    private static void CheckScheduledCarrierRetirement()
    {
        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate("scheduled-carrier", maxActors: 2);
        host.Start(tenant);
        var parent = host.RegisterActor(tenant, 1);
        var child = host.RegisterSupervisedActor(tenant, parent, 2);
        host.ConfigureNamedActorState(tenant, child, "Counter", "Counter|1:I",
            new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64) });
        var scheduler = host.CreateTrustedScheduler();
        var signal = scheduler.WaitForSupervisorTerminal(tenant, parent);
        scheduler.Enqueue(tenant, child);
        OresVmHost.ActorTurnTicket? captured = null;
        OresVmHost.ActorDispatchScope? staleScope = null;
        ExpectFailure(() => scheduler.DispatchNext(tenant, child, "Counter|1:I", scope => {
            captured = child.ActiveTurn ?? throw new Exception("missing active turn");
            var turn = captured;
            staleScope = scope;
            var loan = turn.ActiveStateLoan ?? throw new Exception("missing dispatch loan");
            // A separate physical host thread is not the owner carrier.
            Task.Factory.StartNew(() => {
                ExpectFailure(() => host.ValidateActorStateLoan(tenant, child, turn, loan),
                    "foreign carrier state access");
                ExpectFailure(() => host.EndActorStateLoan(tenant, child, turn, loan),
                    "foreign carrier cleanup");
                host.CancelActorTurn(tenant, child, turn);
                ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, child, turn),
                    "foreign early cancellation acknowledgment");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .GetAwaiter().GetResult();
            ExpectFailure(() => scope.ReadInt64(0), "cancelled generated scope access");
            host.EndActorStateLoan(tenant, child, turn, loan);
            // Even with no outstanding state loan, the delegate is still on
            // the carrier. The exact private scheduler lease must remain live.
            ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, child, turn),
                "zero loans are not physical retirement");
            ExpectFailure(() => host.CompleteActorTurn(tenant, child, turn),
                "complete while scheduled carrier live");
            ExpectFailure(() => host.RetireFaultedActor(tenant, child),
                "terminal before physical delegate unwind");
            if (signal.IsCompleted) throw new Exception("premature supervisor terminal");
            throw new InvalidOperationException("fixture cancellation");
        }), "cancelled scheduled dispatch");
        if (captured is null || staleScope is null || host.ActorTurnSnapshot(tenant, child).Active)
            throw new Exception("scheduled cancellation did not retire after unwind");
        ExpectFailure(() => staleScope.ReadInt64(0), "stale generated scope");
        ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, child, captured),
            "retired scheduled ticket replay");
        scheduler.Enqueue(tenant, child);
        if (scheduler.DispatchNext(tenant, child, "Counter|1:I", scope => scope.AddInt64(0, 42)) != 42)
            throw new Exception("scheduled lane did not recover");
        host.RetireFaultedActor(tenant, child);
        if (!signal.IsCompletedSuccessfully ||
            !scheduler.TryDequeueSupervisorTerminal(tenant, parent, out var notice) ||
            notice.ChildEpoch != child.Epoch)
            throw new Exception("retired child missing scheduled terminal");
        host.RetireActor(tenant, parent);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
        Console.WriteLine("NET_SCHEDULED_CARRIER_RETIREMENT_HOST_TESTS_PASS");
    }

    private static void CheckSupervisorDispatchPriority()
    {
        var host = new OresVmHost(maxTenants: 1, maxQueuedMailBytes: 1);
        var tenant = host.CreateIsolate("control-priority", maxActors: 70, maxQueuedMailBytes: 1);
        host.Start(tenant);
        var parent = host.RegisterActor(tenant, 1);
        host.ConfigureNamedActorState(tenant, parent, "Counter", "Counter|1:I",
            new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64) });
        var scheduler = host.CreateTrustedScheduler();
        host.SendActor(tenant, parent, parent.CurrentRef, new byte[] { 42 });
        scheduler.Enqueue(tenant, parent);
        for (var i = 0; i < OresVmHost.SupervisorControlBurstLimit + 2; i++) {
            var child = host.RegisterSupervisedActor(tenant, parent, 2);
            host.RetireActor(tenant, child);
        }
        var live = host.BeginActorTurn(tenant, parent);
        ExpectFailure(() => scheduler.TryDequeueSupervisorTerminal(tenant, parent, out _),
            "CONTROL selection during active turn");
        host.CompleteActorTurn(tenant, parent, live);
        ExpectFailure(() => scheduler.DequeueUser(tenant, parent), "CONTROL before DATA");
        var seen = new HashSet<ulong>();
        for (var i = 0; i < OresVmHost.SupervisorControlBurstLimit; i++) {
            if (!scheduler.TryDequeueSupervisorTerminal(tenant, parent, out var notice) ||
                !seen.Add(notice.ChildEpoch))
                throw new Exception("missing or replayed priority CONTROL");
        }
        if (scheduler.TryDequeueSupervisorTerminal(tenant, parent, out _) ||
            parent.QueuedMessages != 2)
            throw new Exception("ordinary FIFO did not receive its bounded opportunity");
        var data = scheduler.DequeueUser(tenant, parent);
        if (data is null || data.Length != 1 || data[0] != 42 ||
            host.QueuedMail(tenant).Tenant != 0)
            throw new Exception("ordinary DATA or accounting changed");
        ExpectFailure(() => scheduler.DispatchNext(tenant, parent, "Counter|1:I", scope => scope.ReadInt64(0)),
            "CONTROL before internal FIFO receipt");
        while (scheduler.TryDequeueSupervisorTerminal(tenant, parent, out var notice))
            if (!seen.Add(notice.ChildEpoch)) throw new Exception("terminal replay");
        if (seen.Count != OresVmHost.SupervisorControlBurstLimit + 2 ||
            scheduler.DispatchNext(tenant, parent, "Counter|1:I", scope => scope.AddInt64(0, 42)) != 42)
            throw new Exception("CONTROL drain or internal FIFO receipt failed");
        host.RetireActor(tenant, parent);
        // An internal receipt without a configured dispatch layout cannot
        // impose the ordinary FIFO opportunity and starve terminal CONTROL.
        var unconfigured = host.RegisterActor(tenant, 1);
        scheduler.Enqueue(tenant, unconfigured);
        for (var i = 0; i < OresVmHost.SupervisorControlBurstLimit + 1; i++) {
            var child = host.RegisterSupervisedActor(tenant, unconfigured, 2);
            host.RetireActor(tenant, child);
        }
        for (var i = 0; i < OresVmHost.SupervisorControlBurstLimit + 1; i++)
            if (!scheduler.TryDequeueSupervisorTerminal(tenant, unconfigured, out _))
                throw new Exception("unrunnable FIFO receipt starved terminal CONTROL");
        if (unconfigured.QueuedMessages != 1)
            throw new Exception("unconfigured receipt was consumed");
        host.ConfigureNamedActorState(tenant, unconfigured, "Counter", "Counter|1:I",
            new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64) });
        if (scheduler.DispatchNext(tenant, unconfigured, "Counter|1:I", scope => scope.AddInt64(0, 42)) != 42)
            throw new Exception("configured FIFO receipt did not recover");
        host.RetireActor(tenant, unconfigured);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
        Console.WriteLine("NET_SUPERVISOR_DISPATCH_PRIORITY_HOST_TESTS_PASS");
    }

    private static void CheckMigratableCarrierSegments()
    {
        var host = new OresVmHost(maxTenants: 1);
        var tenant = host.CreateIsolate("migratable-carrier", maxActors: 2);
        host.Start(tenant);
        var actor = host.RegisterActor(tenant, 1, serial: true);
        var other = host.RegisterActor(tenant, 2);
        host.ConfigureNamedActorState(tenant, actor, "Counter", "Counter|1:I",
            new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64) });
        var scheduler = host.CreateTrustedScheduler();
        var foreignScheduler = host.CreateTrustedScheduler();
        scheduler.Enqueue(tenant, actor);
        int firstThread = Environment.CurrentManagedThreadId;
        OresVmHost.ActorDispatchScope? oldScope = null;
        var first = scheduler.StartSegment(tenant, actor, "Counter|1:I", scope => {
            oldScope = scope;
            if (scope.AddInt64(0, 20) != 20) throw new Exception("first segment state");
            return true;
        }) ?? throw new Exception("missing initial continuation");
        var turn = first.Turn;
        if (!host.ActorTurnSnapshot(tenant, actor).Suspended ||
            turn.ScheduledCarrierLease is not null)
            throw new Exception("suspended segment retained its carrier");
        ExpectFailure(() => oldScope!.ReadInt64(0), "retired scope access");
        ExpectFailure(() => host.ResumeActorTurn(tenant, actor, turn), "raw resume bypass");
        ExpectFailure(() => host.CompleteActorTurn(tenant, actor, turn), "raw completion bypass");
        ExpectFailure(() => host.BeginActorTurn(tenant, actor), "serial logical lane retained");
        ExpectFailure(() => foreignScheduler.ResumeSegment(tenant, actor, first, _ => false),
            "foreign scheduler continuation");
        ExpectFailure(() => scheduler.ResumeSegment(tenant, other, first, _ => false),
            "foreign actor continuation");
        var forged = new OresVmHost.ActorContinuation(scheduler, turn);
        ExpectFailure(() => scheduler.ResumeSegment(tenant, actor, forged, _ => false),
            "forged continuation identity");

        var second = Task.Factory.StartNew(() =>
            scheduler.ResumeSegment(tenant, actor, first, scope => {
                if (Environment.CurrentManagedThreadId == firstThread)
                    throw new Exception("fixture did not migrate carrier");
                ExpectFailure(() => oldScope!.ReadInt64(0), "old scope after migration");
                ExpectFailure(() => scheduler.ResumeSegment(tenant, actor, first, _ => false),
                    "reentrant ticket replay");
                if (scope.AddInt64(0, 22) != 42) throw new Exception("migrated state");
                return true;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .GetAwaiter().GetResult() ?? throw new Exception("missing second continuation");
        if (ReferenceEquals(first, second) || !ReferenceEquals(turn, second.Turn))
            throw new Exception("continuation freshness or logical identity");
        ExpectFailure(() => scheduler.ResumeSegment(tenant, actor, first, _ => false),
            "old continuation after resuspension");
        ExpectFailure(() => scheduler.DiscardContinuation(tenant, actor, second),
            "discard without cancellation");
        host.CancelActorTurn(tenant, actor, turn);
        ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, actor, turn),
            "raw cancel acknowledgment bypass");
        ExpectFailure(() => scheduler.ResumeSegment(tenant, actor, second, _ => false),
            "cancelled continuation resume");
        ExpectFailure(() => scheduler.DiscardContinuation(tenant, actor, first),
            "old continuation discard");
        scheduler.DiscardContinuation(tenant, actor, second);
        ExpectFailure(() => scheduler.DiscardContinuation(tenant, actor, second),
            "duplicate discard");

        scheduler.Enqueue(tenant, actor);
        var completed = scheduler.StartSegment(tenant, actor, "Counter|1:I", scope => {
            if (scope.ReadInt64(0) != 42) throw new Exception("state after cancellation");
            return false;
        });
        if (completed is not null || host.ActorTurnSnapshot(tenant, actor).Active)
            throw new Exception("completed segment retained authority");

        scheduler.Enqueue(tenant, actor);
        bool cancelled = false;
        try {
            scheduler.StartSegment(tenant, actor, "Counter|1:I", scope => {
                var current = actor.ActiveTurn ?? throw new Exception("no running segment");
                host.CancelActorTurn(tenant, actor, current);
                ExpectFailure(() => host.AcknowledgeActorTurnCancellation(tenant, actor, current),
                    "running segment cancel acknowledgment");
                return true;
            });
        } catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled || host.ActorTurnSnapshot(tenant, actor).Active)
            throw new Exception("cancelled segment returned a false success or retained lane");

        host.RetireActor(tenant, actor);
        host.RetireActor(tenant, other);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
        Console.WriteLine("NET_MIGRATABLE_CARRIER_SEGMENTS_HOST_TESTS_PASS");
    }

    private static void CheckSupervisorHandlerDispatch()
    {
        var host = new OresVmHost(maxTenants: 1, maxQueuedMailBytes: 1);
        var tenant = host.CreateIsolate("supervisor-handler", maxActors: 3, maxQueuedMailBytes: 1);
        host.Start(tenant);
        var parent = host.RegisterActor(tenant, 1);
        host.ConfigureNamedActorState(tenant, parent, "Counter", "Counter|1:I",
            new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64) });
        var scheduler = host.CreateTrustedScheduler();
        host.SendActor(tenant, parent, parent.CurrentRef, new byte[] { 42 });
        for (var i = 1; i < 1024; i++)
            host.SendActor(tenant, parent, parent.CurrentRef, Array.Empty<byte>());
        var first = host.RegisterSupervisedActor(tenant, parent, 2);
        var second = host.RegisterSupervisedActor(tenant, parent, 3);
        var ready = scheduler.WaitForSupervisorTerminal(tenant, parent);
        host.RetireFaultedActor(tenant, first);
        host.RetireActor(tenant, second);
        if (!ready.IsCompletedSuccessfully) throw new Exception("handler lost readiness");
        ExpectFailure(() => scheduler.TryDispatchSupervisorTerminal(tenant, parent, "Wrong|1:I",
            (_, _) => throw new Exception("wrong-layout handler entered")), "layout admission");
        var seen = new HashSet<ulong>();
        OresVmHost.ActorDispatchScope? stale = null;
        if (!scheduler.TryDispatchSupervisorTerminal(tenant, parent, "Counter|1:I", (scope, notice) => {
            if (!seen.Add(notice.ChildEpoch)) throw new Exception("duplicate notice");
            stale = scope;
            ExpectFailure(() => scheduler.TryDispatchSupervisorTerminal(tenant, parent, "Counter|1:I",
                (_, _) => throw new Exception("nested handler entered")), "nested handler admission");
            scope.AddInt64(0, 20);
        })) throw new Exception("first supervisor handler did not run");
        ExpectFailure(() => stale!.ReadInt64(0), "retired supervisor handler scope");
        ExpectFailure(() => scheduler.TryDispatchSupervisorTerminal(tenant, parent, "Counter|1:I", (scope, notice) => {
            if (!seen.Add(notice.ChildEpoch)) throw new Exception("duplicate notice");
            scope.AddInt64(0, 22);
            throw new InvalidOperationException("fixture handler fault");
        }), "faulting supervisor handler");
        if (seen.Count != 2 || !seen.Contains(first.Epoch) || !seen.Contains(second.Epoch) ||
            host.ActorTurnSnapshot(tenant, parent).Active ||
            scheduler.TryDispatchSupervisorTerminal(tenant, parent, "Counter|1:I",
                (_, _) => throw new Exception("terminal replay")))
            throw new Exception("supervisor handler retirement or exact-once selection");
        if (parent.QueuedMessages != 1024 || host.QueuedMail(tenant).Tenant != 1)
            throw new Exception("supervisor handler changed DATA quota");
        var data = scheduler.DequeueUser(tenant, parent);
        if (data is null || data.Length != 1 || data[0] != 42)
            throw new Exception("supervisor handler changed DATA head");
        var turn = host.BeginActorTurn(tenant, parent);
        var loan = host.BeginActorStateLoan(tenant, parent, turn);
        if (host.ReadActorStateInt64(tenant, parent, turn, loan, 0) != 42)
            throw new Exception("supervisor handler state was not committed");
        host.EndActorStateLoan(tenant, parent, turn, loan);
        host.CompleteActorTurn(tenant, parent, turn);
        host.RetireActor(tenant, parent);
        host.BeginShutdown(tenant); host.CompleteShutdown(tenant); host.Destroy(tenant);
        Console.WriteLine("NET_SUPERVISOR_HANDLER_DISPATCH_HOST_TESTS_PASS");
    }

    internal static void RunSupervisorTerminalChecks()
    {
        CheckSupervisorTerminalReservations();
        CheckNestedStateAuthority();
        CheckCancellationRetirement();
        CheckScheduledCarrierRetirement();
        CheckSupervisorDispatchPriority();
        CheckMigratableCarrierSegments();
        CheckSupervisorHandlerDispatch();
    }

    public static void Run()
    {
        RunSupervisorTerminalChecks();
        var host = new OresVmHost(maxTenants: 2);
        // Fail closed: the native TenantVm isolate is not wired in yet, and a
        // host dictionary is not an isolate, so untrusted admission must reject.
        ExpectFailure(() => host.CreateIsolate("hostile",
            isolation: OresVmHost.IsolationMode.NativeVmIsolate),
            "unwired native VM isolate must reject before admission");
        // Same exact ID grammar as the native Wasmtime tenant host:
        // no dot components, slashes, path escapes or Unicode lookalikes.
        // Reject before allocating a tenant or consuming its capacity.
        foreach (var invalidId in new[] {
            ".", "..", "../escape", "a/b", @"a\b", "a b", "é",
            "", " ", new string('x', 129)
        })
        {
            try {
                host.CreateIsolate(invalidId);
                throw new Exception("invalid tenant ID was admitted: " + invalidId);
            }
            catch (ArgumentException) { }
        }
        var validTenantLabel = host.CreateIsolate("tenant.a-b_1");
        host.Start(validTenantLabel);
        host.BeginShutdown(validTenantLabel);
        host.CompleteShutdown(validTenantLabel);
        host.Destroy(validTenantLabel);
        var a = host.CreateIsolate("tenant-a", maxActors: 1, maxResourcePins: 1);
        var b = host.CreateIsolate("tenant-b", maxActors: 1);
        ExpectFailure(() => host.CreateIsolate("tenant-c"), "host limit");
        ExpectFailure(() => host.CreateIsolate("tenant-a"), "duplicate tenant");
        host.Start(a); host.Start(b);
        // Two tenants execute the same statically typed entry but keep their
        // host-owned scalar state independent. This is NOT generated .ores
        // actor state; the production Native AOT host tests real emitted CIL.
        if (host.InvokeGeneratedScalar(a, () => -1) != -1 ||
            host.InvokeGeneratedScalar(a, () => -2) != -3 ||
            host.InvokeGeneratedScalar(b, () => 100) != 100 ||
            host.GuestSnapshot(a).Scalar != -3 ||
            host.GuestSnapshot(b).Scalar != 100)
            throw new Exception("tenant state isolation failed");
        // An exception from a guest delegate is distinct from a typed
        // numeric overflow in the trusted host commit. Quarantine on guest
        // exceptions and require a new incarnation before subsequent calls.
        var faultHost = new OresVmHost(maxTenants: 1);
        var fault = faultHost.CreateIsolate("fault");
        faultHost.Start(fault);
        if (faultHost.InvokeGeneratedScalar(fault, () => 7) != 7)
            throw new Exception("fault fixture setup");
        ExpectFailure(() => faultHost.InvokeGeneratedScalar(fault, () =>
            faultHost.InvokeGeneratedScalar(fault, () => 1)), "nested guest entry");
        if (faultHost.Snapshot(fault).Phase != OresVmHost.Phase.Quiescing ||
            faultHost.GuestSnapshot(fault).GuestCalls != 0)
            throw new Exception("throwing tenant was not quarantined/drained");
        ExpectFailure(() => faultHost.InvokeGeneratedScalar(fault, () => 1),
            "quarantined tenant must refuse new entry");
        faultHost.CompleteShutdown(fault);
        faultHost.Destroy(fault);
        var recovered = faultHost.CreateIsolate("fault");
        faultHost.Start(recovered);
        if (faultHost.InvokeGeneratedScalar(recovered, () => 1) != 1)
            throw new Exception("fault restart reused tenant scalar state");
        ExpectFailure(() => faultHost.Snapshot(fault), "stale fault handle");
        try { faultHost.InvokeGeneratedScalar(recovered, () => throw new OverflowException()); }
        catch (OverflowException) { }
        // The failed call never commits, and the faulted incarnation's state
        // is discarded (tenant isolation v2), so it reads back as 0.
        if (faultHost.Snapshot(recovered).Phase != OresVmHost.Phase.Quiescing ||
            faultHost.GuestSnapshot(recovered).Scalar != 0 ||
            faultHost.GuestSnapshot(recovered).GuestCalls != 0)
            throw new Exception("guest fault leaked a pin or mutated state");
        faultHost.CompleteShutdown(recovered);
        faultHost.Destroy(recovered);
        if (host.GuestSnapshot(a).Scalar != -3)
            throw new Exception("foreign tenant fault changed tenant-a");

        // Two OresVM hosts may mint the same tenant label and numeric
        // generation. Handle object identity, not these public fields, is
        // the trusted native authority. A foreign host cannot turn, inspect,
        // retire, or influence a tenant belonging to this host.
        var foreignHost = new OresVmHost(maxTenants: 1);
        // Reuse generation 2 in a DIFFERENT host to prove object identity
        // still denies the collision, not merely a numeric epoch mismatch.
        var primer = foreignHost.CreateIsolate("primer");
        foreignHost.Start(primer);
        foreignHost.BeginShutdown(primer);
        foreignHost.CompleteShutdown(primer);
        foreignHost.Destroy(primer);
        var foreignA = foreignHost.CreateIsolate("tenant-a");
        foreignHost.Start(foreignA);
        if (foreignA.Generation != a.Generation)
            throw new Exception("foreign host identity collision fixture unavailable");
        ExpectFailure(() => host.Snapshot(foreignA), "foreign host snapshot");
        ExpectFailure(() => host.GuestSnapshot(foreignA), "foreign host guest state");
        ExpectFailure(() => host.InvokeGeneratedScalar(foreignA, () => 77),
            "foreign host native guest");
        if (host.GuestSnapshot(a).Scalar != -3 ||
            foreignHost.GuestSnapshot(foreignA).Scalar != 0)
            throw new Exception("foreign authority mutated another isolate");
        foreignHost.BeginShutdown(foreignA);
        foreignHost.CompleteShutdown(foreignA);
        foreignHost.Destroy(foreignA);

        // Quota/phase failures must be non-consuming. No valid actor or pin
        // can be minted by a foreign, stale or quiescing incarnation.
        foreach (int invalid in new[] { 0, -1, 65537 })
        {
            try {
                host.CreateIsolate("never-admitted", maxActors: invalid);
                throw new Exception("invalid actor quota admitted: " + invalid);
            } catch (ArgumentOutOfRangeException) { }
            try {
                host.CreateIsolate("never-admitted", maxResourcePins: invalid);
                throw new Exception("invalid pin quota admitted: " + invalid);
            } catch (ArgumentOutOfRangeException) { }
        }
        ExpectFailure(() => host.CreateIsolate("never-admitted"), "full host after quota negatives");
        var actorA = host.RegisterActor(a, 3);
        var actorB = host.RegisterActor(b, 3);
        ExpectFailure(() => host.RegisterActor(a, 3), "duplicate actor incarnation");
        ExpectFailure(() => host.PinResource(a, actorB), "foreign actor pin");
        var refA = actorA.CurrentRef;
        host.SendActor(a, actorA, refA, new byte[] { 1, 2, 3 });
        if (actorA.QueuedMessages != 1)
            throw new Exception("actor mailbox must be real, not a fake ticket");
        var pin = host.PinResource(a, actorA);
        ExpectFailure(() => host.PinResource(a, actorA), "pin quota");
        ExpectFailure(() => host.RetireResource(b, actorB, pin), "foreign tenant pin");
        ExpectFailure(() => host.RetireActor(a, actorA), "actor lifetime pin");
        host.BeginShutdown(a);
        ExpectFailure(() => host.InvokeGeneratedScalar(a, () => 1),
            "guest admission must close at quiescence");
        ExpectFailure(() => host.PinResource(a, actorA), "quiescent admission");
        ExpectFailure(() => host.SendActor(a, actorA, refA, new byte[] { 9 }),
            "quiescent mailbox admission");
        ExpectFailure(() => host.CompleteShutdown(a), "drain outstanding actor");
        host.RetireResource(a, actorA, pin);
        ExpectFailure(() => host.RetireResource(a, actorA, pin), "replay ticket");
        host.RetireActor(a, actorA);
        if (actorA.QueuedMessages != 0)
            throw new Exception("retire must drain pending mailbox-owned copies");
        ExpectFailure(() => host.SendActor(a, actorA, refA, new byte[] { 4 }), "stale actor ref");
        host.CompleteShutdown(a); host.Destroy(a);
        ExpectFailure(() => host.Snapshot(a), "stale tenant handle");
        var a2 = host.CreateIsolate("tenant-a", maxActors: 1);
        if (a2.Generation == a.Generation) throw new Exception("isolate epoch replay");
        host.Start(a2);
        ExpectFailure(() => host.RetireActor(a2, actorA), "actor from old generation");
        var replacement = host.RegisterActor(a2, 3);
        host.RetireActor(a2, replacement);
        host.BeginShutdown(a2); host.CompleteShutdown(a2); host.Destroy(a2);
        // Concurrent physical guest invocation must pin the tenant until the
        // generated host step completes, even when shutdown has begun.
        var isolateVm = new OresVmHost(maxTenants: 1);
        var executing = isolateVm.CreateIsolate("busy");
        isolateVm.Start(executing);
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var active = Task.Run(() => isolateVm.InvokeGeneratedScalar(executing, () => {
            entered.Set();
            if (!finish.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("native generated step did not retire");
            return 7;
        }));
        if (!entered.Wait(TimeSpan.FromSeconds(10)))
            throw new Exception("native tenant test guest never started");
        try {
            isolateVm.BeginShutdown(executing);
            ExpectFailure(() => isolateVm.CompleteShutdown(executing),
                "inflight generated guest pin must block shutdown");
            if (isolateVm.GuestSnapshot(executing).GuestCalls != 1)
                throw new Exception("guest execution lifetime was not pinned");
        }
        finally { finish.Set(); }
        if (active.GetAwaiter().GetResult() != 7)
            throw new Exception("running guest could not commit during quiescence");
        isolateVm.CompleteShutdown(executing);
        isolateVm.Destroy(executing);

        var overflowVm = new OresVmHost(maxTenants: 1);
        var over = overflowVm.CreateIsolate("overflow");
        overflowVm.Start(over);
        if (overflowVm.InvokeGeneratedScalar(over, () => long.MaxValue) != long.MaxValue)
            throw new Exception("incorrect i64 max value");
        try {
            overflowVm.InvokeGeneratedScalar(over, () => 1);
            throw new Exception("expected checked tenant scalar overflow");
        } catch (OverflowException) { }
        if (overflowVm.GuestSnapshot(over).Scalar != long.MaxValue ||
            overflowVm.GuestSnapshot(over).GuestCalls != 0)
            throw new Exception("overflow corrupted tenant state or left guest pinned");
        overflowVm.BeginShutdown(over);overflowVm.CompleteShutdown(over);overflowVm.Destroy(over);
        // Durable lock queue host parity: actors serialize across different locks,
        // while two tenant isolates may use identical lock IDs independently.
        var durableVm = new OresVmHost(maxTenants: 2);
        var da = durableVm.CreateIsolate("durable-a", maxActors: 2);
        var db = durableVm.CreateIsolate("durable-b", maxActors: 1);
        durableVm.Start(da); durableVm.Start(db);
        var actor1 = durableVm.RegisterActor(da, 1);
        var actor2 = durableVm.RegisterActor(da, 2);
        var actorOtherTenant = durableVm.RegisterActor(db, 1);
        var head = durableVm.EnqueueDurable(da, actor1, 7, 1, 4);
        var waiter = durableVm.EnqueueDurable(da, actor1, 7, 2, 4);
        var anotherLock = durableVm.EnqueueDurable(da, actor1, 9, 3, 4);
        var foreign = durableVm.EnqueueDurable(db, actorOtherTenant, 7, 1, 4);
        if (!head.Head || waiter.Head || !anotherLock.Head || !foreign.Head)
            throw new Exception("durable FIFO admission");
        ExpectFailure(() => durableVm.ClaimDurable(da, foreign.Ticket), "foreign ticket");
        ExpectFailure(() => durableVm.FinishDurable(da, head.Ticket), "unclaimed finish");
        ExpectFailure(() => durableVm.RetireActor(da, actor1), "actor with durable work");
        if (!durableVm.ClaimDurable(da, head.Ticket) ||
            durableVm.ClaimDurable(da, anotherLock.Ticket))
            throw new Exception("actor must be non-reentrant");
        if (!ReferenceEquals(durableVm.FinishDurable(da, head.Ticket), waiter.Ticket) ||
            !durableVm.ClaimDurable(da, anotherLock.Ticket) ||
            durableVm.ClaimDurable(da, waiter.Ticket))
            throw new Exception("FIFO transition");
        durableVm.FinishDurable(da, anotherLock.Ticket);
        if (!durableVm.ClaimDurable(da, waiter.Ticket))
            throw new Exception("next FIFO owner never claimable");
        durableVm.FinishDurable(da, waiter.Ticket);
        var unstarted = durableVm.EnqueueDurable(da, actor2, 7, 4, 4);
        durableVm.BeginShutdown(da);
        ExpectFailure(() => durableVm.ClaimDurable(da, unstarted.Ticket), "shutdown claim");
        ExpectFailure(() => durableVm.CompleteShutdown(da), "outstanding queued head");
        durableVm.CancelDurableHead(da, unstarted.Ticket);
        durableVm.RetireActor(da, actor1); durableVm.RetireActor(da, actor2);
        durableVm.CompleteShutdown(da); durableVm.Destroy(da);
        if (!durableVm.ClaimDurable(db, foreign.Ticket))
            throw new Exception("foreign tenant lost queue admission");
        durableVm.FinishDurable(db, foreign.Ticket);
        durableVm.RetireActor(db, actorOtherTenant);
        durableVm.BeginShutdown(db); durableVm.CompleteShutdown(db); durableVm.Destroy(db);

        host.RetireActor(b, actorB);
        host.BeginShutdown(b); host.CompleteShutdown(b); host.Destroy(b);
        Console.WriteLine("PASS .NET tenant IsolateVM host admission, generation fencing, quiescence and drain");
    }
}
