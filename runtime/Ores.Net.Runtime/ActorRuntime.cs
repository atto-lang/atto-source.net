using System.Security.Cryptography;
using System.Threading;

namespace Ores.Net.Runtime;

public delegate void ActorMessageHandler(
    ActorContext context, ReadOnlyMemory<byte> message);

/// <summary>
/// Host-substrate I/O continuation: executes on the actor's existing bounded
/// carrier domain, with the same non-reentrant actor turn as mailbox DATA.
/// No new carrier, guest closure, synthetic mailbox message or CONTROL lane.
/// </summary>
public delegate void ActorIoReadyHandler(ActorContext context);

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
    public int ActorPersistentBytes { get; init; } = 1024 * 1024;
    public int ActorScratchBytes { get; init; } = 64 * 1024;
    // 0 chooses a bounded default (one quarter of the actor persistent arena,
    // capped at 64 KiB). All frames share that actor's existing native budget.
    public int ActorStackBytes { get; init; } = 0;
    public int ActorMaxStackFrames { get; init; } = 256;
    public int ActorMaxAllocations { get; init; } = 4096;
    public int ActorMaxLifetimePins { get; init; } = 4096;
    public int ActorMaxReadLoans { get; init; } = 4096;
    public long NativeBudgetBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxIoQueuedBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxIoQueuedRecords { get; init; } = 65_536;
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
        if (MaxIoQueuedBytes <= 0 || MaxIoQueuedBytes > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxIoQueuedBytes));
        if (MaxIoQueuedRecords <= 0 || MaxIoQueuedRecords > 65_536)
            throw new ArgumentOutOfRangeException(nameof(MaxIoQueuedRecords));

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
    private readonly ActorFrameStack _stack;
    private readonly ActorIoEnvelope _io;

    internal ActorContext(ActorRef self, ActorKind kind,
        ActorArena arena, ActorArenaTurn turn, ActorFrameStack stack,
        ActorIoEnvelope io)
    {
        Self = self;
        Kind = kind;
        _arena = arena;
        _turn = turn;
        _stack = stack;
        _io = io;
    }

    public ActorRef Self { get; }
    public ActorKind Kind { get; }

    // Actor-side directional I/O. Both operations require a currently
    // executing physical actor turn, never just a retained ActorRef.
    // This is host runtime integration, not guest io.* compiler lowering.
    public bool TryReadInput(out byte[]? data)
    {
        ValidateActiveTurn();
        return _io.TryReadInput(Self, out data);
    }

    public bool TryWriteOutput(ReadOnlySpan<byte> data)
    {
        ValidateActiveTurn();
        return _io.TryWriteOutput(Self, data);
    }

    public bool InputEof
    {
        get
        {
            ValidateActiveTurn();
            return _io.InputEof(Self);
        }
    }

    public void CompleteOutput()
    {
        ValidateActiveTurn();
        _io.CompleteOutput(Self);
    }

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

    private readonly object _gate = new();
    // A second Dispose caller must wait for worker joining and reclamation,
    // not merely observe the _closed admission flag and return prematurely.
    private readonly object _disposeGate = new();
    private readonly Dictionary<ulong, ActorCell> _actors = new();
    private readonly ActorRuntimeOptions _options;
    private readonly ActorArenaBudget _nativeBudget;
    private readonly ActorIoBudget _ioBudget;
    private readonly ActorDispatchDomains _domains;

    private ulong _nextActorId;
    private bool _closed;
    private long _completedMessages;
    private long _completedIoTurns;
    private long _faultedTurns;

    private sealed class ActorCell
    {
        private readonly ActorRuntime _owner;

        internal ActorCell(ActorRuntime owner, ActorKind kind,
            ActorMailbox mailbox, ActorArena arena, ActorMessageHandler handler,
            int stackBytes, int maxStackFrames, ActorIoBudget ioBudget,
            ActorIoReadyHandler? ioReadyHandler)
        {
            _owner = owner;
            Kind = kind;
            Mailbox = mailbox;
            Arena = arena;
            Stack = new ActorFrameStack(arena, stackBytes, maxStackFrames);
            Handler = handler;
            IoReadyHandler = ioReadyHandler;
            Reference = mailbox.CurrentRef;
            Io = new ActorIoEnvelope(mailbox, ioBudget);
            HostIo = new ActorIoHostPort(Io, Reference,
                ioReadyHandler is null ? null : owner);
            DispatchAction = Dispatch;
        }

        internal object Gate { get; } = new();
        internal ActorKind Kind { get; }
        internal ActorMailbox Mailbox { get; }
        internal ActorArena Arena { get; }
        internal ActorFrameStack Stack { get; }
        internal ActorIoEnvelope Io { get; }
        internal ActorIoHostPort HostIo { get; }
        internal ActorMessageHandler Handler { get; }
        internal ActorIoReadyHandler? IoReadyHandler { get; }
        internal ActorRef Reference { get; }
        internal Action DispatchAction { get; }
        internal int Scheduled;
        // Each accepted io.in record and first EOF posts one pending readiness
        // turn, independently of ordinary mailbox DATA credit. Per-actor
        // input records are bounded, and the drain alternates with mailbox.
        internal int PendingIoSignals;
        internal bool PreferIo;
        internal bool Stopping;
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
        _ioBudget = new ActorIoBudget(options.MaxIoQueuedBytes, options.MaxIoQueuedRecords);
        _domains = new ActorDispatchDomains(options.Dispatcher);
    }

    public int LiveActors
    {
        get { lock (_gate) return _actors.Count; }
    }

    public long NativeReservedBytes => _nativeBudget.ReservedBytes;
    public int IoQueuedBytes => _ioBudget.QueuedBytes;
    public int IoQueuedRecords => _ioBudget.QueuedRecords;
    public long CompletedMessages => Interlocked.Read(ref _completedMessages);
    public long CompletedIoTurns => Interlocked.Read(ref _completedIoTurns);
    public long FaultedTurns => Interlocked.Read(ref _faultedTurns);
    public int StartedCarrierThreads => _domains.TotalStartedWorkers;

    public ActorRef Spawn(ActorKind kind, ActorMessageHandler handler) =>
        SpawnCore(kind, handler, null);

    private ActorRef SpawnCore(ActorKind kind, ActorMessageHandler handler,
        ActorIoReadyHandler? ioReadyHandler)
    {
        ArgumentNullException.ThrowIfNull(handler);
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
            try
            {
                var mailbox = new ActorMailbox(candidateId,
                    _options.MailboxMessages, _options.MailboxBytes);
                ActorRef actorRef = mailbox.CurrentRef;
                arena = new ActorArena(
                    candidateId,
                    actorRef.Generation,
                    _options.ActorPersistentBytes,
                    _options.ActorScratchBytes,
                    _options.ActorMaxAllocations,
                    _options.ActorMaxLifetimePins,
                    _options.ActorMaxReadLoans,
                    _nativeBudget);

                int stackBytes = _options.ActorStackBytes == 0
                    ? Math.Min(64 * 1024, Math.Max(1, _options.ActorPersistentBytes / 4))
                    : _options.ActorStackBytes;
                var cell = new ActorCell(this, kind, mailbox, arena, handler,
                    stackBytes, _options.ActorMaxStackFrames, _ioBudget, ioReadyHandler);
                _actors.Add(candidateId, cell);
                _nextActorId = candidateId;
                return cell.Reference;
            }
            catch
            {
                arena?.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Mint a separate directional host I/O capability at actor creation.
    /// Merely knowing the ActorRef does not grant either host-side channel.
    /// Without an I/O-ready callback, the legacy host must explicitly
    /// send a mailbox wakeup; the new overload supports direct I/O signaling.
    /// </summary>
    public (ActorRef Actor, ActorIoHostPort HostIo) SpawnWithIo(
        ActorKind kind, ActorMessageHandler handler) =>
        SpawnWithIoCore(kind, handler, null);

    /// <summary>
    /// Bind a separate I/O readiness continuation. Host input and initial EOF
    /// automatically schedule the owning actor without consuming DATA mailbox
    /// credits. I/O is never an extra reentrant turn or a CONTROL action.
    /// </summary>
    public (ActorRef Actor, ActorIoHostPort HostIo) SpawnWithIo(
        ActorKind kind, ActorMessageHandler handler,
        ActorIoReadyHandler onInputReady)
    {
        ArgumentNullException.ThrowIfNull(onInputReady);
        return SpawnWithIoCore(kind, handler, onInputReady);
    }

    private (ActorRef Actor, ActorIoHostPort HostIo) SpawnWithIoCore(
        ActorKind kind, ActorMessageHandler handler,
        ActorIoReadyHandler? onInputReady)
    {
        var actor = SpawnCore(kind, handler, onInputReady);
        var cell = Lookup(actor);
        return (actor, cell.HostIo);
    }

    /// <summary>
    /// Publish an already-admitted, bounded input-record/EOF readiness event.
    /// This is separate from the user mailbox and the trusted STOP control.
    /// A stale, stopped or already-finalized destination is ignored. Never
    /// retain a carrier waiting for input; a scheduled token owns the wakeup.
    /// </summary>
    internal void NotifyInputReady(ActorRef actor)
    {
        ActorCell cell;
        lock (_gate)
        {
            if (_closed || !_actors.TryGetValue(actor.Id, out var found) ||
                found.Reference != actor || found.IoReadyHandler is null)
                return;
            cell = found;
        }

        bool finalize = false;
        lock (cell.Gate)
        {
            if (cell.Stopping) return;
            // Coalesce under pressure. There is always at least one pending
            // readiness token while any accepted signal has not run, so this
            // cannot lose the wakeup. Repeated EOF is ignored at source.
            if (cell.PendingIoSignals < ActorIoEnvelope.MaxRecordsPerChannel + 1)
                ++cell.PendingIoSignals;
            if (cell.Scheduled != 0) return;
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

        if (finalize)
        {
            FinalizeCell(cell);
            throw new InvalidOperationException(
                "I/O readiness dispatch failed; actor was safely retired");
        }
    }

    public void Send(ActorRef destination, ReadOnlySpan<byte> payload)
    {
        ActorCell cell = Lookup(destination);

        lock (cell.Gate)
        {
            if (cell.Stopping || !cell.Mailbox.Authorizes(destination))
                throw new InvalidOperationException(
                    "Actor reference is stale or actor is stopping");
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
        if (ReferenceEquals(s_currentRuntime, this))
            throw new InvalidOperationException(
                "ActorRuntime.Dispose cannot run from one of its actor carriers");

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
        s_currentRuntime = this;
        bool finalize = false;

        try
        {
            int processed = 0;
            while (processed < _options.MessagesPerDispatch)
            {
                byte[] payload = Array.Empty<byte>();
                bool ioReadyTurn;
                ActorArenaTurn turn;

                lock (cell.Gate)
                {
                    if (cell.Stopping)
                        break;

                    // A single scheduled token serializes both inputs. Alternate
                    // if both queues are ready; NEVER consume user mailbox bytes
                    // to manufacture an I/O wakeup, or treat I/O as CONTROL.
                    bool mailReady = cell.Mailbox.QueuedMessages != 0;
                    if (cell.IoReadyHandler is not null &&
                        cell.PendingIoSignals != 0 &&
                        (cell.PreferIo || !mailReady))
                    {
                        --cell.PendingIoSignals;
                        ioReadyTurn = true;
                        cell.PreferIo = false;
                    }
                    else if (cell.Mailbox.TryReceive(out byte[]? dequeued) &&
                        dequeued is not null)
                    {
                        payload = dequeued;
                        ioReadyTurn = false;
                        cell.PreferIo = true;
                    }
                    else if (cell.IoReadyHandler is not null &&
                        cell.PendingIoSignals != 0)
                    {
                        --cell.PendingIoSignals;
                        ioReadyTurn = true;
                        cell.PreferIo = false;
                    }
                    else
                    {
                        break;
                    }

                    try
                    {
                        turn = cell.Arena.EnterTurn(
                            cell.Reference.Id, cell.Reference.Generation);
                    }
                    catch (Exception)
                    {
                        // Mailbox ownership was transferred at dequeue; the I/O
                        // signal owns no buffer. Failed admission retires the
                        // actor and scrubs any pending I/O at finalization.
                        if (!ioReadyTurn)
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
                        var context = new ActorContext(
                            cell.Reference, cell.Kind, cell.Arena, turn, cell.Stack,
                            cell.Io);
                        if (ioReadyTurn)
                            cell.IoReadyHandler!(context);
                        else
                            cell.Handler(context, payload);
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
                    if (!ioReadyTurn)
                        CryptographicOperations.ZeroMemory(payload);
                    if (succeeded)
                    {
                        if (ioReadyTurn)
                            Interlocked.Increment(ref _completedIoTurns);
                        else
                            Interlocked.Increment(ref _completedMessages);
                    }
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
                    else if (cell.Mailbox.QueuedMessages != 0 ||
                        cell.PendingIoSignals != 0)
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
                }

                if (finalize)
                    FinalizeCell(cell);
            }
            finally
            {
                // Even a failure during dispatch-token teardown must not
                // poison a reusable carrier's thread-static runtime identity.
                s_currentRuntime = previous;
            }
        }
    }

    private void MarkStoppingLocked(ActorCell cell)
    {
        if (cell.Stopping)
            return;

        cell.Stopping = true;
        // Trusted CONTROL always wins: pending readiness is discarded without
        // waiting for mailbox slots, and physical I/O scrub remains deferred
        // until all in-flight actor turns have quiesced.
        cell.PendingIoSignals = 0;
        cell.Mailbox.Stop();
        cell.Arena.Retire();
    }

    private void FinalizeCell(ActorCell cell)
    {
        if (Interlocked.Exchange(ref cell.Finalized, 1) != 0)
            return;

        cell.Mailbox.Stop();
        // Only the physical finalizer disposes I/O: in-flight actor turns
        // must finish before their private DATA buffers can be scrubbed.
        cell.Io.Dispose();
        cell.Arena.Retire();
        cell.Arena.TryReclaim();

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

