namespace Ores.Net.Runtime;

/// <summary>
/// Native AOT-compatible, host-owned tenant admission and lifecycle authority.
/// Logical tenant separation only. This is NOT a CLR AppDomain, collectible
/// AssemblyLoadContext, heap security sandbox, or process isolation.
/// </summary>
public sealed partial class OresVmHost
{
    public enum Phase { Created, Running, Quiescing, Stopped }

    // A NativeAOT host-owned dictionary is NOT a separate tenant CLR heap.
    // Explicit process mode refuses admission until a supervised OS sandbox
    // and independently authenticated worker identity actually exist.
    public enum IsolationMode { TrustedInProcess, SandboxedProcess }

    public sealed class IsolateHandle
    {
        internal IsolateHandle(string tenantId, ulong generation) {
            TenantId = tenantId; Generation = generation;
        }
        public string TenantId { get; }
        public ulong Generation { get; }
    }

    public sealed class ActorTicket
    {
        internal ActorTicket(IsolateHandle isolate, ulong id, ulong epoch,
            ActorMailbox mailbox) {
            Isolate = isolate; Id = id; Epoch = epoch; Mailbox = mailbox;
        }
        internal IsolateHandle Isolate { get; }
        public ulong Id { get; }
        internal ulong Epoch { get; }
        internal ActorMailbox Mailbox { get; }
        internal ulong NextTurnGeneration { get; set; } = 1;
        internal ActorTurnTicket? ActiveTurn { get; set; }
        // Compiler-owned, typed actor-local cells. Installed once before any turn.
        internal ActorStateCell[]? StateCells { get; set; }
        internal string? StateLayoutName { get; set; }
    }

    public sealed class ActorTurnTicket
    {
        internal ActorTurnTicket(ActorTicket actor, ulong generation) {
            Actor = actor; Generation = generation;
        }
        internal ActorTicket Actor { get; }
        public ulong Generation { get; }
        internal bool Suspended { get; set; }
        internal bool Completed { get; set; }
        internal ulong NextStateLoanGeneration { get; set; } = 1;
        internal int StateLoanDepth { get; set; }
        internal ActorStateLoan? ActiveStateLoan { get; set; }
    }

    /// <summary>Ephemeral host-owned actor state access, not a reference to state storage.</summary>
    public sealed class ActorStateLoan
    {
        internal ActorStateLoan(ActorTurnTicket turn, ulong generation,
            ActorStateLoan? parent, bool writable) {
            Turn = turn; Generation = generation; Parent = parent; Writable = writable;
        }
        internal ActorTurnTicket Turn { get; }
        public ulong Generation { get; }
        internal ActorStateLoan? Parent { get; }
        public bool Writable { get; }
        internal bool Released { get; set; }
    }

    public sealed class ResourcePin
    {
        internal ResourcePin(IsolateHandle isolate, ActorTicket actor) {
            Isolate = isolate; Actor = actor;
        }
        internal IsolateHandle Isolate { get; }
        internal ActorTicket Actor { get; }
        internal bool Released { get; set; }
    }

    private sealed class TenantState
    {
        internal required IsolateHandle Handle { get; init; }
        internal required int ActorLimit { get; init; }
        internal required int PinLimit { get; init; }
        internal Phase Phase;
        internal ulong NextActorEpoch = 1;
        internal readonly Dictionary<ulong, ActorTicket> Actors = new();
        internal readonly Dictionary<ActorTicket, int> PinsByActor = new();
        internal int ActivePins;
        internal long ScalarState;
        internal int ActiveGuestCalls;
        internal readonly object GuestTurn = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, TenantState> _tenants = new(StringComparer.Ordinal);
    private readonly int _maxTenants;
    private ulong _nextGeneration = 1;
    [ThreadStatic] private static bool _inGuestTurn;

    public OresVmHost(int maxTenants = 32)
    {
        if (maxTenants is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maxTenants));
        _maxTenants = maxTenants;
    }

    private TenantState Check(IsolateHandle handle)
    {
        if (handle is null || !_tenants.TryGetValue(handle.TenantId, out var state) ||
            !ReferenceEquals(state.Handle, handle) ||
            state.Handle.Generation != handle.Generation)
            throw new InvalidOperationException("stale or foreign tenant isolate");
        return state;
    }

    private static void CheckActor(TenantState state, ActorTicket actor)
    {
        if (actor is null || !ReferenceEquals(actor.Isolate, state.Handle) ||
            !state.Actors.TryGetValue(actor.Id, out var current) ||
            !ReferenceEquals(current, actor))
            throw new InvalidOperationException("stale or foreign actor ticket");
    }

