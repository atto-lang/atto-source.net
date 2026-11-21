using Ores.Net.Runtime;
using System.Threading.Tasks;

internal static class ActorTypedReplySlotsSelfTest
{
    private readonly record struct DomainAnswer(long UserId, bool NotFound);

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Exception("Typed actor reply V1: " + reason);
    }
    private static void Reject<TError>(Action action, string reason) where TError : Exception
    {
        try { action(); }
        catch (TError) { return; }
        throw new Exception("Typed actor reply V1 accepted invalid: " + reason);
    }
    public static void Run()
    {
        using var registry = new ActorTypedReplySlots<long>(2, 2);
        var actor = registry.CreateEndpoint(ActorTypedReplySlots<long>.EndpointRole.Actor);
        var worker = registry.CreateEndpoint(ActorTypedReplySlots<long>.EndpointRole.Actor);
        var supervisorApp = registry.CreateEndpoint(
            ActorTypedReplySlots<long>.EndpointRole.SupervisorApplication);
        Reject<ArgumentOutOfRangeException>(() => registry.CreateEndpoint(
            (ActorTypedReplySlots<long>.EndpointRole)99), "CONTROL cannot be endpoint");
        Reject<InvalidOperationException>(() => registry.Reserve(actor, worker,
            ActorTypedReplySlots<long>.ReplyMode.OneWay), "oneway cannot allocate reply");
        Reject<InvalidOperationException>(() => registry.Reserve(actor, actor,
            ActorTypedReplySlots<long>.ReplyMode.Unary), "self-dependent request");
        var first = registry.Reserve(actor, worker, ActorTypedReplySlots<long>.ReplyMode.Unary);
        var second = registry.Reserve(actor, supervisorApp, ActorTypedReplySlots<long>.ReplyMode.Unary);
        Reject<InvalidOperationException>(() => registry.Reserve(actor, worker,
            ActorTypedReplySlots<long>.ReplyMode.Unary), "slot capacity");
        Reject<UnauthorizedAccessException>(() => registry.TryWrite(first, supervisorApp, 11),
            "supervisor application must not forge worker reply");
        Reject<UnauthorizedAccessException>(() => registry.Poll(first, worker),
            "caller-only receive");
        Require(registry.Poll(first, actor).State ==
            ActorTypedReplySlots<long>.ReadState.Pending, "unanswered request pending");
        Require(registry.TryWrite(second, supervisorApp, 22) ==
            ActorTypedReplySlots<long>.WriteState.Written, "supervisor application reply");
        Require(registry.TryWrite(first, worker, 11) ==
            ActorTypedReplySlots<long>.WriteState.Written, "worker reply");
        Require(registry.TryWrite(first, worker, 33) ==
            ActorTypedReplySlots<long>.WriteState.Terminal, "second unary reply refused");
        Require(registry.Poll(first, actor).Value == 11 &&
                registry.Poll(second, actor).Value == 22, "out-of-order correlation");
        Require(registry.Poll(first, actor).State ==
            ActorTypedReplySlots<long>.ReadState.Stale, "unary consume once");
        Require(registry.PendingCount == 0, "unary slots retired");

        var stream = registry.Reserve(actor, worker, ActorTypedReplySlots<long>.ReplyMode.Stream);
        Require(registry.TryWrite(stream, worker, 1) ==
            ActorTypedReplySlots<long>.WriteState.Written, "stream item 1");
        Require(registry.TryWrite(stream, worker, 2) ==
            ActorTypedReplySlots<long>.WriteState.Written, "stream item 2");
        Require(registry.TryWrite(stream, worker, 3) ==
            ActorTypedReplySlots<long>.WriteState.Full, "bounded backpressure");
        Require(registry.Poll(stream, actor).Value == 1, "first ordered item");
        Require(registry.TryWrite(stream, worker, 3) ==
            ActorTypedReplySlots<long>.WriteState.Written, "credit recovered");
        Require(registry.TryCloseStream(stream, worker) ==
            ActorTypedReplySlots<long>.WriteState.Written, "stream normal close");
        Require(registry.TryWrite(stream, worker, 4) ==
            ActorTypedReplySlots<long>.WriteState.Terminal, "no items after close");
        Require(registry.Poll(stream, actor).Value == 2 &&
                registry.Poll(stream, actor).Value == 3, "stream ordered");
        Require(registry.Poll(stream, actor).State ==
            ActorTypedReplySlots<long>.ReadState.End, "normal end not error");

        var failed = registry.Reserve(actor, worker, ActorTypedReplySlots<long>.ReplyMode.Stream);
        Require(registry.TryWrite(failed, worker, 9) ==
            ActorTypedReplySlots<long>.WriteState.Written, "accepted item before error");
        Require(registry.TryFail(failed, worker) ==
            ActorTypedReplySlots<long>.WriteState.Written, "terminal failure");
        Require(registry.Poll(failed, actor).Value == 9 &&
                registry.Poll(failed, actor).State ==
                    ActorTypedReplySlots<long>.ReadState.Failed,
                "failure distinct from normal stream EOF");

        var old = registry.Reserve(actor, worker, ActorTypedReplySlots<long>.ReplyMode.Unary);
        Require(registry.Cancel(old, actor), "cancel wins");
        var renewed = registry.Reserve(actor, worker, ActorTypedReplySlots<long>.ReplyMode.Unary);
        Require(old.LocalHandle != renewed.LocalHandle, "generation fences slot reuse");
        Require(registry.TryWrite(old, worker, 123) ==
            ActorTypedReplySlots<long>.WriteState.Stale, "late reply refused");
        Require(registry.TryFail(renewed, worker) ==
            ActorTypedReplySlots<long>.WriteState.Written, "unanswered unary terminal failure");
        Require(registry.Poll(renewed, actor).State ==
            ActorTypedReplySlots<long>.ReadState.Failed, "unary failure reported");

        var dying = registry.Reserve(actor, worker, ActorTypedReplySlots<long>.ReplyMode.Stream);
        registry.RetireEndpoint(worker);
        Require(registry.PendingCount == 0 &&
                registry.Poll(dying, actor).State ==
                    ActorTypedReplySlots<long>.ReadState.Stale,
                "callee restart fences old stream");
        Reject<InvalidOperationException>(() => registry.Reserve(actor, worker,
            ActorTypedReplySlots<long>.ReplyMode.Stream), "retired callee cannot admit");

        var newWorker = registry.CreateEndpoint(ActorTypedReplySlots<long>.EndpointRole.Actor);
        for (int i = 0; i < 100; ++i)
        {
            var lease = registry.Reserve(actor, newWorker,
                ActorTypedReplySlots<long>.ReplyMode.Stream);
            Parallel.Invoke(
                () => registry.TryCloseStream(lease, newWorker),
                () => registry.Cancel(lease, actor));
            Require(registry.PendingCount == 0, "close/cancel race settled");
        }
        registry.Dispose();
        Reject<ObjectDisposedException>(() => registry.CreateEndpoint(
            ActorTypedReplySlots<long>.EndpointRole.Actor), "disposed ledger");

        // Application domain Result remains a typed value, not a transport fault.
        using var domain = new ActorTypedReplySlots<DomainAnswer>(1, 1);
        var domainCaller = domain.CreateEndpoint(ActorTypedReplySlots<DomainAnswer>.EndpointRole.Actor);
        var domainCallee = domain.CreateEndpoint(ActorTypedReplySlots<DomainAnswer>.EndpointRole.Actor);
        var answer = domain.Reserve(domainCaller, domainCallee,
            ActorTypedReplySlots<DomainAnswer>.ReplyMode.Unary);
        Require(domain.TryWrite(answer, domainCallee, new DomainAnswer(0, true)) ==
            ActorTypedReplySlots<DomainAnswer>.WriteState.Written, "typed domain error response");
        var received = domain.Poll(answer, domainCaller);
        Require(received.State == ActorTypedReplySlots<DomainAnswer>.ReadState.Item &&
                received.Value.NotFound, "preserve domain result independently of transport");
        Console.WriteLine("PASS .NET NativeAOT-safe HOST-ONLY actor typed unary/stream reply slots");
    }
}
