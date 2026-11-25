// Verified trapped actor await; exact shared GuestResults ABI.
#nullable enable
using Ores.Net.Runtime;
using System.Threading.Tasks;
namespace Oreslang.Generated;
internal sealed class AwaitActor_TrapFaultCounter
{
    internal const string Layout = "TrapFaultCounter|1:I";
    internal static OresVmHost.ActorTicket Create(OresVmHost host,
        OresVmHost.IsolateHandle isolate, ulong id, bool serial)
    {
        var actor = host.RegisterActor(isolate, id, serial);
        try {
            host.ConfigureNamedActorState(isolate, actor, "TrapFaultCounter", Layout,
                new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64, 0L) });
            return actor;
        } catch { host.RetireActor(isolate, actor); throw; }
    }
    private Task<long>? _pending;
    private int _pc;
    internal GuestResultI64? Result { get; private set; }
    internal AwaitActor_TrapFaultCounter() { }
    internal OresVmHost.ActorPumpStep Start_observe(OresVmHost.ActorDispatchScope scope)
    {
        if (_pc != 0) throw new System.InvalidOperationException("stale start");
        var producer = new OresVmHost.ActorScalarTask(static () => Produce(9223372036854775807L, 1L));
        _pending = producer.Future;
        _pc = 1;
        return new OresVmHost.ActorPumpStep(producer.Future, Resume, producer, ObserveFaultInSegment: true);
    }
    private static long Produce(long left, long right) => checked(left + right);
    private OresVmHost.ActorPumpStep? Resume(OresVmHost.ActorDispatchScope scope)
    {
        if (_pc != 1 || _pending is null || !_pending.IsCompleted)
            throw new System.InvalidOperationException("stale or non-ready resume");
        _pc = 2;
        Result = GuestAwaitResults.TrapCompletedI64(_pending);
        return null;
    }
}
