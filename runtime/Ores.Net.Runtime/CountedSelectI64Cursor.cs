namespace Ores.Net.Runtime;

/// Host runtime exhaustion, distinct from a producer failure with the same message.
public sealed class SelectExhaustedException : InvalidOperationException
{
    public SelectExhaustedException() : base("SelectExhausted") { }
}

/// Trusted host-only scalar selection. One non-migrating owner drives
/// registration and commit; producer callbacks only mark their own round dirty.
/// This is not an actor scheduler, guest Future/Array, or source lowering.
public sealed class CountedSelectI64Cursor
{
    public enum CursorState { Ready, Waiting, Success, Cancelled, Failed }
    private sealed class Round(int count)
    {
        public readonly SelectArbiter Arbiter = new(count);
        public readonly List<Func<bool>> Probes = new(count);
        public int Dirty = 1;
        public long Value;
        public bool Eof;
        public Exception? Error;
        public SelectableI64Channel.NotificationBatch? Notifications;
    }
    private readonly Thread _owner = Thread.CurrentThread;
    private readonly int _count;
    private readonly bool _fair;
    private readonly List<object> _live = new();
    private readonly List<long> _values;
    private List<Func<long, long>>? _projectors;
    private int _cursor;
    private bool _driving;
    private Round? _round;
    private CursorState _state;
    private Exception? _failure;

    public CountedSelectI64Cursor(int count, IReadOnlyList<object> sources, bool fair = true,
        IReadOnlyList<Func<long, long>>? projectors = null)
    {
        if (count < 0 || count > 1024) throw new ArgumentOutOfRangeException(nameof(count), "SelectCountInvalid");
        _count = count;
        _fair = fair;
        _values = new(count);
        if (count == 0) { _state = CursorState.Success; return; }
        ArgumentNullException.ThrowIfNull(sources);
        int length = sources.Count;
        if (length < 1 || length > 256) throw new ArgumentException("counted source count");
        for (int i = 0; i < length; i++)
        {
            object source = sources[i];
            if (source is not SelectableI64Channel && source is not SelectableI64Future)
                throw new ArgumentException("counted source requires trusted i64 channel or Future");
            _live.Add(source);
        }
        if (projectors is not null)
        {
            if (projectors.Count != length) throw new ArgumentException("one counted projector per lexical arm required");
            _projectors = new(length);
            for (int i = 0; i < length; i++)
                _projectors.Add(projectors[i] ?? throw new ArgumentException("counted projector cannot be null"));
        }
        _state = CursorState.Ready;
    }
    private void Owner()
    {
        if (!ReferenceEquals(Thread.CurrentThread, _owner) || _driving)
            throw new InvalidOperationException("counted cursor requires serialized host owner");
    }
    public CursorState State { get { Owner(); return _state; } }
    public bool Dirty { get { Owner(); return _round is { } r && Volatile.Read(ref r.Dirty) != 0; } }
    public Exception? Failure { get { Owner(); return _failure; } }

    private void Fail(Exception error)
    {
        _round = null; _live.Clear(); _projectors = null; _values.Clear();
        _failure = error; _state = CursorState.Failed;
    }

    public void BeginRound()
    {
        Owner();
        if (_state != CursorState.Ready) throw new InvalidOperationException("round not ready");
        _driving = true;
        var round = new Round(_live.Count);
        _round = round; _state = CursorState.Waiting;
        try
        {
            for (int i = 0; i < _live.Count; i++)
            {
                var registration = round.Arbiter.RegisterCase(i);
                if (_live[i] is SelectableI64Channel channel)
                {
                    var notifications = channel.CreateNotificationBatch();
                    var sub = channel.SubscribeReadable(_ => Volatile.Write(ref round.Dirty, 1));
                    registration.Arm(() => sub.Close());
                    round.Probes.Add(() => registration.TryWin(() =>
                    {
                        if (!channel.TryCommitRead(sub, out var result, notifications)) return false;
                        round.Value = result.Value;
                        round.Eof = result.Status == SelectableI64Channel.ReadStatus.Eof;
                        round.Notifications = notifications;
                        return true;
                    }));
                }
                else if (_live[i] is SelectableI64Future future)
                {
                    var sub = future.SubscribeReady(_ => Volatile.Write(ref round.Dirty, 1));
                    registration.Arm(() => sub.Close());
                    round.Probes.Add(() => registration.TryWin(() =>
                    {
                        if (!future.TryCommit(sub, out var result)) return false;
                        round.Value = result.Value;
                        if (result.Status != SelectableI64Future.FutureStatus.Value)
                            round.Error = result.Error ?? new OperationCanceledException("selected Future cancelled");
                        return true;
                    }));
                }
            }
        }
        catch (Exception error)
        {
            round.Arbiter.Cancel(); Fail(error); throw;
        }
        finally { _driving = false; }
    }

    // One winner per round. Notification delivery from this commit is
    // drained before the next round; stale producer callbacks can only
    // write the retired round's advisory flag, never commit or resume.
    public bool Pump()
    {
        Owner();
        if (_state != CursorState.Waiting || _round is not { } round)
            throw new InvalidOperationException("no active round");
        _driving = true;
        try
        {
            Volatile.Write(ref round.Dirty, 0);
            int n = _live.Count, start = _fair ? _cursor : 0;
            for (int k = 0; k < n; k++)
            {
                int i = (start + k) % n;
                if (!round.Probes[i]()) continue;
                round.Notifications?.Dispatch();
                round.Arbiter.ThrowIfCleanupFailed();
                _round = null;
                if (round.Error is { } error) { Fail(error); return true; }
                // Invoke only on the authenticated owner after the round has drained.
                if (!round.Eof) _values.Add(_projectors is null ? round.Value : _projectors[i](round.Value));
                var winner = _live[i];
                int next = (i + 1) % n;
                if (round.Eof || winner is SelectableI64Future)
                {
                    int nextIndex = -1;
                    for (int j = 0; j < n; j++)
                    {
                        int index = (next + j) % n;
                        if (!ReferenceEquals(_live[index], winner)) { nextIndex = index; break; }
                    }
                    _cursor = 0;
                    for (int j = 0; j < nextIndex; j++)
                        if (!ReferenceEquals(_live[j], winner)) _cursor++;
                    // Retire all source aliases while preserving lexical projector alignment.
                    for (int j = n - 1; j >= 0; j--)
                        if (ReferenceEquals(_live[j], winner))
                        {
                            _live.RemoveAt(j);
                            _projectors?.RemoveAt(j);
                        }
                }
                else _cursor = next;
                if (_values.Count == _count) { _live.Clear(); _projectors = null; _state = CursorState.Success; }
                else if (_live.Count == 0) Fail(new SelectExhaustedException());
                else _state = CursorState.Ready;
                return true;
            }
            return false;
        }
        catch (Exception error)
        {
            round.Arbiter.Cancel(); Fail(error); throw;
        }
        finally { _driving = false; }
    }

    public bool Cancel()
    {
        Owner();
        if (_state != CursorState.Ready && _state != CursorState.Waiting) return false;
        _driving = true;
        try
        {
            _round?.Arbiter.Cancel();
            _round?.Arbiter.ThrowIfCleanupFailed();
            _round = null; _values.Clear(); _live.Clear(); _projectors = null; _state = CursorState.Cancelled;
            return true;
        }
        catch (Exception error) { Fail(error); throw; }
        finally { _driving = false; }
    }
    public long[] Result() => Snapshot();

    internal long[] Snapshot()
    {
        Owner();
        if (_state != CursorState.Success) throw new InvalidOperationException("batch not successful");
        return _values.ToArray();
    }
}
