// Verified one-await actor frame; static NativeAOT source.
#nullable enable
using Ores.Net.Runtime;
using System.Threading.Tasks;
namespace Oreslang.Generated;
internal sealed class AwaitActor_CaptureTaskCounter
{
    internal const string Layout = "CaptureTaskCounter|1:I";
    internal static OresVmHost.ActorTicket Create(OresVmHost host,
        OresVmHost.IsolateHandle isolate, ulong id, bool serial)
    {
        var actor = host.RegisterActor(isolate, id, serial);
        try {
            host.ConfigureNamedActorState(isolate, actor, "CaptureTaskCounter", Layout,
                new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64, 0L) });
            return actor;
        } catch { host.RetireActor(isolate, actor); throw; }
    }
    private readonly long _saved;
    private Task<long>? _pending;
    private long _first;
    private int _pc;
    internal long? Result { get; private set; }
    internal AwaitActor_CaptureTaskCounter(long incoming)
    {
        _saved = incoming;
    }
    internal OresVmHost.ActorPumpStep Start_add(OresVmHost.ActorDispatchScope scope)
    {
        if (_pc != 0) throw new System.InvalidOperationException("stale start");
        var producer = new OresVmHost.ActorScalarTask(Produce, _saved, 5L);
        _pending = producer.Future;
        _pc = 1;
        return new OresVmHost.ActorPumpStep(producer.Future, Resume, producer, ObserveFaultInSegment: true);
    }
    private static long Produce(long left, long right) => checked(left + right);
    private OresVmHost.ActorPumpStep? Resume(OresVmHost.ActorDispatchScope scope)
    {
        if (_pc != 1 || _pending is null || !_pending.IsCompleted)
            throw new System.InvalidOperationException("stale or non-ready resume");
        _first = _pending.GetAwaiter().GetResult();
        var producer = new OresVmHost.ActorScalarTask(Produce, _first, 11L);
        _pending = producer.Future;
        _pc = 2;
        return new OresVmHost.ActorPumpStep(producer.Future, ResumeSecond, producer, ObserveFaultInSegment: true);
    }
    private OresVmHost.ActorPumpStep? ResumeSecond(OresVmHost.ActorDispatchScope scope)
    {
        if (_pc != 2 || _pending is null || !_pending.IsCompleted)
            throw new System.InvalidOperationException("stale or non-ready second resume");
        long second = _pending.GetAwaiter().GetResult();
        _pc = 3;
        long delta = checked(checked(_saved + _first) + second);
        Result = scope.AddInt64(0, delta);
        return null;
    }
}
