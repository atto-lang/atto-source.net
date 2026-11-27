using Ores.Net.Runtime;
using System.Threading;

internal static class ActorSpawnQuotaGateSelfTest
{
    private static void Require(bool ok) { if (!ok) throw new InvalidOperationException("spawn quota invariant"); }
    private static void Deny(Action a, string code)
    {
        try { a(); } catch (InvalidOperationException e) when(e.Message==code) { return; }
        throw new InvalidOperationException("expected "+code);
    }
    public static void Run()
    {
        var unit=new ActorSpawnQuotaGate.Charge(1,1,1,1);
        var g=new ActorSpawnQuotaGate(unit,1,2);
        var other=new ActorSpawnQuotaGate(unit,1,2);
        var p=g.Reserve(unit);
        Require(g.TryEnterReceive(p)==null);
        Deny(()=>other.BeginInit(p),"E_SPAWN_STALE_PERMIT");
        var init=g.BeginInit(p);
        Deny(()=>g.BeginInit(p),"E_SPAWN_INIT_AUTHORITY");
        Require(g.CompleteInit(init,true));
        Deny(()=>g.CompleteInit(init,true),"E_SPAWN_INIT_AUTHORITY");
        var a=g.TryEnterReceive(p)!.Value; var b=g.TryEnterReceive(p)!.Value;
        Deny(()=>g.TryEnterReceive(p),"E_SPAWN_FULL");
        g.Retire(p); Require(g.TryEnterReceive(p)==null);
        Deny(()=>g.ReleaseAfterQuiescence(p),"E_SPAWN_IN_FLIGHT");
        g.FinishReceive(a);
        Deny(()=>g.FinishReceive(a),"E_SPAWN_STALE_PERMIT");
        Deny(()=>g.ReleaseAfterQuiescence(p),"E_SPAWN_IN_FLIGHT");
        g.FinishReceive(b); g.ReleaseAfterQuiescence(p);
        Require(g.Usage==default);
        var q=g.Reserve(unit); Require(q.Generation>p.Generation);
        Deny(()=>g.Retire(p),"E_SPAWN_STALE_PERMIT");
        var qi=g.BeginInit(q); g.Retire(q);
        Deny(()=>g.ReleaseAfterQuiescence(q),"E_SPAWN_IN_FLIGHT");
        Require(!g.CompleteInit(qi,true)); g.ReleaseAfterQuiescence(q);
        Deny(()=>g.Reserve(default),"E_SPAWN_QUOTA");
        foreach(var charge in new ActorSpawnQuotaGate.Charge[] {new(2,1,1,1),new(1,2,1,1),new(1,1,2,1),new(1,1,1,2)})
            Deny(()=>g.Reserve(charge),"E_SPAWN_QUOTA");
        for(int i=0;i<100_000;i++)
        {
            var permit=g.Reserve(unit);
            Require(!g.CompleteInit(g.BeginInit(permit),false));
            g.ReleaseAfterQuiescence(permit);
        }
        int winners=0;
        // Hold the winner until every thread has attempted, so no sequential
        // reuse can make the concurrency assertion pass accidentally.
        using var start=new Barrier(16);
        using var attempted=new Barrier(16);
        var errors=new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var threads=Enumerable.Range(0,16).Select(_=>new Thread(()=>{
            ActorSpawnQuotaGate.Permit? permit=null;
            try
            {
                start.SignalAndWait();
                try { permit=g.Reserve(unit); Interlocked.Increment(ref winners); }
                catch(InvalidOperationException e) when(e.Message=="E_SPAWN_QUOTA") { }
            } catch(Exception e) { errors.Enqueue(e); }
            finally { attempted.SignalAndWait(); }
            try { if(permit is {} x) { g.Retire(x); g.ReleaseAfterQuiescence(x); } }
            catch(Exception e) { errors.Enqueue(e); }
        })).ToArray();
        foreach(var thread in threads) thread.Start();
        foreach(var thread in threads) Require(thread.Join(10_000));
        Require(errors.IsEmpty && winners==1 && g.Usage==default);
        Console.WriteLine("NET_SPAWN_QUOTA_HOST_PASS|rollback=100000|contenders=16|guest=0");
    }
}
