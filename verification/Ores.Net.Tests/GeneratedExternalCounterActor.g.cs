// Verified external mtd receive: bounded source subset, no dynamic emit.
using Ores.Net.Runtime;
namespace Oreslang.Generated;
public static class DeclaredActor_Counter
{
    public const string ActorName = "Counter";
    public static ActorRef SpawnRuntime(ActorRuntime runtime, ActorRef replyTo)
    {
        var state = new ActorNativeScalarState(new long[] { 0L }, new byte[] { 1 });
        return runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            if (message.Length != 8) throw new System.InvalidOperationException("Expected 8-byte ActorMail<int> payload");
            long incoming = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(message.Span);
            long result = state.AddInt64(context, 0, incoming);
            System.Span<byte> reply = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply, context.Self.Id);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(reply[8..], result);
            runtime.Send(replyTo, reply);
            if (incoming == 4L) runtime.Stop(context.Self);
        });
    }
}
