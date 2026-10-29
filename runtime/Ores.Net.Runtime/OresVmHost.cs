namespace Ores.Net.Runtime;

/// <summary>
/// Native AOT-compatible, host-owned tenant admission and lifecycle authority.
/// Logical tenant separation only. This is NOT a CLR AppDomain, collectible
/// AssemblyLoadContext, heap security sandbox, or process isolation.
/// </summary>
public sealed class OresVmHost
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
        public ActorMailbox Mailbox { get; }
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
            actor.Mailbox.Stop(); // Erase pending private queued copies.
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
