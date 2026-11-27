using static Atto.Net.ActorRuntime.ActorSpawnChargePlan;

int checks = 0;
void Check(bool condition) { if (!condition) throw new Exception("check failed"); checks++; }
void Deny(string code, Action action)
{
    try { action(); }
    catch (ArgumentException e) { Check(e.Message == code); return; }
    throw new Exception("expected " + code);
}
var p = new Policy(false, 4, 2, 8, 1000, 100, 50, 4096);
var d = new Defaults(Affinity.Sticky, 500, 50, 10);
Check(Resolve(p,d,new Request()) == new Plan(2,Affinity.Sticky,50,500,400,4096,10));
Check(Resolve(p,d,new Request(1,Affinity.Pinned,300,20,3)) == new Plan(1,Affinity.Pinned,20,300,160,4096,3));
foreach (int cap in new[]{0,-1,5}) Deny("E_SPAWN_POLICY",()=>Resolve(p,d,new Request(Carriers:cap)));
Deny("E_SPAWN_POLICY",()=>Resolve(p,d,new Request(Affinity:Affinity.Pinned)));
Deny("E_SPAWN_POLICY",()=>Resolve(p,d,new Request(Affinity:(Affinity)99)));
var serial = p with { Serial=true, CarrierCap=1, DefaultCarriers=1 };
Check(Resolve(serial,d,new Request()).CarrierCap == 1);
Deny("E_SPAWN_POLICY",()=>Resolve(serial,d,new Request(Carriers:2)));
Deny("E_SPAWN_POLICY",()=>Resolve(serial with {CarrierCap=2},d,new Request()));
foreach (long heap in new long[]{0,-1,1001}) Deny("E_SPAWN_QUOTA",()=>Resolve(p,d,new Request(HeapBytes:heap)));
Deny("E_SPAWN_QUOTA",()=>Resolve(p,d,new Request(StackBytes:101)));
Deny("E_SPAWN_QUOTA",()=>Resolve(p,d,new Request(MailboxMessages:51)));
Deny("E_SPAWN_QUOTA",()=>Resolve(p,d with {HeapBytes=0},new Request(HeapBytes:10)));
var huge = p with {LiveStackSlots=long.MaxValue};
Deny("E_SPAWN_QUOTA",()=>Resolve(huge,d,new Request()));
Check(Resolve(huge,d with {StackBytes=1},new Request()).Stacks == long.MaxValue);
Deny("E_SPAWN_POLICY",()=>Resolve(p with {MailboxByteCharge=0},d,new Request()));
Console.WriteLine($"NET_SPAWN_CHARGE_PASS {checks} checks; host only");
