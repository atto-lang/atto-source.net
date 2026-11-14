using System.Collections.Generic;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ores.Net.Runtime;

public delegate void ActorMessageHandler(
    ActorContext context, ReadOnlyMemory<byte> message);

/// <summary>
/// Bounded host-runtime options. This remains a runtime integration substrate,
/// not source-level actor lowering.
/// </summary>
public sealed class ActorRuntimeOptions
{
    public int MaxActors { get; init; } = 4096;
    public int MessagesPerDispatch { get; init; } = 64;
    public int MailboxMessages { get; init; } = 1024;
    public int MailboxBytes { get; init; } = 4 * ActorMailbox.MaxMessageBytes;
    // Opt-in native queued storage: copy-on-dequeue preserves retainable
    // ReadOnlyMemory callback isolation. This is NOT zero-GC delivery.
    public int NativeMailboxSlotBytes { get; init; } = 0;
    public int ActorPersistentBytes { get; init; } = 1024 * 1024;
    public int ActorScratchBytes { get; init; } = 64 * 1024;
    // Opt-in non-realtime profile. Logical quotas remain fixed while physical
    // native backing is charged and acquired on the first guest allocation.
    // Eager/prefault deployment remains the default until native AOT gates run.
    public bool DemandBackedActorHeaps { get; init; } = false;
    // 0 chooses a bounded default (one quarter of the actor persistent arena,
    // capped at 64 KiB). All frames share that actor's existing native budget.
    public int ActorStackBytes { get; init; } = 0;
    public int ActorMaxStackFrames { get; init; } = 256;
    public int ActorMaxAllocations { get; init; } = 4096;
    public int ActorMaxLifetimePins { get; init; } = 4096;
    public int ActorMaxReadLoans { get; init; } = 4096;
    public long NativeBudgetBytes { get; init; } = 512L * 1024 * 1024;
    public ActorDispatcherOptions Dispatcher { get; init; } = new();

    internal void Validate()
    {
        if (MaxActors <= 0 || MaxActors > 65_536)
            throw new ArgumentOutOfRangeException(nameof(MaxActors));
        if (MessagesPerDispatch <= 0 || MessagesPerDispatch > 4096)
            throw new ArgumentOutOfRangeException(nameof(MessagesPerDispatch));
        if (MailboxMessages <= 0 ||
            MailboxMessages > ActorMailbox.MaxMailboxMessages)
            throw new ArgumentOutOfRangeException(nameof(MailboxMessages));
        if (MailboxBytes <= 0 || MailboxBytes > ActorMailbox.MaxMailboxBytes)
            throw new ArgumentOutOfRangeException(nameof(MailboxBytes));
        if (NativeMailboxSlotBytes < 0 || NativeMailboxSlotBytes > 4096 ||
            NativeMailboxSlotBytes > MailboxBytes)
            throw new ArgumentOutOfRangeException(nameof(NativeMailboxSlotBytes));
        if (ActorPersistentBytes <= 0 || ActorScratchBytes <= 0 ||
            ActorPersistentBytes > ActorArena.MaxArenaBytes ||
            ActorScratchBytes > ActorArena.MaxArenaBytes ||
            ActorPersistentBytes > ActorArena.MaxArenaBytes - ActorScratchBytes)
            throw new ArgumentOutOfRangeException(nameof(ActorPersistentBytes));
        if (ActorStackBytes < 0 || ActorStackBytes > ActorPersistentBytes)
            throw new ArgumentOutOfRangeException(nameof(ActorStackBytes));
        if (ActorMaxStackFrames <= 0 || ActorMaxStackFrames > 1024)
            throw new ArgumentOutOfRangeException(nameof(ActorMaxStackFrames));
        if (ActorMaxAllocations <= 0 ||
            ActorMaxAllocations > ActorArena.MaxAllocationSlots)
            throw new ArgumentOutOfRangeException(nameof(ActorMaxAllocations));
        if (ActorMaxLifetimePins <= 0 ||
            ActorMaxLifetimePins > ActorArena.MaxOutstandingLifetimePins)
            throw new ArgumentOutOfRangeException(nameof(ActorMaxLifetimePins));
        if (ActorMaxReadLoans <= 0 ||
            ActorMaxReadLoans > ActorArena.MaxOutstandingReadLoans)
            throw new ArgumentOutOfRangeException(nameof(ActorMaxReadLoans));
        if (NativeBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(NativeBudgetBytes));

        ArgumentNullException.ThrowIfNull(Dispatcher);
        Dispatcher.Validate();
        if (Dispatcher.QueueCapacityPerDomain < MaxActors)
            throw new ArgumentOutOfRangeException(nameof(Dispatcher),
                "Each dispatch queue must hold at least one token per live actor");
    }
}

