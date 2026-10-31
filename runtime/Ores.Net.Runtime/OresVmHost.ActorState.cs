namespace Ores.Net.Runtime;

/// <summary>
/// Trusted NativeAOT host-owned scalar state storage for a single actor
/// instance. This is NOT source compiler lowering or a tenant OS sandbox.
/// Every cell access validates and uses the state loan under one host lock.
/// </summary>
public sealed partial class OresVmHost
{
    public enum ActorStateScalarKind { Int64 = 1, Boolean = 2 }

    public readonly record struct ActorStateField(
        ActorStateScalarKind Kind, long Int64Default = 0, bool BooleanDefault = false);

    internal sealed class ActorStateCell
    {
        internal ActorStateCell(ActorStateField field) {
            Kind = field.Kind;
            Int64Value = field.Int64Default;
            BooleanValue = field.BooleanDefault;
        }

        internal readonly ActorStateScalarKind Kind;
        internal long Int64Value;
        internal bool BooleanValue;
    }

    /// <summary>
    /// Install one bounded, immutable typed layout before the actor runs.
    /// Defaults are copied into actor-local cells; no guest obtains the array.
    /// </summary>
    public void ConfigureActorState(
        IsolateHandle handle, ActorTicket actor, ReadOnlySpan<ActorStateField> fields)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActor(tenant, actor);
            if (tenant.Phase != Phase.Running || actor.ActiveTurn is not null ||
                actor.NextTurnGeneration != 1 || actor.StateCells is not null)
                throw new InvalidOperationException("actor state layout admission closed");
            if (fields.Length is < 1 or > 16)
                throw new InvalidOperationException("actor state requires 1 to 16 scalar fields");

            var cells = new ActorStateCell[fields.Length];
            for (var i = 0; i < fields.Length; ++i) {
                var field = fields[i];
                if (field.Kind != ActorStateScalarKind.Int64 &&
                    field.Kind != ActorStateScalarKind.Boolean)
                    throw new InvalidOperationException("unsupported actor state scalar kind");
                if ((field.Kind == ActorStateScalarKind.Int64 && field.BooleanDefault) ||
                    (field.Kind == ActorStateScalarKind.Boolean && field.Int64Default != 0))
                    throw new InvalidOperationException("actor state default does not match scalar kind");
                cells[i] = new ActorStateCell(field);
            }
            actor.StateCells = cells;
        }
    }

    private static ActorStateCell CheckActorStateCell(
        TenantState tenant, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, ActorStateScalarKind kind, bool write)
    {
        // Both the loan check and the actual cell access take place inside
        // _gate, preventing a cancellation/restart between them.
        CheckStateLoan(tenant, actor, turn, loan, write);
        var cells = actor.StateCells;
        if (cells is null || index < 0 || index >= cells.Length)
            throw new InvalidOperationException("actor state field is not configured");
        var cell = cells[index];
        if (cell.Kind != kind)
            throw new InvalidOperationException("actor state field type mismatch");
        return cell;
    }

    public long ReadActorStateInt64(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index)
    {
        lock (_gate) {
            var tenant = Check(handle);
            return CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Int64, false).Int64Value;
        }
    }

    public void WriteActorStateInt64(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, long value)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Int64, true).Int64Value = value;
        }
    }

    /// <summary>One checked read/modify/write without an external TOCTOU gap.</summary>
    public long AddActorStateInt64(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, long delta)
    {
        lock (_gate) {
            var tenant = Check(handle);
            var cell = CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Int64, true);
            var next = checked(cell.Int64Value + delta);
            cell.Int64Value = next;
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

        /// <summary>Admit one bounded empty control message for a future turn.</summary>
        public void Enqueue(IsolateHandle isolate, ActorTicket actor)
            => Host.EnqueueScheduledActor(this, isolate, actor);

        /// <summary>
        /// Consume an actual FIFO message before admitting a logical turn.
        /// The host owns scheduling; the guest receives only a state scope.
        /// </summary>
        public long DispatchNext(IsolateHandle isolate, ActorTicket actor,
            Func<ActorDispatchScope, long> generatedMethod)
            => Host.DispatchQueuedActor(this, isolate, actor, generatedMethod);
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
            // Host and mailbox gates are held in the same order as SendActor.
            // TryReceive refuses to consume typed scheduler controls.
            return actor.Mailbox.TryReceive(out var payload) ? payload : null;
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
        IsolateHandle handle, ActorTicket actor,
        Func<ActorDispatchScope, long> generatedMethod)
    {
        ArgumentNullException.ThrowIfNull(generatedMethod);
        if (!ReferenceEquals(scheduler.Host, this))
            throw new InvalidOperationException("foreign actor scheduler");

        ActorTurnTicket turn;
        lock (_gate) {
            var tenant = Check(handle);
            CheckActor(tenant, actor);
            if (tenant.Phase != Phase.Running || actor.ActiveTurn is not null)
                throw new InvalidOperationException("actor logical turn admission closed");
            // Fail before consuming the FIFO control receipt. A registered actor
            // is not runnable by the generated state path until its typed layout
            // is installed; the same receipt may be retried after configuration.
            if (actor.StateCells is null)
                throw new InvalidOperationException("actor state layout is not configured");
            if (actor.NextTurnGeneration == ulong.MaxValue)
                throw new InvalidOperationException("actor logical turn generation exhausted");
            // Do not dequeue nonempty messages or start a turn without
            // consuming exactly one admitted, FIFO-head control tick.
            if (!actor.Mailbox.TryReceiveEmptyDispatch())
                throw new InvalidOperationException(
                    "actor dispatch requires queued empty control message");
            // _gate remains held across dequeue and logical-turn admission.
            // No other host thread may race to consume or retire the actor.
            turn = BeginActorTurn(handle, actor);
        }
        ActorDispatchScope? scope = null;
        try {
            var loan = BeginActorStateLoan(handle, actor, turn, writable: true);
            scope = new ActorDispatchScope(this, handle, actor, turn, loan);
            var result = generatedMethod(scope);
            scope.Revoke();
            EndActorStateLoan(handle, actor, turn, loan);
            CompleteActorTurn(handle, actor, turn);
            return result;
        }
        catch {
            // Scope revocation precedes logical-lane retirement, even when
            // the generated method retained the scope or threw mid-update.
            scope?.Revoke();
            try { CancelActorTurn(handle, actor, turn); }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    public bool ReadActorStateBoolean(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index)
    {
        lock (_gate) {
            var tenant = Check(handle);
            return CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Boolean, false).BooleanValue;
        }
    }

    public void WriteActorStateBoolean(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, int index, bool value)
    {
        lock (_gate) {
            var tenant = Check(handle);
            CheckActorStateCell(tenant, actor, turn, loan, index,
                ActorStateScalarKind.Boolean, true).BooleanValue = value;
        }
    }
}
