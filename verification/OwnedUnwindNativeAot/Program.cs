using Atto.Generated;
using G=Atto.Generated.OwnedUnwindGuest;
using F=Atto.Generated.OwnedUnwindGuest.Frame;
using System.Runtime.CompilerServices;

static void Check(bool ok){if(!ok)throw new InvalidOperationException("owned unwind assertion");}
static void Wake(ref F f,int slot,int kind,long value,int next){
    Check(G.Pending(ref f,7,1)==slot&&G.Resume(ref f,7,1)==-1&&G.Retire(ref f,7,1)==-1);
    Check(G.Complete(ref f,8,1,slot,kind,value)==-1);
    Check(G.Complete(ref f,7,2,slot,kind,value)==-1);
    Check(G.Complete(ref f,7,1,slot==1?2:1,kind,value)==-1);
    Check(G.Complete(ref f,7,1,slot,3,value)==-1);
    Check(G.Complete(ref f,7,1,slot,1,0)==-1);
    Check(G.Complete(ref f,7,1,slot,kind,value)==0);
    Check(G.Complete(ref f,7,1,slot,kind,value)==-1);
    long result=999;
    Check(G.Pending(ref f,7,1)==-1&&G.Result(ref f,7,1,ref result)==-1&&result==999);
    Check(G.Resume(ref f,7,1)==next);
}
static void Finish(ref F f,int kind,long value,long error=0,long suppressed=0,int rank=0){
    long result=999;
    Check(G.Outcome(ref f,7,1)==kind);
    Check(kind<=2?G.Result(ref f,7,1,ref result)==0&&result==value:G.Result(ref f,7,1,ref result)==-1&&result==999);
    Check(G.Error(ref f,7,1)==error&&G.SuppressedCount(ref f,7,1)==(suppressed==0?0:1));
    if(suppressed!=0)Check(G.SuppressedError(ref f,7,1,0)==suppressed&&G.SuppressedRank(ref f,7,1,0)==rank);
    Check(G.SuppressedError(ref f,7,1,-1)==-1);
    Check(G.Retire(ref f,7,1)==0&&G.Retire(ref f,7,1)==-1&&G.Start(ref f,7,1)==-1);
    Check(G.Outcome(ref f,7,1)==-1&&G.Complete(ref f,7,1,1,0,99)==-1);
    for(int i=0;i<26;i++)Check(f[i]==(i==2?33:0));
}
static void Scenario(){
    F a=default,b=default;
    Check(G.Start(ref a,0,1)==-1&&G.Start(ref a,7,1)==1&&G.Start(ref a,7,1)==-1);
    Check(G.Start(ref b,7,1)==1);
    if(G.Arity==2){
        Wake(ref a,2,0,7,1);Check(G.Pending(ref b,7,1)==2);
        Wake(ref b,2,2,11,1);Wake(ref a,1,0,9,2);Finish(ref a,1,42);
        Wake(ref b,1,1,22,2);Finish(ref b,5,0,11,22,2);
    }else{
        Check(G.Arity==3);
        Wake(ref a,1,1,11,1);Check(G.Pending(ref b,7,1)==1);
        Wake(ref b,1,0,-42,1);Wake(ref a,2,0,0,1);Wake(ref b,2,0,0,2);Finish(ref b,1,-42);
        Wake(ref a,3,0,42,2);Finish(ref a,2,42,11);
        for(int k=0;k<3;k++){
            F f=default;Check(G.Start(ref f,7,1)==1);
            if(k==0){Wake(ref f,1,2,11,1);Wake(ref f,2,0,0,2);Finish(ref f,5,0,11);}
            if(k==1){Wake(ref f,1,1,11,1);Wake(ref f,2,1,22,2);Finish(ref f,4,0,22,11,1);}
            if(k==2){Wake(ref f,1,1,11,1);Wake(ref f,2,0,0,1);Wake(ref f,3,1,33,2);Finish(ref f,4,0,33,11,1);}
        }
    }
}
Check(!RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled);
Check(Unsafe.SizeOf<F>()==208&&G.Words==26);
Scenario();
long before=GC.GetAllocatedBytesForCurrentThread();
for(int i=0;i<10000;i++)Scenario();
long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
Check(allocated==0);
RuntimeProbe.Run();
Console.WriteLine($"PASS_NET_NATIVEAOT_OWNED_UNWIND arity={G.Arity} frame=208 iterations=10000 allocated={allocated}");
