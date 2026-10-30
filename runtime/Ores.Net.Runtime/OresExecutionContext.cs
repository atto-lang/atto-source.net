namespace Ores.Net.Runtime;

public enum OresCallbackBoundary : byte
{
    HostCallback = 1,
    Io = 2,
    Timer = 3,
    Await = 4,
    ActorMessage = 5,
    SupervisorControl = 6,
    TaskJoin = 7,
    Spawn = 8
}

public readonly struct OresExecutionContext
{
    public OresExecutionContext(
        ulong runtimeDomain,
        ulong execution,
        ulong dispatch,
        ulong generation,
        ulong parentExecution = 0,
        ulong continuation = 0)
    {
        if (runtimeDomain == 0) throw new ArgumentOutOfRangeException(nameof(runtimeDomain));
        if (execution == 0) throw new ArgumentOutOfRangeException(nameof(execution));
        if (dispatch == 0) throw new ArgumentOutOfRangeException(nameof(dispatch));
        if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));

        RuntimeDomain = runtimeDomain;
        Execution = execution;
        Dispatch = dispatch;
        Generation = generation;
        ParentExecution = parentExecution;
        Continuation = continuation;
    }

    public ulong RuntimeDomain { get; }
    public ulong Execution { get; }
    public ulong Dispatch { get; }
    public ulong Generation { get; }
    public ulong ParentExecution { get; }
    public ulong Continuation { get; }
}

public readonly struct OresExecutionCallbackContext
{
    internal OresExecutionCallbackContext(
        OresExecutionContext source,
        OresCallbackBoundary boundary)
    {
        RuntimeDomain = source.RuntimeDomain;
        Execution = source.Execution;
        Dispatch = source.Dispatch;
        Generation = source.Generation;
        ParentExecution = source.ParentExecution;
        Continuation = source.Continuation;
        Boundary = boundary;
    }

    internal OresExecutionCallbackContext(
        OresExecutionCallbackContext source,
        OresCallbackBoundary boundary)
    {
        RuntimeDomain = source.RuntimeDomain;
        Execution = source.Execution;
        Dispatch = source.Dispatch;
        Generation = source.Generation;
        ParentExecution = source.ParentExecution;
        Continuation = source.Continuation;
        Boundary = boundary;
    }

    public ulong RuntimeDomain { get; }
    public ulong Execution { get; }
    public ulong Dispatch { get; }
    public ulong Generation { get; }
    public ulong ParentExecution { get; }
    public ulong Continuation { get; }
    public OresCallbackBoundary Boundary { get; }
}

/// <summary>
/// Oreslang logical execution and callback provenance. Both are explicit,
/// synchronous scopes. They intentionally use ThreadStatic rather than
/// AsyncLocal so CLR ExecutionContext flow cannot silently grant Oreslang
/// execution identity to Task/ThreadPool callbacks.
/// </summary>
public static class OresExecutionContexts
{
    [ThreadStatic]
    private static OresExecutionContext? s_active;

    [ThreadStatic]
    private static OresExecutionCallbackContext? s_callback;

    [ThreadStatic]
    private static ulong s_activeScopeSequence;

    [ThreadStatic]
    private static ulong s_activeScopeTop;

    [ThreadStatic]
    private static ulong s_callbackScopeSequence;

    [ThreadStatic]
    private static ulong s_callbackScopeTop;

    public static OresExecutionContext? Active => s_active;
    public static OresExecutionCallbackContext? Callback => s_callback;

    public static ActiveScope EnterActive(OresExecutionContext context) =>
        new(context);

    public static CallbackScope EnterCallback(OresExecutionCallbackContext context) =>
        new(context);

    public static OresExecutionCallbackContext? CaptureCallback(
        OresCallbackBoundary boundary)
    {
        ValidateBoundary(boundary);
        if (s_active is OresExecutionContext active)
            return new OresExecutionCallbackContext(active, boundary);
        if (s_callback is OresExecutionCallbackContext callback)
            return new OresExecutionCallbackContext(callback, boundary);
        return null;
    }

    public static void RunCallback(
        OresExecutionCallbackContext? context,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (context is not OresExecutionCallbackContext captured)
        {
            action();
            return;
        }

        var scope = EnterCallback(captured);
        try
        {
            action();
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static void ValidateBoundary(OresCallbackBoundary boundary)
    {
        if (boundary < OresCallbackBoundary.HostCallback ||
            boundary > OresCallbackBoundary.Spawn)
            throw new ArgumentOutOfRangeException(nameof(boundary));
    }

    public ref struct ActiveScope
    {
        private readonly OresExecutionContext? _previous;
        private readonly int _ownerThreadId;
        private readonly ulong _token;
        private readonly ulong _previousTop;
        private bool _disposed;

        internal ActiveScope(OresExecutionContext current)
        {
            if (s_activeScopeSequence == ulong.MaxValue)
                throw new InvalidOperationException("active execution scope token exhausted");

            _previous = s_active;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _previousTop = s_activeScopeTop;
            _token = ++s_activeScopeSequence;
            _disposed = false;
            s_active = current;
            s_activeScopeTop = _token;
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException(
                    "active execution scope must close on its installing carrier");
            if (s_activeScopeTop != _token)
                throw new InvalidOperationException(
                    "active execution scopes must close in strict LIFO order");

            s_active = _previous;
            s_activeScopeTop = _previousTop;
            _disposed = true;
        }
    }

    public ref struct CallbackScope
    {
        private readonly OresExecutionCallbackContext? _previous;
        private readonly int _ownerThreadId;
        private readonly ulong _token;
        private readonly ulong _previousTop;
        private bool _disposed;

        internal CallbackScope(OresExecutionCallbackContext current)
        {
            ValidateBoundary(current.Boundary);
            if (s_callbackScopeSequence == ulong.MaxValue)
                throw new InvalidOperationException("callback scope token exhausted");

            _previous = s_callback;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _previousTop = s_callbackScopeTop;
            _token = ++s_callbackScopeSequence;
            _disposed = false;
            s_callback = current;
            s_callbackScopeTop = _token;
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException(
                    "callback scope must close on its installing carrier");
            if (s_callbackScopeTop != _token)
                throw new InvalidOperationException(
                    "callback scopes must close in strict LIFO order");

            s_callback = _previous;
            s_callbackScopeTop = _previousTop;
            _disposed = true;
        }
    }
}