/// <summary>
/// Turn-scoped actor memory facade. It never exposes the native arena or raw
/// pointer. Retaining this context after the handler returns fails closed
/// because its underlying ActorArenaTurn has been disposed.
/// </summary>
public sealed class ActorContext
{
    private readonly ActorArena _arena;
    private readonly ActorArenaTurn _turn;
    private readonly ActorExecutionStack _stack;
    private readonly object _guestFrameGate = new();
    private readonly List<ActorStackFrameHandle> _guestFrames = new();
    private bool _guestFrameFaulted;

    internal ActorContext(ActorRef self, ActorKind kind,
        ActorArena arena, ActorArenaTurn turn,
        ActorExecutionContext execution)
    {
        Self = self;
        Kind = kind;
        _arena = arena;
        _turn = turn;
        _stack = execution.Stack;
        Execution = execution;
    }

    public ActorRef Self { get; }
    public ActorKind Kind { get; }
    public ActorExecutionContext Execution { get; }

    // Host capability providers must enforce the same physical-turn authority
    // as actor memory access. A retained context is not a reusable credential.
    internal void ValidateActiveTurn() => _arena.ValidateActiveTurn(_turn);

    public ActorArenaHandle AllocatePersistent(int bytes, int alignment = 8) =>
        _arena.AllocatePersistent(_turn, bytes, alignment);

    public void ReleasePersistent(ActorArenaHandle handle) =>
        _arena.ReleasePersistent(_turn, handle);

    public void Write(ActorArenaHandle handle, int offset,
        ReadOnlySpan<byte> source) =>
        _arena.Write(_turn, handle, offset, source);

    public void Read(ActorArenaHandle handle, int offset,
        Span<byte> destination) =>
        _arena.Read(_turn, handle, offset, destination);

    public ActorArenaReadLoan BorrowRead(ActorArenaHandle handle) =>
        _arena.BorrowRead(_turn, handle);

    public ActorArenaLifetimePin PinLifetime() =>
        _arena.AcquireLifetimePin(_turn);

    public ActorScratchHandle AllocateScratch(int bytes, int alignment = 8) =>
        _arena.AllocateScratch(_turn, bytes, alignment);

    public void WriteScratch(ActorScratchHandle handle, int offset,
        ReadOnlySpan<byte> source) =>
        _arena.WriteScratch(_turn, handle, offset, source);

    public void ReadScratch(ActorScratchHandle handle, int offset,
        Span<byte> destination) =>
        _arena.ReadScratch(_turn, handle, offset, destination);

    // Compiler-host logical activation frames: actor-owned native bytes, not
    // CLR machine stacks. Frame capabilities are never serialized or exported.
    public ActorStackFrameHandle PushStackFrame(int bytes, int alignment = 8) =>
        _stack.Push(_turn, bytes, alignment);

    public void ReadStackFrame(ActorStackFrameHandle frame, int offset,
        Span<byte> destination) => _stack.Read(_turn, frame, offset, destination);

    public void WriteStackFrame(ActorStackFrameHandle frame, int offset,
        ReadOnlySpan<byte> source) => _stack.Write(_turn, frame, offset, source);

    public void PopStackFrame(ActorStackFrameHandle frame) =>
        _stack.Pop(_turn, frame);

    // Compiler-owned guest frame calls are intentionally separate from the
    // failure-neutral host stack API above. The shipping synchronous actor
    // runtime has no ownership protocol for parking compiled guest frames.
    // Once a guest frame operation fails, the whole handler fails at exit
    // even when a host callback catches the original exception.
    public ActorStackFrameHandle GuestPushStackFrame(int bytes, int alignment = 8)
    {
        lock (_guestFrameGate)
        {
            RequireGuestFrameTurn();
            try
            {
                ActorStackFrameHandle frame = _stack.Push(_turn, bytes, alignment);
                _guestFrames.Add(frame);
                return frame;
            }
            catch (Exception)
            {
                _guestFrameFaulted = true;
                throw;
            }
        }
    }

