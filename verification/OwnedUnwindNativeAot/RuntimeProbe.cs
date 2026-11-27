using Ores.Net.Runtime;
using G=Atto.Generated.OwnedUnwindGuest;
internal static class RuntimeProbe
{
    static void Check(bool ok){if(!ok)throw new InvalidOperationException("runtime probe");}
    static SelectableI64Future[] Inputs()=>Enumerable.Range(0,G.Arity).Select(_=>new SelectableI64Future(1)).ToArray();
    static void Reject(Action a){try{a();}catch(InvalidOperationException){return;}throw new InvalidOperationException("expected rejection");}
    internal static void Run(){
        var f=Inputs();var fence=new SelectContinuationFence(1,2);
        OwnedUnwindSession.Outcome? result=null;
        OwnedUnwindSession? s=null;
        s=OwnedUnwindSession.Start(f,fence,7,1,o=>{
            Check(!fence.Advance(1)&&fence.Executing==2);
            Check(!s!.Pump()&&s.Done);result=o;
        });
        Check(!fence.Advance(1)&&!s.Pump());
        Reject(()=>OwnedUnwindSession.Start(f,new SelectContinuationFence(1,2),8,1,_=>{}));
        int first=G.Arity==2?1:0;
        var err=new InvalidOperationException("original");
        var thread=new Thread(()=>{if(G.Arity==2)Check(f[first].Resolve(0));else Check(f[first].Fail(err));});
        thread.Start();thread.Join();
        Check(s.Ready&&result is null&&f[G.Arity==2?0:1].PendingWaiters==0);
        Check(s.Pump());
        Check(f[G.Arity==2?0:1].PendingWaiters==1);
        Check(f[G.Arity==2?0:1].Resolve(0));Check(s.Pump());
        if(G.Arity==3){Check(result is null&&f[2].PendingWaiters==1);Check(f[2].Resolve(42));Check(s.Pump());}
        Check(result.HasValue&&result.Value.Kind==(G.Arity==2?1:2)&&result.Value.Value==42);
        if(G.Arity==3)Check(ReferenceEquals(result.GetValueOrDefault().Error,err));
        Check(fence.Executing==0&&fence.PendingTickets==0&&fence.Advance(1));
        var stopInputs=Inputs();var stopFence=new SelectContinuationFence(1,2);OwnedUnwindSession.Outcome? cancelled=null;
        var stopped=OwnedUnwindSession.Start(stopInputs,stopFence,7,1,o=>cancelled=o);
        Check(stopped.RequestStop()&&!stopped.RequestStop());
        Check(stopInputs.All(x=>x.PendingWaiters==0));
        for(int i=0;i<G.Arity&&!stopped.Done;i++)Check(stopped.Pump());
        Check(stopped.Done&&cancelled.HasValue&&cancelled.Value.Kind==5&&stopFence.Advance(1));
        Check(stopInputs.All(x=>x.Resolve(99))&&!stopped.Pump());
        var retry=Inputs();var stale=new SelectContinuationFence(2,2);
        Reject(()=>OwnedUnwindSession.Start(retry,stale,7,1,_=>{}));
        var retryFence=new SelectContinuationFence(1,2);var session=OwnedUnwindSession.Start(retry,retryFence,7,1,_=>{});
        Check(session.RequestStop());for(int i=0;i<G.Arity&&!session.Done;i++)Check(session.Pump());Check(session.Done);
        Console.WriteLine("PASS_NET_NATIVEAOT_OWNED_FUTURE_RUNTIME arity="+G.Arity);
    }
}
