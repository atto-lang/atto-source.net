// Generated from verified isolated actor IR; no runtime code generation.
using Ores.Net.Runtime;
namespace Oreslang.Generated;
public static class Actor_MessageCounter
{
    public const string ActorName = "MessageCounter";
    public const string ActorLayoutIdentity = "MessageCounter|1:I";
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
    // Production mailbox dispatch: native state and execution-owned frames.
    public static ActorRef SpawnRuntime(ActorRuntime runtime, ActorRef replyTo)
    {
        var state = new ActorNativeScalarState(new long[] { 0L }, new byte[] { 1 });
        return runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            if (message.Length != 8) throw new System.InvalidOperationException("Expected one 8-byte signed actor message");
            long delta = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(message.Span);
            long result = state.AddInt64(context, 0, delta);
            System.Span<byte> reply = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply, context.Self.Id);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(reply[8..], result);
            runtime.Send(replyTo, reply);
        });
    }
}
