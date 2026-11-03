using System;
using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>Opt-in host substrate, not source actor or Native AOT qualification.</summary>
public sealed class SharedReactorPool : IDisposable
{
    public interface IReactor
    {
        // Bounded <=10ms. May enqueue records, never invoke guest callbacks.
        void Poll(bool mayWait);
        // Nonblocking, thread-safe, sticky across poll entry, must not throw.
        void Wake();
    }
    private const int MaxWorkers = 256, Capacity = 1024;
    // SoA. Separate cold handles from state and padded per-worker counters.
    private readonly Thread?[] _threads = new Thread?[MaxWorkers];
    private readonly int[] _states = new int[MaxWorkers * 16];
    private readonly long[] _completed = new long[MaxWorkers * 8];
    private readonly Action?[] _queue = new Action?[Capacity];
    private readonly object _gate = new(), _joinGate = new();
    private readonly IReactor _reactor;
    private readonly int _maximum;
    private int _pollOwner = -1, _head, _size, _started;
    private long _failures;
    private bool _closing;
    [ThreadStatic] private static SharedReactorPool? s_current;

    public SharedReactorPool(IReactor reactor, int initial = 2, int maximum = 2)
    {
        if (reactor is null || initial < 1 || initial > maximum || maximum > MaxWorkers)
            throw new ArgumentException("require 1 <= initial <= maximum <= 256");
        _reactor = reactor; _maximum = maximum;
        try { EnsureWorkers(initial); }
        catch { Dispose(); throw; }
    }
    public void EnsureWorkers(int count)
    {
        lock (_gate)
        {
            if (_closing || count < 1 || count > _maximum)
                throw new ArgumentOutOfRangeException(nameof(count));
            while (_started < count)
            {
                int slot = _started;
                var worker = new Thread(() => Run(slot))
                    { IsBackground = true, Name = "ores-shared-worker-" + slot };
                worker.Start(); _threads[slot] = worker; ++_started;
            }
        }
    }
    public bool TrySubmit(Action work)
    {
        if (work is null) return false;
        lock (_gate)
        {
            if (_closing || _size == Capacity) return false;
            _queue[(_head + _size) % Capacity] = work; ++_size;
            Monitor.Pulse(_gate);
        }
        // Lease acquisition precedes the poller's ready-queue check, so an
        // absent owner means a future poller will observe this submission.
        // Avoid notifying the reactor on every hot-queue admission.
        if (Volatile.Read(ref _pollOwner) != -1) WakeReactor();
        return true;
    }
    private void Run(int slot)
    {
        s_current = this;
        int slices = 0;
        try
        {
            while (true)
            {
                Action? work = null;
                lock (_gate)
                {
                    if (_size != 0)
                    {
                        work = _queue[_head]; _queue[_head] = null;
                        _head = (_head + 1) % Capacity; --_size;
                    }
                    else if (_closing) return;
                }
                if (work is not null)
                {
                    Volatile.Write(ref _states[slot * 16], 2);
                    try { work(); }
                    catch (Exception) { Interlocked.Increment(ref _failures); }
                    finally { Interlocked.Increment(ref _completed[slot * 8]); }
                    if (++slices < 64) continue;
                }
                slices = 0;
                if (Interlocked.CompareExchange(ref _pollOwner, slot, -1) == -1)
                {
                    try
                    {
                        bool mayWait;
                        lock (_gate) mayWait = !_closing && _size == 0;
                        Volatile.Write(ref _states[slot * 16], 3);
                        _reactor.Poll(mayWait);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref _failures);
                        lock (_gate) { _closing = true; Monitor.PulseAll(_gate); }
                    }
                    finally { Volatile.Write(ref _pollOwner, -1); }
                }
                else if (work is null)
                {
                    Volatile.Write(ref _states[slot * 16], 1);
                    lock (_gate)
                        if (!_closing && _size == 0) Monitor.Wait(_gate, 10);
                }
            }
        }
        finally { Volatile.Write(ref _states[slot * 16], 4); s_current = null; }
    }
    public int StartedWorkers { get { lock (_gate) return _started; } }
    public long Completed
    {
        get
        {
            long result = 0;
            for (int i = 0; i < MaxWorkers; ++i) result += Interlocked.Read(ref _completed[i * 8]);
            return result;
        }
    }
    public long Failures => Interlocked.Read(ref _failures);
    private void WakeReactor()
    {
        try { _reactor.Wake(); }
        catch (Exception) { Interlocked.Increment(ref _failures); }
    }
    public void Dispose()
    {
        if (ReferenceEquals(s_current, this))
            throw new InvalidOperationException("cannot join current worker");
        lock (_joinGate)
        {
            int count;
            lock (_gate) { _closing = true; count = _started; Monitor.PulseAll(_gate); }
            WakeReactor();
            for (int i = 0; i < count; ++i) _threads[i]!.Join();
        }
    }
}
