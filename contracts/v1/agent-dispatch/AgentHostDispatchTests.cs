// Test-only NativeAOT application: no dynamic reflection, JIT or Oreslang guest claim.
using System;
using System.Linq;
using System.Threading.Tasks;
using Oreslang.AgentDispatchHost;
static class Program {
    static Scope Req()=>new("tenant","worker",7,9,new string('a',40),
        "dispatch",new string('b',64),10000,1024,24);
    static AgentHostDispatchGate Gate()=>new("tenant","worker",7,9,new string('a',40),100);
    static void Check(bool good){if(!good)throw new Exception("agent host contract failed");}
    static TrustedHost Host(Func<Scope,bool>? current=null,Func<Scope,bool>? authorize=null,
         Func<Scope,bool>? claim=null,Func<Scope,bool>? send=null)=>new() {
        IsCurrent=current??(_=>true),Authorize=authorize??(_=>true),
        ClaimOnce=claim??(_=>true),SubmitDataOnly=send??(_=>true)
    };
    static async Task Main() {
        int sent=0, claims=0;
        var gate=Gate();
        var h=Host(claim:_=>{claims++;return claims==1;},send:_=>{sent++;return true;});
        var results=await Task.WhenAll(Enumerable.Range(0,16).Select(_=>
            Task.Run(()=>gate.Dispatch(Req(),h,9000))));
        Check(results.Count(x=>x)==1 && claims==1 && sent==1);
        Check(!Gate().Dispatch(Req() with {TenantId="other"},Host(),9000));
        Check(!Gate().Dispatch(Req() with {Generation=8},Host(),9000));
        Check(!Gate().Dispatch(Req() with {Epoch=10},Host(),9000));
        Check(!Gate().Dispatch(Req() with {Revision=new string('c',40)},Host(),9000));
        Check(!Gate().Dispatch(Req() with {ContextSha256=new string('X',64)},Host(),9000));
        Check(!Gate().Dispatch(Req() with {ExpiresAtMs=9000},Host(),9000));
        Check(!Gate().Dispatch(Req() with {MeasuredTokens=101},Host(),9000));
        Check(!Gate().Dispatch(Req() with {Utf8Bytes=65537},Host(),9000));
        sent=0;
        Check(!Gate().Dispatch(Req(),Host(authorize:_=>false,send:_=>{sent++;return true;}),9000));
        int read=0;
        Check(!Gate().Dispatch(Req(),Host(current:_=>++read==1,
            send:_=>{sent++;return true;}),9000));
        Check(read==2 && sent==0);
        read=0;
        Check(!Gate().Dispatch(Req(),Host(authorize:_=>++read==1,
            send:_=>{sent++;return true;}),9000));
        Check(read==2 && sent==0);
        Check(!Gate().Dispatch(Req(),Host(claim:_=>throw new Exception("storage failed"),
            send:_=>{sent++;return true;}),9000));
        Check(sent==0);
        gate=Gate(); sent=0;
        h=Host(send:_=>{sent++;throw new Exception("uncertain outcome");});
        Check(!gate.Dispatch(Req(),h,9000) && !gate.Dispatch(Req(),h,9000));
        Check(sent==1);
        Console.WriteLine("AGENT_HOST_DISPATCH_CONTRACT PASS host-only NativeAOT target");
    }
}