    public void GuestReadStackFrame(ActorStackFrameHandle frame, int offset,
        Span<byte> destination)
    {
        lock (_guestFrameGate)
        {
            RequireGuestFrameTurn();
            RequireGuestFrameHandle(frame);
            try { _stack.Read(_turn, frame, offset, destination); }
            catch (Exception)
            {
                _guestFrameFaulted = true;
                throw;
            }
        }
    }

    public void GuestWriteStackFrame(ActorStackFrameHandle frame, int offset,
        ReadOnlySpan<byte> source)
    {
        lock (_guestFrameGate)
        {
            RequireGuestFrameTurn();
            RequireGuestFrameHandle(frame);
            try { _stack.Write(_turn, frame, offset, source); }
            catch (Exception)
            {
                _guestFrameFaulted = true;
                throw;
            }
        }
    }

    public void GuestPopStackFrame(ActorStackFrameHandle frame)
    {
        lock (_guestFrameGate)
        {
            RequireGuestFrameTurn();
            if (_guestFrames.Count == 0 ||
                !ReferenceEquals(_guestFrames[^1], frame))
            {
                _guestFrameFaulted = true;
                throw new InvalidOperationException(
                    "Compiled guest stack pop violates strict LIFO");
            }

            try
            {
                _stack.Pop(_turn, frame);
                _guestFrames.RemoveAt(_guestFrames.Count - 1);
            }
            catch (Exception)
            {
                _guestFrameFaulted = true;
                throw;
            }
        }
    }

    internal void ValidateGuestFrameExit()
    {
        // Never try to admit a new physical turn here. Concurrent STOP may
        // have retired the arena while the handler was unwinding.
        lock (_guestFrameGate)
        {
            if (_guestFrameFaulted || _guestFrames.Count != 0)
                throw new InvalidOperationException(
                    "Compiled guest actor frame fault or unbalanced synchronous handler");
        }
    }

    private void RequireGuestFrameTurn()
    {
        // Authenticate the exact carrier and turn before any state mutation.
        // A stolen or expired context must not poison a healthy actor.
        _arena.ValidateActiveTurn(_turn);
        if (_guestFrameFaulted)
            throw new InvalidOperationException(
                "Compiled guest frame execution already faulted");
    }

    private void RequireGuestFrameHandle(ActorStackFrameHandle frame)
    {
        if (!_guestFrames.Contains(frame))
        {
            _guestFrameFaulted = true;
            throw new InvalidOperationException(
                "Compiled guest frame handle is foreign, stale, or host-owned");
        }
    }
    // Internal compiler/runtime ABI authority: never expose sealed type
    // descriptors as guest values or permit caller-provided raw tags to mint
    // type authority on an already-issued activation.
    internal void SealScalarFrame(ActorStackFrameHandle frame,
        ReadOnlySpan<byte> declaredTypes, int blockCount) =>
        _stack.SealScalarFrame(_turn, frame, declaredTypes, blockCount);

    internal void ValidateScalarFrameHeader(
        ActorStackFrameHandle frame, int slots, int blocks) =>
        _stack.ValidateScalarHeader(_turn, frame, slots, blocks);

    internal void ValidateScalarFrameSlot(
        ActorStackFrameHandle frame, int slot, byte kind) =>
        _stack.ValidateScalarSlot(_turn, frame, slot, kind);

    public ActorShardColumns CreateShard(int rows, int fields,
        ActorDataLayout layout, int lanes = 1) =>
        new(_arena, _turn, rows, fields, layout, lanes);

    public double ReadShard(ActorShardColumns shard, int row, int field)
    {
        ArgumentNullException.ThrowIfNull(shard);
        return shard.Read(_turn, row, field);
    }

    public void WriteShard(ActorShardColumns shard, int row, int field, double value)
    {
        ArgumentNullException.ThrowIfNull(shard);
        shard.Write(_turn, row, field, value);
    }

    public void IntegrateShard(ActorShardColumns shard,
        int positionField, int velocityField, double dt)
    {
        ArgumentNullException.ThrowIfNull(shard);
        shard.Integrate(_turn, positionField, velocityField, dt);
    }

    public void CopyShardColumnInto(ActorShardColumns shard, int field,
        Span<double> destination)
    {
        ArgumentNullException.ThrowIfNull(shard);
        shard.CopyColumnInto(_turn, field, destination);
    }

    public void ReleaseShard(ActorShardColumns shard)
    {
        ArgumentNullException.ThrowIfNull(shard);
        shard.Release(_turn);
    }
}

