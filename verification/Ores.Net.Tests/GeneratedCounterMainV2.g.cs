// Source-checked Counter v2 async main, static NativeAOT.
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Ores.Net.Runtime;
namespace Oreslang.Generated;
public static class CounterMain_V2
{
    public static async Task<string> RunAsync()
    {
        using var runtime = new ActorRuntime(new ActorRuntimeOptions {
            MaxActors = 4, ActorPersistentBytes = 256, ActorScratchBytes = 64,
            ActorStackBytes = 16, NativeBudgetBytes = 64 * 1024
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outputs = Channel.CreateBounded<long>(new BoundedChannelOptions(4) {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        ActorRef worker = default;
        ActorRef observer = runtime.Spawn(ActorKind.Shared, (_, payload) => {
            if (payload.Length != 16)
                throw new InvalidOperationException("Malformed actor output");
            if (BinaryPrimitives.ReadUInt64LittleEndian(payload.Span) != worker.Id)
                throw new InvalidOperationException("Foreign actor output");
            long value = BinaryPrimitives.ReadInt64LittleEndian(payload.Span[8..]);
            if (!outputs.Writer.TryWrite(value))
                throw new InvalidOperationException("Bounded output overflow");
        });
        worker = DeclaredActor_Counter.SpawnRuntime(runtime, observer);
        // get_ready is resolved by synchronous actor registration in this closed profile.
        Task done = runtime.ObserveTermination(worker);
        var mail = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(mail, 1L);
        runtime.Send(worker, mail);
        BinaryPrimitives.WriteInt64LittleEndian(mail, 2L);
        runtime.Send(worker, mail);
        BinaryPrimitives.WriteInt64LittleEndian(mail, 3L);
        runtime.Send(worker, mail);
        BinaryPrimitives.WriteInt64LittleEndian(mail, 4L);
        runtime.Send(worker, mail);
        long v0 = await outputs.Reader.ReadAsync(timeout.Token);
        long v1 = await outputs.Reader.ReadAsync(timeout.Token);
        long v2 = await outputs.Reader.ReadAsync(timeout.Token);
        long v3 = await outputs.Reader.ReadAsync(timeout.Token);
        await done.WaitAsync(timeout.Token);
        var stdout = new StringBuilder();
        stdout.Append(v0.ToString(CultureInfo.InvariantCulture));
        stdout.Append('|');
        stdout.Append(v1.ToString(CultureInfo.InvariantCulture));
        stdout.Append('|');
        stdout.Append(v2.ToString(CultureInfo.InvariantCulture));
        stdout.Append('|');
        stdout.Append(v3.ToString(CultureInfo.InvariantCulture));
        runtime.Dispose();
        if (runtime.NativeReservedBytes != 0)
            throw new InvalidOperationException("Actor-private native bytes leaked");
        return stdout.ToString();
    }
}
