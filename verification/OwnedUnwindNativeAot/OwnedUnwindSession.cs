using System.Runtime.CompilerServices;
using Ores.Net.Runtime;
using G=Atto.Generated.OwnedUnwindGuest;
using F=Atto.Generated.OwnedUnwindGuest.Frame;

// Trusted host bridge for generated quanta. No guest code in producer callbacks.
// Actor lane is supplied by the embedder; no generic task scheduler here.
internal sealed class OwnedUnwindSession
{
    private static readonly object ClaimsGate=new();
    private static readonly ConditionalWeakTable<SelectableI64Future,object> Claims=new();
    private static readonly object ClaimMarker=new();
    private readonly object gate=new();
    private readonly SelectableI64Future[] futures;
    private readonly SelectContinuationFence fence;
    private readonly long owner,epoch;
    private readonly Action<Outcome> publish;
    private readonly Exception?[] errors=new Exception?[8];
    private int errorCount;
    private F frame;
    private SelectContinuationFence.Execution? lifetime;
    private SelectContinuationFence.Ticket? ticket;
    private SelectableI64Future.Subscription? subscription;
    private int slot,kind;
    private long value;
    private bool ready,done,pumping,stop;
    internal readonly record struct Outcome(int Kind,long Value,Exception? Error,Exception? Suppressed,int SuppressedCount);
    private OwnedUnwindSession(SelectableI64Future[] f,SelectContinuationFence fence,long owner,long epoch,Action<Outcome> publish)
    { futures=f;this.fence=fence;this.owner=owner;this.epoch=epoch;this.publish=publish; }
    // Transactional, adapter-local claim domain. Other legacy Future observers
    // are not yet migrated to this domain. Trusted embedder owns identity.
    internal static OwnedUnwindSession Start(SelectableI64Future[] input,SelectContinuationFence fence,long owner,long epoch,Action<Outcome> publish)
    {
        ArgumentNullException.ThrowIfNull(input);ArgumentNullException.ThrowIfNull(fence);ArgumentNullException.ThrowIfNull(publish);
        if(owner<=0||epoch<=0||input.Length!=G.Arity)throw new ArgumentException("E_OWNED_UNWIND_ARITY_IDENTITY");
        var f=(SelectableI64Future[])input.Clone();
        if(f.Any(x=>x is null)||f.Distinct(ReferenceEqualityComparer.Instance).Count()!=f.Length)throw new ArgumentException("E_OWNED_UNWIND_ALIAS");
        var s=new OwnedUnwindSession(f,fence,owner,epoch,publish);
        lock(ClaimsGate){
            if(f.Any(x=>Claims.TryGetValue(x,out _)))throw new InvalidOperationException("E_FUTURE_MOVED");
            int count=0;
            try {
                foreach(var future in f){Claims.Add(future,ClaimMarker);count++;}
                var pin=fence.Register((ulong)epoch);
                try{s.lifetime=fence.TryClaim(pin)??throw new InvalidOperationException("E_GENERATION_PIN");}
                finally{pin.Close();}
            }catch{for(int i=0;i<count;i++)Claims.Remove(f[i]);throw;}
        }
        // From here failure is fail-stop: retain pin and claims, never
        // represent a partially started invocation as quiescent.
        lock(s.gate){
            Require(G.Start(ref s.frame,owner,epoch)==1);
            s.Park();
        }
        return s;
    }
    private static void Require(bool ok){if(!ok)throw new InvalidOperationException("E_OWNED_UNWIND_INVARIANT");}
    private long Receipt(Exception e){
        for(int i=0;i<errorCount;i++)if(ReferenceEquals(errors[i],e))return i+1;
        Require(errorCount<errors.Length);errors[errorCount++]=e;return errorCount;
    }
    private Exception? Error(long id){if(id==0)return null;Require(id>0&&id<=errorCount);return errors[(int)id-1];}
    private void Park(){
        slot=G.Pending(ref frame,owner,epoch);Require(slot>0&&slot<=futures.Length);
        ready=false;ticket=fence.Register((ulong)epoch);
        var local=ticket;
        if(stop){kind=2;value=Receipt(new OperationCanceledException());ready=true;return;}
        var future=futures[slot-1];
        subscription=future.SubscribeReady(sub=>{
            lock(gate){
                if(done||ready||!ReferenceEquals(ticket,local)||!local.Active){sub.Close();return;}
                Require(future.TryCommit(sub,out var result));
                switch(result.Status){
                    case SelectableI64Future.FutureStatus.Value:kind=0;value=result.Value;break;
                    case SelectableI64Future.FutureStatus.Failed:kind=1;value=Receipt(result.Error!);break;
                    case SelectableI64Future.FutureStatus.Cancelled:kind=2;value=Receipt(new OperationCanceledException());break;
                    default:throw new InvalidOperationException("E_FUTURE_PENDING");
                }
                ready=true;
            }
        });
    }
    internal bool Ready {get{lock(gate)return ready&&!done;}}
    internal bool Done {get{lock(gate)return done;}}
    // Explicit host stop requests cancel pending wait and each remaining
    // cleanup await at a later pump; never reclaim from the producer thread.
    // Already-published receipt wins. No wall-clock deadline or general cleanup.
    internal bool RequestStop(){
        lock(gate){
            if(done||stop)return false;
            stop=true;
            if(!ready){subscription?.Close();kind=2;value=Receipt(new OperationCanceledException());ready=true;}
            return true;
        }
    }
    internal bool Pump(){
        lock(gate){
            if(done||pumping||!ready)return false;
            pumping=true;
            try {
                using(var execution=fence.TryClaim(ticket)??throw new InvalidOperationException("E_RESUME_CLAIM")){
                    subscription?.Close();subscription=null;ticket=null;ready=false;
                    Require(G.Complete(ref frame,owner,epoch,slot,kind,value)==0);
                    int next=G.Resume(ref frame,owner,epoch);
                    if(next==2){
                        int outcome=G.Outcome(ref frame,owner,epoch);Require(outcome is >=1 and <=5);
                        long v=0;if(outcome<=2)Require(G.Result(ref frame,owner,epoch,ref v)==0);
                        int n=checked((int)G.SuppressedCount(ref frame,owner,epoch));Require(n>=0&&n<=7);
                        var result=new Outcome(outcome,v,Error(G.Error(ref frame,owner,epoch)),n==0?null:Error(G.SuppressedError(ref frame,owner,epoch,0)),n);
                        Require(G.Retire(ref frame,owner,epoch)==0);done=true;
                        try{publish(result);}finally{Array.Clear(errors);Array.Clear(futures);lifetime!.Dispose();lifetime=null;}
                        return true;
                    }
                    Require(next==1);
                }
                // Release transient claim before registering next ticket;
                // maximum two physical credits for one invocation.
                Park();return true;
            }finally{pumping=false;}
        }
    }
}
