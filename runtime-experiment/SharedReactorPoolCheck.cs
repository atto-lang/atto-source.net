using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Ores.Net.Runtime;

static class Check
{
    [ThreadStatic] private static bool s_polling;
    static void Require(bool value) { if (!value) throw new Exception("shared reactor assertion"); }
    static void Until(Func<bool> predicate)
    {
        var time = Stopwatch.StartNew();
        while (!predicate()) { Require(time.ElapsedMilliseconds < 5000); Thread.Yield(); }
    }
    // A real UDP readiness source; readiness observation never runs work inline.
    sealed class Io : SharedReactorPool.IReactor, IDisposable
    {
        readonly Socket _input = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        readonly Socket _sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        readonly AutoResetEvent _wake = new(false);
        public SharedReactorPool? Pool;
        public int Active, Delivered, Wakes;
        public readonly ConcurrentDictionary<int, bool> Polls = new(), Executions = new();
        int _pending;
        public Io()
        {
            _input.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _input.Blocking = false;
            _sender.Connect(_input.LocalEndPoint!);
        }
        public void Emit() { Require(_sender.Send(new byte[] { 1 }) == 1); }
        public void Wake() { Interlocked.Increment(ref Wakes); _wake.Set(); }
        public void Poll(bool mayWait)
        {
            Require(Interlocked.Increment(ref Active) == 1); s_polling = true;
            Polls.TryAdd(Environment.CurrentManagedThreadId, true);
            try
            {
                if (mayWait && _pending == 0) _wake.WaitOne(10);
                while (_input.Poll(0, SelectMode.SelectRead))
                {
                    Require(_input.Receive(new byte[1]) == 1); ++_pending;
                }
                while (_pending != 0 && Pool is not null)
                {
                    if (!Pool.TrySubmit(() => {
                        Require(!s_polling);
                        Executions.TryAdd(Environment.CurrentManagedThreadId, true);
                        Interlocked.Increment(ref Delivered);
                    })) break;
                    --_pending;
                }
            }
            finally { s_polling = false; Require(Interlocked.Decrement(ref Active) == 0); }
        }
        public void Dispose() { _sender.Dispose(); _input.Dispose(); _wake.Dispose(); }
    }
    public static void Main()
    {
        using (var io = new Io())
        using (var pool = new SharedReactorPool(io, 2, 4))
        {
            io.Pool = pool; Require(pool.StartedWorkers == 2);
            for (int i = 0; i < 100; ++i) { io.Emit(); io.Wake(); }
            Until(() => Volatile.Read(ref io.Delivered) == 100);
            Require(pool.StartedWorkers == 2);
            Require(pool.TrySubmit(() => throw new InvalidOperationException("expected")));
            int denied = 0;
            Require(pool.TrySubmit(() => {
                try { pool.Dispose(); }
                catch (InvalidOperationException) { Interlocked.Increment(ref denied); }
            }));
            Until(() => Volatile.Read(ref denied) == 1 && pool.Failures == 1);
            pool.EnsureWorkers(4); Require(pool.StartedWorkers == 4);
            bool capped = false;
            try { pool.EnsureWorkers(5); } catch (ArgumentOutOfRangeException) { capped = true; }
            Require(capped);
            int done = 0;
            var producers = new Thread[4];
            for (int i = 0; i < producers.Length; ++i)
            {
                producers[i] = new Thread(() => {
                    for (int j = 0; j < 2000; ++j)
                        while (!pool.TrySubmit(() => Interlocked.Increment(ref done))) Thread.Yield();
                });
                producers[i].Start();
            }
            foreach (var producer in producers) producer.Join();
            Until(() => Volatile.Read(ref done) == 8000); pool.Dispose();
            Require(pool.Completed == 8102 && !pool.TrySubmit(() => {}));
            foreach (int id in io.Executions.Keys) io.Polls.TryAdd(id, true);
            Require(io.Polls.Count <= 4);
        }
        using (var io = new Io())
        using (var pool = new SharedReactorPool(io, 1, 1))
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            io.Pool = pool;
            Require(pool.TrySubmit(() => { entered.Set(); Require(release.Wait(5000)); }));
            Require(entered.Wait(5000));
            int done = 0;
            int wakesBeforeHotQueue = Volatile.Read(ref io.Wakes);
            for (int i = 0; i < 1024; ++i) Require(pool.TrySubmit(() => Interlocked.Increment(ref done)));
            Require(Volatile.Read(ref io.Wakes) == wakesBeforeHotQueue); // no polling worker
            Require(!pool.TrySubmit(() => {})); release.Set(); pool.Dispose(); Require(done == 1024);
        }
        using (var io = new Io())
        using (var pool = new SharedReactorPool(io, 1, 1))
        {
            io.Pool = pool;
            int slices = 0;
            Action chain = null!;
            chain = () => {
                if (Interlocked.Increment(ref slices) == 1) { io.Emit(); io.Wake(); }
                if (Volatile.Read(ref io.Delivered) == 0) Require(pool.TrySubmit(chain));
            };
            Require(pool.TrySubmit(chain)); Until(() => Volatile.Read(ref io.Delivered) == 1);
            pool.Dispose(); Require(slices >= 64);
            Require(io.Polls.Count == 1 && io.Executions.Count == 1);
            foreach (int id in io.Executions.Keys) Require(io.Polls.ContainsKey(id));
        }
        using (var io = new Io())
        {
            foreach (var limits in new[] { (0, 2), (-1, 2), (3, 2), (1, 257) })
            {
                bool denied = false;
                try { new SharedReactorPool(io, limits.Item1, limits.Item2).Dispose(); }
                catch (ArgumentException) { denied = true; }
                Require(denied);
            }
        }
        Console.WriteLine("shared-reactor host: default-two, single-poller, UDP readiness, shared execution, " +
            "bounded queue, 8000 concurrent submissions, growth cap, self-join denial, drain PASS");
    }
}
