// Verified one-await actor frame; static NativeAOT source.
using Ores.Net.Runtime;
using System.Threading.Tasks;
namespace Oreslang.Generated;
internal sealed class AwaitActor_AwaitCounter
{
    internal const string Layout = "AwaitCounter|1:I";
    internal static OresVmHost.ActorTicket Create(OresVmHost host,
        OresVmHost.IsolateHandle isolate, ulong id, bool serial)
    {
        var actor = host.RegisterActor(isolate, id, serial);
        try {
            host.ConfigureNamedActorState(isolate, actor, "AwaitCounter", Layout,
                new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64, 0L) });
            return actor;
        } catch { host.RetireActor(isolate, actor); throw; }
    }
    private readonly long _saved;
    private readonly Task<long> _pending;
    private int _pc;
    internal long? Result { get; private set; }
    internal AwaitActor_AwaitCounter(long incoming, Task<long> pending)
    {
        _saved = incoming;
        _pending = pending ?? throw new System.ArgumentNullException(nameof(pending));
    }
    internal OresVmHost.ActorPumpStep Start_add(OresVmHost.ActorDispatchScope scope)
    {
        if (_pc != 0) throw new System.InvalidOperationException("stale start");
        _pc = 1;
        return new OresVmHost.ActorPumpStep(_pending, Resume);
    }
    private OresVmHost.ActorPumpStep? Resume(OresVmHost.ActorDispatchScope scope)
    {
        if (_pc != 1 || !_pending.IsCompletedSuccessfully)
            throw new System.InvalidOperationException("stale or non-ready resume");
        _pc = 2;
        long delta = checked(_saved + _pending.GetAwaiter().GetResult());
        Result = scope.AddInt64(0, delta);
        return null;
    }
}