/// <summary>
/// Serial actor runtime integrating bounded mailbox transport, one actor-owned
/// unmanaged arena per actor, and the three runtime-wide execution domains.
///
/// CONTROL does not own a carrier pool. Stop/fault is published by changing the
/// actor cell's control state and the already-scheduled actor-domain token
/// performs finalization after the current physical turn.
/// </summary>
public sealed class ActorRuntime : IDisposable
{
    [ThreadStatic]
    private static ActorRuntime? s_currentRuntime;

    // ExecutionContext flows into Task.Run and other managed offloads,
    // unlike ThreadStatic. Keep the actor-origin marker in captured tasks
    // even after the physical turn returns. Such tasks must never acquire
    // host/supervisor ActorRef pubsub authority by changing carrier threads.
    // This is defense in depth, NOT an unforgeable guest capability: a trusted
    // raw C# host handler can suppress ExecutionContext flow. Compiler guest
    // lowering must use context-bound intrinsics, never these host overloads.
    // A boolean sentinel cannot keep a retired ActorRuntime alive in a
    // captured asynchronous ExecutionContext.
    private static readonly AsyncLocal<bool> s_actorOrigin = new();

    // The actor dispatcher is the physical guest authority boundary.
    // A carrier must never call manager/host ActorRef pubsub APIs on behalf
    // of an arbitrary actor; source lowering uses ActorContext-checked
    // intrinsics that expire when the physical turn ends.
    internal static void RequireHostPubSubAuthority(string operation)
    {
        if (s_currentRuntime is not null || s_actorOrigin.Value)
            throw new System.Security.SecurityException(
                "actor code cannot " + operation +
                "; use a turn-scoped actor capability instead");
    }

    private readonly object _gate = new();
    // A second Dispose caller must wait for worker joining and reclamation,
    // not merely observe the _closed admission flag and return prematurely.
    private readonly object _disposeGate = new();
    private readonly Dictionary<ulong, ActorCell> _actors = new();
    private readonly ActorRuntimeOptions _options;
    private readonly ActorArenaBudget _nativeBudget;
    private readonly ActorDispatchDomains _domains;
    private readonly ActorPubSub _pubSub;

    private ulong _nextActorId;
    private bool _closed;
    private long _completedMessages;
    private long _faultedTurns;

    private sealed class ActorCell
    {
        private readonly ActorRuntime _owner;

        internal ActorCell(ActorRuntime owner, ActorKind kind,
            ActorMailbox mailbox, ActorArena arena, ActorMessageHandler handler,
            int stackBytes, int maxStackFrames)
        {
            _owner = owner;
            Kind = kind;
            Mailbox = mailbox;
            Arena = arena;
            FrameStorage = new ActorFrameStorage(arena, stackBytes, maxStackFrames);
            Handler = handler;
            Reference = mailbox.CurrentRef;
            DispatchAction = Dispatch;
        }

        internal object Gate { get; } = new();
        internal ActorKind Kind { get; }
        internal ActorMailbox Mailbox { get; }
        internal ActorArena Arena { get; }
        internal ActorFrameStorage FrameStorage { get; }
        internal ulong NextExecutionSequence;
        internal ActorMessageHandler Handler { get; }
        internal ActorRef Reference { get; }
        internal Action DispatchAction { get; }
        internal int Scheduled;
        internal bool Stopping;
        // Graceful close rejects new DATA while accepted queued turns drain.
        // Stop() remains a distinct immediate abort that scrubs pending data.
        internal bool Draining;
        internal int Finalized;
        internal TaskCompletionSource Terminated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Dispatch() => _owner.RunCell(this);
    }

    public ActorRuntime(ActorRuntimeOptions? options = null)
    {
        options ??= new ActorRuntimeOptions();
        options.Validate();
        _options = options;
        _nativeBudget = new ActorArenaBudget(options.NativeBudgetBytes);
        _domains = new ActorDispatchDomains(options.Dispatcher);
        _pubSub = new ActorPubSub(
            maxGroups: Math.Min(options.MaxActors, 4096),
            maxGroupsPerActor: 64,
            maxTopics: 4096,
            // Shared ActorGroup admission quota is 64 per actor, matching
            // the Java/Graal bounded cooperation contract.
            maxSubscriptionsPerActor: 64);
    }

    public ActorPubSub PubSub => _pubSub;

    public int LiveActors
    {
        get { lock (_gate) return _actors.Count; }
    }

