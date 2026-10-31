// Generated from verified isolated actor IR; no runtime code generation.
using Ores.Net.Runtime;
namespace Oreslang.Generated;
public static class Actor_Counter
{
    public static OresVmHost.ActorTicket Create(OresVmHost host,
        OresVmHost.IsolateHandle isolate, ulong actorId)
    {
        var actor = host.RegisterActor(isolate, actorId);
        try {
            host.ConfigureActorState(isolate, actor,
                new OresVmHost.ActorStateField[] {
                    new(OresVmHost.ActorStateScalarKind.Int64, Int64Default: 0L),
                });
            return actor;
        }
        catch { host.RetireActor(isolate, actor); throw; }
    }
    public static long Call_increment(
        OresVmHost host, OresVmHost.IsolateHandle isolate,
        OresVmHost.ActorTicket actor)
    {
        return host.InvokeTrustedActorStateLong(isolate, actor,
            (turn, loan) => host.AddActorStateInt64(isolate, actor, turn, loan, 0, 1L));
    }
}
