// Generated from verified isolated actor IR; no runtime code generation.
using Ores.Net.Runtime;
namespace Oreslang.Generated;
public static class Actor_Counter
{
    public const string ActorName = "Counter";
    public static OresVmHost.ActorTicket Create(OresVmHost host,
        OresVmHost.IsolateHandle isolate, ulong actorId)
    {
        var actor = host.RegisterActor(isolate, actorId);
        try {
            host.ConfigureNamedActorState(isolate, actor, ActorName,
                new OresVmHost.ActorStateField[] {
                    new(OresVmHost.ActorStateScalarKind.Int64, Int64Default: 0L),
                });
            return actor;
        }
        catch { host.RetireActor(isolate, actor); throw; }
    }
    public static long Call_increment(OresVmHost.ActorDispatchScope scope)
    {
        return scope.AddInt64(0, 1L);
    }
    public static long Dispatch_increment(
        OresVmHost.ActorScheduler scheduler, OresVmHost.IsolateHandle isolate,
        OresVmHost.ActorTicket actor)
    {
        return scheduler.DispatchNext(isolate, actor, ActorName, Call_increment);
    }
}