    public long NativeReservedBytes => _nativeBudget.ReservedBytes;
    public long CompletedMessages => Interlocked.Read(ref _completedMessages);
    public long FaultedTurns => Interlocked.Read(ref _faultedTurns);
    public int StartedCarrierThreads => _domains.TotalStartedWorkers;

    public ActorRef Spawn(ActorKind kind, ActorMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // A C# async lambda can otherwise bind to this void-returning delegate
        // as async-void. It returns to RunCell at the first suspension, which
        // would end the physical actor turn and allow the next message to run
        // while the continuation later resumes outside actor admission.
        // Oreslang suspension requires a compiler/runtime-owned logical turn
        // ledger; until that exists on this host surface, fail closed.
        if (handler.Method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
            throw new ArgumentException(
                "async/async-void host actor handlers are forbidden; suspension requires scheduler-owned logical actor continuation lowering",
                nameof(handler));
        if (kind is not (ActorKind.Private or ActorKind.Shared or ActorKind.Untrusted))
            throw new ArgumentOutOfRangeException(nameof(kind));

        lock (_gate)
        {
            EnsureOpenLocked();
            if (_actors.Count >= _options.MaxActors)
                throw new InvalidOperationException("Actor runtime capacity exhausted");
            if (_nextActorId == ulong.MaxValue)
                throw new OverflowException("Actor runtime ID space exhausted");

            ulong candidateId = _nextActorId + 1;
            ActorArena? arena = null;
            ActorMailbox? mailbox = null;
            try
            {
                mailbox = _options.NativeMailboxSlotBytes == 0
                    ? new ActorMailbox(candidateId,
                        _options.MailboxMessages, _options.MailboxBytes)
                    : new ActorMailbox(candidateId,
                        _options.MailboxMessages, _options.MailboxBytes,
                        _options.NativeMailboxSlotBytes, _nativeBudget);
                ActorRef actorRef = mailbox.CurrentRef;
                arena = new ActorArena(
                    candidateId,
                    actorRef.Generation,
                    _options.ActorPersistentBytes,
                    _options.ActorScratchBytes,
                    _options.ActorMaxAllocations,
                    _options.ActorMaxLifetimePins,
                    _options.ActorMaxReadLoans,
                    _nativeBudget,
                    _options.DemandBackedActorHeaps);

                int stackBytes = _options.ActorStackBytes == 0
                    ? Math.Min(64 * 1024, Math.Max(1, _options.ActorPersistentBytes / 4))
                    : _options.ActorStackBytes;
                var cell = new ActorCell(this, kind, mailbox, arena, handler,
                    stackBytes, _options.ActorMaxStackFrames);
                _actors.Add(candidateId, cell);
                try
                {
                    _pubSub.RegisterActor(cell.Reference, kind);
                }
                catch
                {
                    _actors.Remove(candidateId);
                    throw;
                }
                _nextActorId = candidateId;
                return cell.Reference;
            }
            catch
            {
                // Rollback must refund the native queued slab AND actor arena.
                arena?.Dispose();
                mailbox?.Dispose();
                throw;
            }
        }
    }

    public void Send(ActorRef destination, ReadOnlySpan<byte> payload)
    {
        ActorCell cell = Lookup(destination);

        lock (cell.Gate)
        {
            if (cell.Stopping || cell.Draining || !cell.Mailbox.Authorizes(destination))
                throw new InvalidOperationException(
                    "Actor reference is stale or actor is stopping or draining");
            // Do not start a carrier for a message that cannot enter the
            // mailbox. Send revalidates after scheduling before publication.
            cell.Mailbox.ValidateAdmission(destination, payload.Length);

            // Publish the dispatch token before the message while holding the
            // cell gate. A newly-started carrier blocks on this same gate, so
            // it cannot observe an empty mailbox and lose the wakeup.
            if (cell.Scheduled == 0 && !TryScheduleLocked(cell))
                throw new InvalidOperationException(
                    "Actor dispatch queue cannot admit work");

            cell.Mailbox.Send(destination, payload);
        }
    }

    /// <summary>
    /// Gracefully seal admission for this exact ActorRef incarnation, then
    /// drain all already accepted DATA turns before retiring its arena. This
    /// host primitive is NOT a source-level pipe or a supervisor-controlled
    /// graph-completion guarantee. Pipeline callers must wait for upstream
    /// retirement BEFORE sealing downstream admission. Stop() remains abortive.
    /// </summary>
    public Task DrainAndStop(ActorRef actor)
    {
        ActorCell cell = Lookup(actor);
        Task completion = cell.Terminated.Task; // generation-bound terminal receipt
        bool finalizeNow = false;
        lock (cell.Gate)
        {
            if (cell.Stopping || !cell.Mailbox.Authorizes(actor))
                throw new InvalidOperationException("Actor reference is stale or stopping");
            if (cell.Draining)
                return completion;
            cell.Draining = true;
            if (cell.Scheduled == 0)
            {
                if (cell.Mailbox.QueuedMessages == 0)
                {
                    MarkStoppingLocked(cell); // no pending DATA to discard
                    finalizeNow = true;
                }
                else
                {
                    // Scheduling failure must NOT strand previously admitted
                    // DATA behind a half-published terminal transition.
                    try
                    {
                        if (!TryScheduleLocked(cell))
                            throw new InvalidOperationException(
                                "Actor dispatch queue cannot admit graceful drain");
                    }
                    catch
                    {
                        cell.Draining = false;
                        throw;
                    }
                }
            }
        }
        if (finalizeNow)
            FinalizeCell(cell);
        return completion;
    }

    public void Stop(ActorRef actor)
    {
        ActorCell cell = Lookup(actor);
        bool finalizeNow;

        lock (cell.Gate)
        {
            if (!cell.Mailbox.Authorizes(actor))
                throw new InvalidOperationException("Actor reference is stale");

            MarkStoppingLocked(cell);
            finalizeNow = cell.Scheduled == 0;
        }

        if (finalizeNow)
            FinalizeCell(cell);
    }

    internal Task ObserveTermination(ActorRef actor)
    {
        ActorCell cell = Lookup(actor);
        // Numeric IDs are lookup keys, not observation authority. The immutable
        // generation + mailbox nonce must match the actual runtime-minted ref.
        // Do not use Mailbox.Authorizes here: legitimate observers may wait
        // after Stop has closed admission but before physical finalization.
        if (actor != cell.Reference)
            throw new InvalidOperationException("Actor reference is stale");
        return cell.Terminated.Task;
    }

    // Statically bound capability-provider dispatch. This is the existing
    // bounded mailbox admission, not a socket send or asynchronous offload.
    internal void PostHostMessage(ActorRef actor) => Send(actor, ReadOnlySpan<byte>.Empty);

    public void Dispose()
    {
        if (ReferenceEquals(s_currentRuntime, this) || s_actorOrigin.Value)
            throw new InvalidOperationException(
                "ActorRuntime.Dispose cannot run from an actor carrier or actor-origin offload");

        // Hold across joins and final reclamation, not just the _closed check.
        lock (_disposeGate)
        {
            ActorCell[] cells;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                cells = _actors.Values.ToArray();
            }

            foreach (ActorCell cell in cells)
            {
                bool finalizeNow;
                lock (cell.Gate)
                {
                    MarkStoppingLocked(cell);
                    finalizeNow = cell.Scheduled == 0;
                }
                if (finalizeNow)
                    FinalizeCell(cell);
            }

            _domains.Dispose();

            // All queued/running dispatch tokens have returned at this point.
            foreach (ActorCell cell in cells)
                FinalizeCell(cell);

            _pubSub.Close();
        }
    }

