using System.Buffers.Binary;

namespace Ores.Net.Runtime;

/// <summary>
/// Trusted NativeAOT host-owned scalar state storage for a single actor
/// instance. This is NOT source compiler lowering or a tenant OS sandbox.
/// Every cell access validates and uses the state loan under one host lock.
///
/// atto-spec chapter 27 section 1: the state words live in the actor's own
/// heap (<see cref="ActorTicket.Heap"/>), one 8-byte little-endian word per
/// field at actor-relative offset <c>index * 8</c>, in unmanaged arena bytes
/// outside the CLR GC. Only the field kinds (bookkeeping) are managed. Each
/// access runs inside one owner-validated arena turn, and retirement scrubs,
/// frees and refunds the bytes.
/// </summary>
public sealed partial class OresVmHost
{
    public enum ActorStateScalarKind { Int64 = 1, Boolean = 2 }

    public readonly record struct ActorStateField(
        ActorStateScalarKind Kind, long Int64Default = 0, bool BooleanDefault = false);

    private const int ActorStateWordBytes = sizeof(long);

    private static long LoadActorStateWord(ActorTicket actor, int index)
    {
        Span<byte> word = stackalloc byte[ActorStateWordBytes];
        using (var heapTurn = actor.Heap.EnterTurn(actor.Id, actor.Epoch))
            actor.Heap.Read(heapTurn, actor.StateBlock!, index * ActorStateWordBytes, word);
        return BinaryPrimitives.ReadInt64LittleEndian(word);
    }

    private static void StoreActorStateWord(ActorTicket actor, int index, long value)
    {
        Span<byte> word = stackalloc byte[ActorStateWordBytes];
        BinaryPrimitives.WriteInt64LittleEndian(word, value);
        using var heapTurn = actor.Heap.EnterTurn(actor.Id, actor.Epoch);
        actor.Heap.Write(heapTurn, actor.StateBlock!, index * ActorStateWordBytes, word);
    }

    /// <summary>
    /// Install one bounded, immutable typed layout before the actor runs.
    /// Defaults are copied into actor-local cells; no guest obtains the array.
    /// </summary>
    public void ConfigureActorState(
        IsolateHandle handle, ActorTicket actor, ReadOnlySpan<ActorStateField> fields)
        => ConfigureActorStateCore(handle, actor, null, fields);

    public void ConfigureNamedActorState(
        IsolateHandle handle, ActorTicket actor, string actorName,
        string layoutIdentity, ReadOnlySpan<ActorStateField> fields)
    {
        ValidateActorStateName(actorName);
        var canonicalIdentity = BuildActorStateLayoutIdentity(actorName, fields);
        if (!string.Equals(layoutIdentity, canonicalIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "actor state layout identity does not match configured fields");
        ConfigureActorStateCore(handle, actor, canonicalIdentity, fields);
    }

    private static void ValidateActorStateName(string actorName)
    {
        if (string.IsNullOrEmpty(actorName) || actorName.Length > 128)
            throw new ArgumentException("invalid actor state layout name", nameof(actorName));
        static bool Start(char ch) =>
            ch == '_' || ch is >= 'A' and <= 'Z' || ch is >= 'a' and <= 'z';
        static bool Part(char ch) => Start(ch) || ch is >= '0' and <= '9';
        if (!Start(actorName[0]))
            throw new ArgumentException("invalid actor state layout name", nameof(actorName));
        for (var i = 1; i < actorName.Length; ++i)
            if (!Part(actorName[i]))
                throw new ArgumentException("invalid actor state layout name", nameof(actorName));
    }

    private static string BuildActorStateLayoutIdentity(
        string actorName, ReadOnlySpan<ActorStateField> fields)
    {
        var b = new System.Text.StringBuilder(actorName.Length + 8 + fields.Length);
        b.Append(actorName).Append('|')
            .Append(fields.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .Append(':');
        for (var i = 0; i < fields.Length; ++i)
            b.Append(fields[i].Kind switch {
                ActorStateScalarKind.Int64 => 'I',
                ActorStateScalarKind.Boolean => 'B',
                _ => '?'
            });
        return b.ToString();
    }

    private void ConfigureActorStateCore(
        IsolateHandle handle, ActorTicket actor, string? layoutIdentity,
        ReadOnlySpan<ActorStateField> fields)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActor(tenant, actor);
            if (tenant.Phase != Phase.Running || actor.ActiveTurn is not null ||
                actor.NextTurnGeneration != 1 || actor.StateKinds is not null)
                throw new InvalidOperationException("actor state layout admission closed");
            if (fields.Length is < 1 or > 16)
                throw new InvalidOperationException("actor state requires 1 to 16 scalar fields");

            var kinds = new ActorStateScalarKind[fields.Length];
            for (var i = 0; i < fields.Length; ++i) {
                var field = fields[i];
                if (field.Kind != ActorStateScalarKind.Int64 &&
                    field.Kind != ActorStateScalarKind.Boolean)
                    throw new InvalidOperationException("unsupported actor state scalar kind");
                if ((field.Kind == ActorStateScalarKind.Int64 && field.BooleanDefault) ||
                    (field.Kind == ActorStateScalarKind.Boolean && field.Int64Default != 0))
                    throw new InvalidOperationException("actor state default does not match scalar kind");
                kinds[i] = field.Kind;
            }

            // Allocate and initialize the state words in the actor heap before
            // publishing the layout: a failed charge leaves the actor unconfigured.
            Span<byte> word = stackalloc byte[ActorStateWordBytes];
            using (var heapTurn = actor.Heap.EnterTurn(actor.Id, actor.Epoch)) {
                var block = actor.Heap.AllocatePersistent(
                    heapTurn, fields.Length * ActorStateWordBytes, ActorStateWordBytes);
                for (var i = 0; i < fields.Length; ++i) {
                    BinaryPrimitives.WriteInt64LittleEndian(word,
                        fields[i].Kind == ActorStateScalarKind.Int64
                            ? fields[i].Int64Default
                            : fields[i].BooleanDefault ? 1L : 0L);
                    actor.Heap.Write(heapTurn, block, i * ActorStateWordBytes, word);
                }
                actor.StateBlock = block;
            }
            actor.StateKinds = kinds;
            actor.StateLayoutIdentity = layoutIdentity;
        }
    }