    public IsolateHandle CreateIsolate(string tenantId, int maxActors = 64, int maxResourcePins = 1024,
        IsolationMode isolation = IsolationMode.TrustedInProcess)
    {
        if (isolation != IsolationMode.TrustedInProcess)
            throw new InvalidOperationException("sandboxed tenant process isolation not implemented");
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 128 ||
            !tenantId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new ArgumentException("invalid tenant ID", nameof(tenantId));
        if (maxActors is < 1 or > 65536 || maxResourcePins is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxActors), "invalid tenant quotas");
        lock (_gate) {
            if (_tenants.ContainsKey(tenantId)) throw new InvalidOperationException("duplicate tenant");
            if (_tenants.Count >= _maxTenants) throw new InvalidOperationException("host tenant quota");
            if (_nextGeneration == ulong.MaxValue) throw new InvalidOperationException("generation exhausted");
            var handle = new IsolateHandle(tenantId, _nextGeneration++);
            _tenants.Add(tenantId, new TenantState {
                Handle = handle, ActorLimit = maxActors, PinLimit = maxResourcePins
            });
            return handle;
        }
    }

    public void Start(IsolateHandle handle)
    {
        lock (_gate) {
            var s = Check(handle);
            if (s.Phase != Phase.Created) throw new InvalidOperationException("start phase");
            s.Phase = Phase.Running;
        }
    }

    public ActorTicket RegisterActor(IsolateHandle handle, ulong id)
    {
        lock (_gate) {
            var s = Check(handle);
            if (s.Phase != Phase.Running || id == 0 || s.Actors.ContainsKey(id) ||
                s.Actors.Count >= s.ActorLimit)
                throw new InvalidOperationException("actor admission denied");
            if (s.NextActorEpoch == ulong.MaxValue)
                throw new InvalidOperationException("actor epoch exhausted");
            // Existing byte-copy mailbox enforces ActorRef capability and queue caps.
            var actor = new ActorTicket(handle, id, s.NextActorEpoch++, new ActorMailbox(id));
            s.Actors.Add(id, actor);
            s.PinsByActor.Add(actor, 0);
            return actor;
        }
    }

    // Host-owned admission: caller cannot bypass the tenant phase gate or
    // resurrect a retired mailbox through ActorTicket.
    public void SendActor(IsolateHandle handle, ActorTicket actor, ActorRef destination,
        ReadOnlySpan<byte> payload)
    {
        lock (_gate) {
            var s = Check(handle); CheckActor(s, actor);
            if (s.Phase != Phase.Running)
                throw new InvalidOperationException("tenant actor admission closed");
            actor.Mailbox.Send(destination, payload);
        }
    }

    private static void CheckTurn(
        TenantState state, ActorTicket actor, ActorTurnTicket turn)
    {
        CheckActor(state, actor);
        if (turn is null || turn.Completed || !ReferenceEquals(turn.Actor, actor) ||
            !ReferenceEquals(actor.ActiveTurn, turn))
            throw new InvalidOperationException("stale or foreign actor logical turn");
    }

    /// <summary>
    /// Admit one actor-local user turn. Suspension never clears ActiveTurn:
    /// it releases only the physical carrier and later user turns stay denied.
    /// </summary>
    public ActorTurnTicket BeginActorTurn(IsolateHandle handle, ActorTicket actor)
    {
        lock (_gate) {
            var s = Check(handle); CheckActor(s, actor);
            if (s.Phase != Phase.Running)
                throw new InvalidOperationException("tenant actor admission closed");
            if (actor.ActiveTurn is not null)
                throw new InvalidOperationException("actor logical turn already active");
            if (actor.NextTurnGeneration == ulong.MaxValue)
                throw new InvalidOperationException("actor logical turn generation exhausted");
            var turn = new ActorTurnTicket(actor, actor.NextTurnGeneration++);
            actor.ActiveTurn = turn;
            return turn;
        }
    }

    /// <summary>
    /// A state loan can only be minted for an active unsuspended actor turn.
    /// Nested scopes fence the parent loan until the child is returned.
    /// </summary>
    public ActorStateLoan BeginActorStateLoan(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        bool writable = false)
    {
        lock (_gate) {
            var s = Check(handle); CheckTurn(s, actor, turn);
            if (turn.Suspended)
                throw new InvalidOperationException("actor state access while suspended");
            if (turn.StateLoanDepth >= 32)
                throw new InvalidOperationException("bounded actor state loan nesting exceeded");
            if (turn.NextStateLoanGeneration == ulong.MaxValue)
                throw new InvalidOperationException("actor state loan generation exhausted");
            var loan = new ActorStateLoan(turn, turn.NextStateLoanGeneration++,
                turn.ActiveStateLoan, writable);
            turn.ActiveStateLoan = loan;
            ++turn.StateLoanDepth;
            return loan;
        }
    }

    private static void CheckStateLoan(
        TenantState state, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, bool requireWrite)
    {
        CheckTurn(state, actor, turn);
        if (turn.Suspended || loan is null || loan.Released ||
            !ReferenceEquals(loan.Turn, turn) ||
            !ReferenceEquals(turn.ActiveStateLoan, loan))
            throw new InvalidOperationException("stale, nested-fenced or foreign actor state loan");
        if (requireWrite && !loan.Writable)
            throw new InvalidOperationException("read-only actor state loan cannot write");
    }

    /// <summary>Generated state operations must enforce this validation.</summary>
    public void ValidateActorStateLoan(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan, bool requireWrite = false)
    {
        lock (_gate) {
            var s = Check(handle);
            CheckStateLoan(s, actor, turn, loan, requireWrite);
        }
    }

    public void EndActorStateLoan(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn,
        ActorStateLoan loan)
    {
        lock (_gate) {
            var s = Check(handle);
            CheckStateLoan(s, actor, turn, loan, false);
            loan.Released = true;
            turn.ActiveStateLoan = loan.Parent;
            --turn.StateLoanDepth;
        }
    }

    public void SuspendActorTurn(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn)
    {
        lock (_gate) {
            var s = Check(handle); CheckTurn(s, actor, turn);
            if (turn.Suspended)
                throw new InvalidOperationException("actor logical turn already suspended");
            if (turn.ActiveStateLoan is not null)
                throw new InvalidOperationException("state loan must retire before suspension");
            turn.Suspended = true;
        }
    }

    public void ResumeActorTurn(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn)
    {
        lock (_gate) {
            var s = Check(handle); CheckTurn(s, actor, turn);
            if (!turn.Suspended)
                throw new InvalidOperationException("actor logical turn is not suspended");
            turn.Suspended = false;
        }
    }

    public void CompleteActorTurn(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn)
    {
        lock (_gate) {
            var s = Check(handle); CheckTurn(s, actor, turn);
            if (turn.ActiveStateLoan is not null)
                throw new InvalidOperationException("state loan must retire before completion");
            turn.Completed = true;
            turn.Suspended = false;
            actor.ActiveTurn = null;
        }
    }

    /// <summary>
    /// Cancellation owns the same terminal transition as successful completion:
    /// it fences the exact active turn, clears actor admission once, and makes
    /// every later resume/complete/cancel attempt stale.
    /// </summary>
    public void CancelActorTurn(
        IsolateHandle handle, ActorTicket actor, ActorTurnTicket turn)
    {
        lock (_gate) {
            var s = Check(handle); CheckTurn(s, actor, turn);
            // Terminal cancellation revokes all bounded nested scopes.
            while (turn.ActiveStateLoan is { } loan) {
                loan.Released = true;
                turn.ActiveStateLoan = loan.Parent;
            }
            turn.StateLoanDepth = 0;
            turn.Completed = true;
            turn.Suspended = false;
            actor.ActiveTurn = null;
        }
    }

    public (bool Active, bool Suspended, ulong Generation) ActorTurnSnapshot(
        IsolateHandle handle, ActorTicket actor)
    {
        lock (_gate) {
            var s = Check(handle); CheckActor(s, actor);
            var turn = actor.ActiveTurn;
            return turn is null
                ? (false, false, 0UL)
                : (true, turn.Suspended, turn.Generation);
        }
    }

    public ResourcePin PinResource(IsolateHandle handle, ActorTicket actor)
    {
        lock (_gate) {
            var s = Check(handle); CheckActor(s, actor);
            if (s.Phase != Phase.Running || s.ActivePins >= s.PinLimit)
                throw new InvalidOperationException("resource admission denied");
            var pin = new ResourcePin(handle, actor);
            s.PinsByActor[actor]++;
            s.ActivePins++;
            return pin;
        }
    }

    // Only the trusted native owner calls this after physical retirement.
    public void RetireResource(IsolateHandle handle, ActorTicket actor, ResourcePin pin)
    {
        lock (_gate) {
            var s = Check(handle); CheckActor(s, actor);
            if (pin is null || pin.Released || !ReferenceEquals(pin.Isolate, handle) ||
                !ReferenceEquals(pin.Actor, actor))
                throw new InvalidOperationException("stale or foreign resource ticket");
            pin.Released = true;
            --s.ActivePins; --s.PinsByActor[actor];
        }
    }

    public void RetireActor(IsolateHandle handle, ActorTicket actor)
    {
        lock (_gate) {
            var s = Check(handle); CheckActor(s, actor);
            if (s.PinsByActor[actor] != 0)
                throw new InvalidOperationException("actor has outstanding resources");
            if (actor.ActiveTurn is not null)
                throw new InvalidOperationException("actor has active logical turn");
            actor.Mailbox.Stop(); // Erase pending private queued copies.
            // Clear bounded actor-local cells before removing host ownership.
            // Managed memory may still be retained by the GC; this is logical
            // retirement and best-effort value erasure, not physical zeroization.
            if (actor.StateCells is { } cells) {
                foreach (var cell in cells) {
                    cell.Int64Value = 0;
                    cell.BooleanValue = false;
                }
                actor.StateCells = null;
            }
            actor.StateLayoutName = null;
            s.PinsByActor.Remove(actor);
            s.Actors.Remove(actor.Id);
        }
    }

    /// <summary>
    /// Trusted Native AOT scalar bridge. Execute the statically linked
    /// generated CIL entry once, and add its checked result to tenant state.
    /// This does not lower .ores actor/global state into CIL or sandbox CLR
    /// code. Source must compile to a statically linked Func<long> entry.
    /// </summary>
    public long InvokeGeneratedScalar(IsolateHandle handle, Func<long> generatedEntry)
    {
        ArgumentNullException.ThrowIfNull(generatedEntry);
        if (_inGuestTurn) throw new InvalidOperationException("nested guest entry forbidden");
        _inGuestTurn = true;
        try
        {
            TenantState tenant;
            lock (_gate) {
                tenant = Check(handle);
                if (tenant.Phase != Phase.Running)
                    throw new InvalidOperationException("native guest admission closed");
                if (tenant.ActiveGuestCalls >= 64)
                    throw new InvalidOperationException("tenant guest-call limit");
                ++tenant.ActiveGuestCalls;
            }
            try
            {
                lock (tenant.GuestTurn)
                {
                    lock (_gate) {
                        if (tenant.Phase != Phase.Running)
                            throw new InvalidOperationException("queued guest cancelled on shutdown");
                    }
                    // Native AOT knows the target statically; no Reflection.Emit,
                    // dynamic assemblies, or runtime compilation. Do not hold
                    // root host lock while executing generated code.
                    long result;
                    try {
                        result = generatedEntry();
                    } catch {
                        // An arbitrary trusted-native guest callback can
                        // mutate tenant state before throwing. Quarantine
                        // this incarnation and refuse subsequent guest calls.
                        // This does NOT isolate CLR heaps or contain native
                        // memory corruption; OS process mode remains denied.
                        lock (_gate) {
                            if (tenant.Phase == Phase.Running)
                                tenant.Phase = Phase.Quiescing;
                        }
                        throw;
                    }
                    lock (_gate) {
                        if (tenant.Phase is not (Phase.Running or Phase.Quiescing))
                            throw new InvalidOperationException("stopped tenant cannot commit");
                        tenant.ScalarState = checked(tenant.ScalarState + result);
                        return tenant.ScalarState;
                    }
                }
            }
            finally {
                lock (_gate) { --tenant.ActiveGuestCalls; }
            }
        }
        finally { _inGuestTurn = false; }
    }

    public (long Scalar, int GuestCalls) GuestSnapshot(IsolateHandle handle)
    {
        lock (_gate) {
            var tenant = Check(handle);
            return (tenant.ScalarState, tenant.ActiveGuestCalls);
        }
    }

    public void BeginShutdown(IsolateHandle handle)
    {
        lock (_gate) {
            var s = Check(handle);
            if (s.Phase is not (Phase.Running or Phase.Created))
                throw new InvalidOperationException("shutdown phase");
            s.Phase = Phase.Quiescing;
        }
    }

    public void CompleteShutdown(IsolateHandle handle)
    {
        lock (_gate) {
            var s = Check(handle);
            if (s.Phase != Phase.Quiescing || s.Actors.Count != 0 || s.ActivePins != 0 || s.ActiveGuestCalls != 0)
                throw new InvalidOperationException("actor or resource still active");
            s.Phase = Phase.Stopped;
        }
    }

    public void Destroy(IsolateHandle handle)
    {
        lock (_gate) {
            var s = Check(handle);
            if (s.Phase != Phase.Stopped) throw new InvalidOperationException("isolate not stopped");
            _tenants.Remove(handle.TenantId);
        }
    }

    public (Phase Phase, ulong Generation, int Actors, int Pins) Snapshot(IsolateHandle handle)
    {
        lock (_gate) {
            var s = Check(handle);
            return (s.Phase, handle.Generation, s.Actors.Count, s.ActivePins);
        }
    }
}