    private ActorCell Lookup(ActorRef actor)
    {
        lock (_gate)
        {
            EnsureOpenLocked();
            if (!_actors.TryGetValue(actor.Id, out ActorCell? cell))
                throw new InvalidOperationException("Actor reference is stale");
            return cell;
        }
    }

    private bool TryScheduleLocked(ActorCell cell)
    {
        if (cell.Scheduled != 0)
            return true;

        cell.Scheduled = 1;
        try
        {
            if (_domains.TryDispatch(cell.Kind, cell.DispatchAction))
                return true;
        }
        catch
        {
            cell.Scheduled = 0;
            throw;
        }

        cell.Scheduled = 0;
        return false;
    }

    private void RunCell(ActorCell cell)
    {
        ActorRuntime? previous = s_currentRuntime;
        bool previousOrigin = s_actorOrigin.Value;
        s_currentRuntime = this;
        s_actorOrigin.Value = true;
        bool finalize = false;

        try
        {
            int processed = 0;
            while (processed < _options.MessagesPerDispatch)
            {
                byte[] payload;
                ActorArenaTurn turn;

                lock (cell.Gate)
                {
                    if (cell.Stopping)
                        break;
                    byte[]? dequeued;
                    try
                    {
                        if (!cell.Mailbox.TryReceive(out dequeued) ||
                            dequeued is null)
                            break;
                    }
                    catch (Exception)
                    {
                        // Native queue delivery may allocate a managed owned
                        // copy. Faulted dequeue must stop the actor instead
                        // of leaking native leases or rescheduling forever.
                        Interlocked.Increment(ref _faultedTurns);
                        MarkStoppingLocked(cell);
                        break;
                    }

                    payload = dequeued;
                    try
                    {
                        turn = cell.Arena.EnterTurn(
                            cell.Reference.Id, cell.Reference.Generation);
                    }
                    catch (Exception)
                    {
                        // The mailbox transferred this buffer to us before
                        // turn admission. Do not strand sensitive bytes on the
                        // exceptional path or allow repeated failed scheduling.
                        CryptographicOperations.ZeroMemory(payload);
                        Interlocked.Increment(ref _faultedTurns);
                        MarkStoppingLocked(cell);
                        break;
                    }
                }

                bool succeeded = false;
                try
                {
                    using (turn)
                    {
                        if (cell.NextExecutionSequence == ulong.MaxValue)
                            throw new InvalidOperationException("Actor invocation identity exhausted");
                        var execution = new ActorExecutionContext(
                            cell.Reference, ++cell.NextExecutionSequence, cell.FrameStorage);
                        execution.Stack.Begin(turn);
                        var context = new ActorContext(
                            cell.Reference, cell.Kind, cell.Arena, turn, execution);
                        try
                        {
                            cell.Handler(context, payload);
                            // Swallowing a quota fault cannot turn this receive
                            // into successful completion of its actor generation.
                            execution.Stack.ValidateSynchronousTurnExit(turn);
                            // Any failed compiled-guest frame operation is terminal for this turn,
                            // even if the handler caught it (#409 guest frame gate).
                            context.ValidateGuestFrameExit();
                        }
                        finally { execution.Stack.Complete(turn); }
                    }
                    succeeded = true;
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _faultedTurns);
                    lock (cell.Gate)
                        MarkStoppingLocked(cell);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                    if (succeeded)
                        Interlocked.Increment(ref _completedMessages);
                }

                processed++;
                if (cell.Stopping)
                    break;
            }
        }
        finally
        {
            try
            {
                lock (cell.Gate)
                {
                    cell.Scheduled = 0;
                    if (cell.Stopping)
                    {
                        finalize = true;
                    }
                    else if (cell.Mailbox.QueuedMessages != 0)
                    {
                        try
                        {
                            if (!TryScheduleLocked(cell))
                            {
                                MarkStoppingLocked(cell);
                                finalize = true;
                            }
                        }
                        catch
                        {
                            MarkStoppingLocked(cell);
                            finalize = true;
                        }
                    }
                    else if (cell.Draining)
                    {
                        // The final turn has returned and no accepted DATA
                        // remain. Only now may Stop scrub/retire the arena.
                        MarkStoppingLocked(cell);
                        finalize = true;
                    }
                }

                if (finalize)
                    FinalizeCell(cell);
            }
            finally
            {
                // Even a failure during dispatch-token teardown must not
                // poison a reusable carrier's thread-static runtime identity.
                s_currentRuntime = previous;
                s_actorOrigin.Value = previousOrigin;
            }
        }
    }

    private void MarkStoppingLocked(ActorCell cell)
    {
        if (cell.Stopping)
            return;

        cell.Stopping = true;
        cell.Mailbox.Stop();
        cell.Arena.Retire();
    }

    private void FinalizeCell(ActorCell cell)
    {
        if (Interlocked.Exchange(ref cell.Finalized, 1) != 0)
            return;

        cell.Mailbox.Stop();
        cell.Arena.Retire();
        cell.Arena.TryReclaim();
        _pubSub.RetireActor(cell.Reference);

        lock (_gate)
        {
            if (_actors.TryGetValue(cell.Reference.Id, out ActorCell? current) &&
                ReferenceEquals(current, cell))
                _actors.Remove(cell.Reference.Id);
        }
        cell.Terminated.TrySetResult();
    }

    private void EnsureOpenLocked()
    {
        if (_closed)
            throw new ObjectDisposedException(nameof(ActorRuntime));
    }
}

