namespace Ores.Net.Runtime;

/// Host-only aggregate task. The trusted serialized owner pumps the actual
/// scalar arbiter; no actor scheduling or guest Array<T> lowering is implied.
public sealed class CountedSelectI64Batch
{
    private readonly Thread _owner = Thread.CurrentThread;
    private readonly CountedSelectI64Cursor _cursor;
    private readonly TaskCompletionSource<IReadOnlyList<long>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _driving;

    public CountedSelectI64Batch(int count, IReadOnlyList<object> sources, bool fair = true)
    {
        _cursor = new(count, sources, fair);
        Publish();
    }

    public Task<IReadOnlyList<long>> Completion => _completion.Task;

    private void Enter()
    {
        if (!ReferenceEquals(Thread.CurrentThread, _owner) || _driving)
            throw new InvalidOperationException("counted batch requires serialized host owner");
        _driving = true;
    }

    public bool Pump()
    {
        Enter();
        try
        {
            if (Completion.IsCompleted) return false;
            bool progress;
            try
            {
                switch (_cursor.State)
                {
                    case CountedSelectI64Cursor.CursorState.Ready:
                        _cursor.BeginRound(); progress = true; break;
                    case CountedSelectI64Cursor.CursorState.Waiting:
                        progress = _cursor.Pump(); break;
                    default:
                        progress = true; break;
                }
            }
            catch (Exception)
            {
                if (_cursor.State != CountedSelectI64Cursor.CursorState.Failed) throw;
                progress = true;
            }
            Publish();
            return progress;
        }
        finally { _driving = false; }
    }

    public bool Cancel()
    {
        Enter();
        try
        {
            if (Completion.IsCompleted) return false;
            bool accepted;
            try { accepted = _cursor.Cancel(); }
            catch (Exception)
            {
                if (_cursor.State != CountedSelectI64Cursor.CursorState.Failed) throw;
                accepted = true;
            }
            Publish();
            return accepted;
        }
        finally { _driving = false; }
    }

    private void Publish()
    {
        switch (_cursor.State)
        {
            case CountedSelectI64Cursor.CursorState.Success:
                _completion.TrySetResult(Array.AsReadOnly(_cursor.Result())); break;
            case CountedSelectI64Cursor.CursorState.Cancelled:
                _completion.TrySetCanceled(); break;
            case CountedSelectI64Cursor.CursorState.Failed:
                _completion.TrySetException(_cursor.Failure!); break;
        }
    }
}