    private static void CheckActorStateCell(
        TenantState tenant, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, ActorStateScalarKind kind, bool write)
    {
        // Both the loan check and the actual heap access take place inside
        // _gate, preventing a cancellation/restart between them.
        CheckStateLoan(tenant, actor, turn, loan, write);
        var kinds = actor.StateKinds;
        if (kinds is null || actor.StateBlock is null || index < 0 || index >= kinds.Length)
            throw new InvalidOperationException("actor state field is not configured");
        if (kinds[index] != kind)
            throw new InvalidOperationException("actor state field type mismatch");
    }

    public long ReadActorStateInt64(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Int64, false);
            return LoadActorStateWord(actor, index);
        }
    }

    public void WriteActorStateInt64(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, long value)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Int64, true);
            StoreActorStateWord(actor, index, value);
        }
    }

    /// <summary>One checked read/modify/write without an external TOCTOU gap.</summary>
    public long AddActorStateInt64(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, long delta)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Int64, true);
            var next = checked(LoadActorStateWord(actor, index) + delta);
            StoreActorStateWord(actor, index, next);
            return next;
        }
    }

    /// <summary>
    /// Opaque, host-minted, single-dispatch state capability. Generated actor
    /// methods receive this scope, never host/actor/turn/loan tickets.
    /// The scope is revoked on exit (including exceptions), and every
    /// operation is checked against the active actor loan under one lock.
    /// </summary>
    public sealed class ActorDispatchScope
    {
        private readonly OresVmHost _host;
        private readonly IsolateHandle _isolate;
        private readonly ActorTicket _actor;
        private readonly ActorTurnTicket _turn;
        private readonly ActorStateLoan _loan;
        private readonly int _ownerThreadId;
        private bool _live = true;

        internal ActorDispatchScope(OresVmHost host, IsolateHandle isolate,
            ActorTicket actor, ActorTurnTicket turn, ActorStateLoan loan)
        {
            _host = host; _isolate = isolate; _actor = actor;
            _turn = turn; _loan = loan;
            _ownerThreadId = Environment.CurrentManagedThreadId;
        }

        private void CheckLive()
        {
            if (!_live)
                throw new InvalidOperationException("retired actor dispatch scope");
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("actor dispatch scope crossed physical carrier");
        }

        internal void Revoke()
        {
            lock (_host._gate) { _live = false; }
        }

        public long AddInt64(int index, long delta)
        {
            lock (_host._gate) {
                CheckLive();
                return _host.AddActorStateInt64(
                    _isolate, _actor, _turn, _loan, index, delta);
            }
        }

        public long ReadInt64(int index)
        {
            lock (_host._gate) {
                CheckLive();
                return _host.ReadActorStateInt64(
                    _isolate, _actor, _turn, _loan, index);
            }
        }

        public void WriteInt64(int index, long value)
        {
            lock (_host._gate) {
                CheckLive();
                _host.WriteActorStateInt64(
                    _isolate, _actor, _turn, _loan, index, value);
            }
        }
    }

    /// <summary>
    /// Host-retained scheduler capability. Guest actor methods cannot create
    /// an instance or mint a dispatch scope; only the trusted host should
    /// receive the scheduler from CreateTrustedScheduler. This does NOT
    /// authenticate arbitrary managed code in the same process.
    /// </summary>
    public sealed class ActorScheduler
    {
        internal readonly OresVmHost Host;

        internal ActorScheduler(OresVmHost host) { Host = host; }

        /// <summary>
        /// Trusted host obtains the current local target capability. This
        /// reference is not a serializable guest address.
        /// </summary>
        public ActorRef Address(IsolateHandle isolate, ActorTicket actor)
            => Host.GetScheduledActorAddress(this, isolate, actor);

        /// <summary>Read FIFO depth for host verification; no dequeue.</summary>
        public int PendingMessages(IsolateHandle isolate, ActorTicket actor)
            => Host.CountScheduledActorMessages(this, isolate, actor);

        /// <summary>
        /// Trusted host dequeue of FIFO-head USER data only. Null means empty
        /// queue or a control receipt at the head; an empty byte array is
        /// a real zero-byte user message, never a scheduler receipt.
        /// </summary>
        public byte[]? DequeueUser(IsolateHandle isolate, ActorTicket actor)
            => Host.DequeueScheduledActorUser(this, isolate, actor);

        // Trusted scheduler bridge. Readiness is not a dispatch or carrier ticket.
        internal Task WaitForSupervisorTerminal(IsolateHandle isolate, ActorTicket actor)
            => Host.WaitForSupervisorTerminalAsync(isolate, actor);

        internal bool TryDequeueSupervisorTerminal(IsolateHandle isolate, ActorTicket actor,
            out SupervisorTerminalNotice notice)
            => Host.TryTakeScheduledSupervisorTerminal(this, isolate, actor, out notice);

        /// <summary>Admit one bounded empty control message for a future turn.</summary>
        public void Enqueue(IsolateHandle isolate, ActorTicket actor)
            => Host.EnqueueScheduledActor(this, isolate, actor);

        /// <summary>
        /// Consume an actual FIFO message before admitting a logical turn.
        /// The host owns scheduling; the guest receives only a state scope.
        /// </summary>
        public long DispatchNext(IsolateHandle isolate, ActorTicket actor,
            string expectedLayoutIdentity, Func<ActorDispatchScope, long> generatedMethod)
            => Host.DispatchQueuedActor(
                this, isolate, actor, expectedLayoutIdentity, generatedMethod);
    }

    /// <summary>
    /// Trusted embedding host obtains and retains this scheduler. It must
    /// never expose it to an untrusted guest or external tenant.
    /// </summary>
    public ActorScheduler CreateTrustedScheduler() => new(this);

    private ActorRef GetScheduledActorAddress(
        ActorScheduler scheduler, IsolateHandle isolate, ActorTicket actor)
    {
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        lock (_gate) {
            var tenant = Check(isolate);
            CheckActor(tenant, actor);
            if (tenant.Phase != Phase.Running)
                throw new InvalidOperationException("tenant actor admission closed");
            return actor.Mailbox.CurrentRef;
        }
    }

    private int CountScheduledActorMessages(
        ActorScheduler scheduler, IsolateHandle isolate, ActorTicket actor)
    {
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        lock (_gate) {
            var tenant = Check(isolate);
            CheckActor(tenant, actor);
            return actor.Mailbox.QueuedMessages;
        }
    }

    private byte[]? DequeueScheduledActorUser(
        ActorScheduler scheduler, IsolateHandle isolate, ActorTicket actor)
    {
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        lock (_gate) {
            var tenant = Check(isolate);
            CheckActor(tenant, actor);
            if (tenant.Phase != Phase.Running || actor.ActiveTurn is not null)
                throw new InvalidOperationException("actor user dequeue admission closed");
            CheckOrdinaryDispatchPriority(tenant, actor);
            // Host and mailbox gates are held in the same order as SendActor.
            // TryReceive refuses to consume typed scheduler controls.
            // Use the current host-mediated path so dequeuing DATA refunds
            // actor, tenant and host byte accounting exactly once. Typed
            // control at FIFO head remains unconsumed by that operation.
            if (!TryReceiveActor(isolate, actor, out var payload)) return null;
            actor.SupervisorControlBurst = 0;
            return payload;
        }
    }

    private void EnqueueScheduledActor(
        ActorScheduler scheduler, IsolateHandle isolate, ActorTicket actor)
    {
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");
        lock (_gate) {
            var tenant = Check(isolate);
            CheckActor(tenant, actor);
            if (tenant.Phase != Phase.Running)
                throw new InvalidOperationException("tenant actor admission closed");
            // Only the trusted host may enqueue typed control envelopes.
            // Empty user Send() cannot forge a dispatch ticket.
            actor.Mailbox.EnqueueTrustedDispatch(actor.Mailbox.CurrentRef);
        }
    }

    private long DispatchQueuedActor(ActorScheduler scheduler,
        IsolateHandle handle, ActorTicket actor, string expectedLayoutIdentity,
        Func<ActorDispatchScope, long> generatedMethod)
    {
        ArgumentNullException.ThrowIfNull(generatedMethod);
        if (string.IsNullOrEmpty(expectedLayoutIdentity) ||
            expectedLayoutIdentity.Length > 256)
            throw new ArgumentException(
                "invalid actor state layout identity", nameof(expectedLayoutIdentity));
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");

        // The lease is private to this synchronous invocation, never passed
        // through the guest scope or an external cancellation callback.
        var carrierLease = new object();
        ActorTurnTicket turn;
        lock (_gate) {
            var tenant = Check(handle);
            CheckActor(tenant, actor);
            if (tenant.Phase != Phase.Running || actor.ActiveTurn is not null)
                throw new InvalidOperationException("actor logical turn admission closed");
            // Fail before consuming the FIFO control receipt. A registered actor
            // is not runnable by the generated state path until its typed layout
            // is installed; the same receipt may be retried after configuration.
            if (actor.StateKinds is null)
                throw new InvalidOperationException("actor state layout is not configured");
            if (!string.Equals(actor.StateLayoutIdentity, expectedLayoutIdentity,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "generated actor method does not match configured actor layout");
            if (actor.NextTurnGeneration == ulong.MaxValue)
                throw new InvalidOperationException("actor logical turn generation exhausted");
            CheckOrdinaryDispatchPriority(tenant, actor);
            // Do not dequeue nonempty messages or start a turn without
            // consuming exactly one admitted, FIFO-head control tick.
            if (!actor.Mailbox.TryReceiveEmptyDispatch())
                throw new InvalidOperationException(
                    "actor dispatch requires queued empty control message");
            // _gate remains held across dequeue and logical-turn admission.
            // No other host thread may race to consume or retire the actor.
            turn = BeginActorTurn(handle, actor);
            actor.SupervisorControlBurst = 0;
            turn.ScheduledCarrierLease = carrierLease;
            turn.ScheduledCarrierThread = Environment.CurrentManagedThreadId;
        }
        ActorDispatchScope? scope = null;
        ActorStateLoan? loan = null;
        try {
            loan = BeginActorStateLoan(handle, actor, turn, writable: true);
            scope = new ActorDispatchScope(this, handle, actor, turn, loan);
            var result = generatedMethod(scope);
            scope.Revoke();
            EndActorStateLoan(handle, actor, turn, loan);
            FinishScheduledActorExecution(handle, actor, turn, carrierLease, cancelled: false);
            return result;
        }
        catch {
            // Scope revocation precedes logical-lane retirement, even when
            // the generated method retained the scope or threw mid-update.
            scope?.Revoke();
            // This synchronous dispatch owner has returned from the generated
            // delegate (including its unwind); the scope is already revoked.
            // Request alone cannot release the lane.
            try {
                CancelActorTurn(handle, actor, turn);
                if (loan is not null && !loan.Released)
                    EndActorStateLoan(handle, actor, turn, loan);
                FinishScheduledActorExecution(handle, actor, turn, carrierLease, cancelled: true);
            }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    // Only the scheduler invocation that installed the exact lease can
    // relinquish it, after the delegate and its scope have unwound. Do not
    // expose this as a caller-mintable physical-retirement receipt.
    private void FinishScheduledActorExecution(IsolateHandle handle, ActorTicket actor,
        ActorTurnTicket turn, object carrierLease, bool cancelled)
    {
        lock (_gate) {
            var state = Check(handle);
            CheckTurn(state, actor, turn);
            if (!ReferenceEquals(turn.ScheduledCarrierLease, carrierLease) ||
                turn.ScheduledCarrierThread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("stale or foreign scheduled carrier retirement");
            if (turn.ActiveStateLoan is not null || turn.StateLoanDepth != 0)
                throw new InvalidOperationException("scheduled carrier still owns state loans");
            // Cancellation may race the return of a successful delegate.
            // Keep the lease installed until the failure path retires it.
            if (turn.CancellationRequested != cancelled)
                throw new InvalidOperationException("scheduled carrier cancellation outcome mismatch");
            turn.ScheduledCarrierLease = null;
            turn.ScheduledCarrierThread = 0;
            if (cancelled) AcknowledgeActorTurnCancellation(handle, actor, turn);
            else CompleteActorTurn(handle, actor, turn);
        }
    }

    public bool ReadActorStateBoolean(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Boolean, false);
            return LoadActorStateWord(actor, index) != 0;
        }
    }

    public void WriteActorStateBoolean(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, bool value)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Boolean, true);
            StoreActorStateWord(actor, index, value ? 1L : 0L);
        }
    }
}
