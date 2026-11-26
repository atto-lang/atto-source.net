// Generated from verified isolated actor IR; no runtime code generation.
using Ores.Net.Runtime;
namespace Oreslang.Generated;
public static class Actor_Counter
{
    public const string ActorName = "Counter";
    public const string ActorLayoutIdentity = "Counter|1:I";
    public static OresVmHost.ActorTicket Create(OresVmHost host,
        OresVmHost.IsolateHandle isolate, ulong actorId)
    {
        var actor = host.RegisterActor(isolate, actorId);
        try {
            host.ConfigureNamedActorState(
                isolate, actor, ActorName, ActorLayoutIdentity,
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
        return scheduler.DispatchNext(isolate, actor, ActorLayoutIdentity, Call_increment);
    }
    // Production mailbox dispatch: native state and execution-owned frames.
    public static ActorRef SpawnRuntime(ActorRuntime runtime, ActorRef replyTo)
    {
        var state = new ActorNativeScalarState(new long[] { 0L }, new byte[] { 1 });
        return runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            if (!message.IsEmpty) throw new System.InvalidOperationException("Expected zero-argument actor message");
            long result = state.AddInt64(context, 0, 1L);
            System.Span<byte> reply = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply, context.Self.Id);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(reply[8..], result);
            runtime.Send(replyTo, reply);
        });
    }
}
