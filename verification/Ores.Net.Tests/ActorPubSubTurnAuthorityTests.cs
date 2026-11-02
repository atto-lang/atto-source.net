using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Ores.Net.Runtime;

/// <summary>
/// NativeAOT-compatible tests for the compiler's future context-bound guest
/// intrinsic. Host ActorRef overloads are NOT proof of source lowering.
/// </summary>
internal static class ActorPubSubTurnAuthorityTests
{
    public static void Run()
    {
        TurnBoundSubscribePublishAndRead();
        SubscriptionQuotaAndEpochRejoin();
        GlobalQueueSlotsAreBoundedAndReclaimed();
        InvalidUtf8AndOversizedStringFailBeforeAdmission();
        Console.WriteLine(
            "PASS .NET bounded pubsub turn authority, membership epochs and quotas");
    }

    private static void TurnBoundSubscribePublishAndRead()
    {
        using var runtime = new ActorRuntime();
        using var group = runtime.PubSub.CreateGroup();
        var topic = group.DefineTopic<string>(
            "turn-bound", ActorTopicDeliveryPolicy.Reliable, 2);
        using var completed = new ManualResetEventSlim();
        ActorContext? retained = null;
        Exception? failure = null;

        ActorRef actor = runtime.Spawn(ActorKind.Shared, (context, _) =>
        {
            try
            {
                retained = context;
                RequireDenied(() => _ = runtime.PubSub.CreateGroup(),
                    "actor must not create an ActorGroup");
                RequireDenied(() => _ = group.DefineTopic<string>(
                    "forged", ActorTopicDeliveryPolicy.Reliable, 1),
                    "actor must not define a topic");
                RequireDenied(() => _ = group.Join(context.Self),
                    "actor must not grant group membership");
                RequireDenied(() => group.Dispose(),
                    "actor must not close its manager's group");
                RequireDenied(() => _ = topic.Publish(context.Self, "spoofed"),
                    "actor must not use host ActorRef publication");
                RequireDenied(() => _ = topic.Subscribe(context.Self),
                    "actor must not use host ActorRef subscription");
                // ThreadStatic alone is insufficient: a Task inherits the
                // actor's ExecutionContext but runs on another thread.
                RequireDenied(() => _ = Task.Run(
                    () => topic.Publish(context.Self, "offloaded-spoof"))
                    .GetAwaiter().GetResult(),
                    "actor-origin Task.Run must not regain host publish authority");
                RequireDenied(() => _ = Task.Run(
                    () => runtime.PubSub.CreateGroup())
                    .GetAwaiter().GetResult(),
                    "actor-origin Task.Run must not regain group-manager authority");
                Require(topic.Subscribe(context),
                    "actor context subscribes inside physical turn");
                Require(!topic.Subscribe(context),
                    "duplicate subscribe is idempotent");
                var admitted = topic.Publish(context, "actor-origin");
                Require(admitted.Accepted == 1 && !admitted.Backpressured,
                    "physical-turn publication is admitted");
                Require(topic.TryRead(context, out string value)
                    && value == "actor-origin",
                    "physical-turn read retains payload");
                Require(topic.Unsubscribe(context), "turn-bound close");
            }
            catch (Exception error) { failure = error; }
            finally { completed.Set(); }
        });
        Require(group.Join(actor), "host explicitly grants membership");
        runtime.Send(actor, new byte[] { 1 });
        Require(completed.Wait(TimeSpan.FromSeconds(5)),
            "actor turn completed");
        if (failure is not null)
            throw new Exception("Actor pubsub turn-bound test failed", failure);
        Require(retained is not null, "captured only for negative stale test");
        bool denied = false;
        try { _ = topic.Publish(retained!, "stale-turn"); }
        catch (InvalidOperationException) { denied = true; }
        Require(denied,
            "retained ActorContext must never grant authority after handler return");

        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0,
                TimeSpan.FromSeconds(5)),
            "actor finalization");
        Require(group.MemberCount == 0,
            "termination drops group membership");
    }

    private static void SubscriptionQuotaAndEpochRejoin()
    {
        using var runtime = new ActorRuntime();
        using var group = runtime.PubSub.CreateGroup();
        var topics = new List<ActorPubSub.ActorTopic<string>>();
        for (int i = 0; i < 65; i++)
            topics.Add(group.DefineTopic<string>(
                "quota-" + i, ActorTopicDeliveryPolicy.Latest, 1));

        ActorRef first = runtime.Spawn(ActorKind.Shared, static (_, _) => { });
        Require(group.Join(first), "first incarnation admitted");
        for (int i = 0; i < 64; i++)
            Require(topics[i].Subscribe(first), "subscription quota " + i);
        Require(!topics[0].Subscribe(first),
            "duplicate subscription consumes zero additional quota");
        bool exhausted = false;
        try { _ = topics[64].Subscribe(first); }
        catch (InvalidOperationException) { exhausted = true; }
        Require(exhausted, "65th subscription exceeds bounded actor quota");

        Require(topics[0].Unsubscribe(first), "unsubscribe returns quota");
        Require(topics[64].Subscribe(first),
            "returned quota can be reassigned to another topic");
        Require(topics[1].Publish(first, "old-generation").Accepted == 1,
            "old incarnation receives buffered value");
        Require(group.Leave(first), "membership retirement");
        Require(!group.Contains(first), "leave is a revocation barrier");
        Require(group.Join(first), "same actor explicitly rejoins group");
        Require(topics[1].Subscribe(first),
            "rejoining creates a new subscription edge");
        Require(!topics[1].TryRead(first, out _),
            "old edge buffered payload never survives rejoin");

        runtime.Stop(first);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0,
                TimeSpan.FromSeconds(5)),
            "actor finalization");
        Require(group.MemberCount == 0, "actor death removes memberships");

        ActorRef replacement = runtime.Spawn(
            ActorKind.Shared, static (_, _) => { });
        Require(group.Join(replacement), "replacement generation admitted");
        Require(topics[1].Subscribe(replacement), "replacement subscription");
        Require(!topics[1].TryRead(replacement, out _),
            "replacement has no abandoned delivery from dead actor");
        Require(topics[1].Publish(replacement, "fresh").Accepted == 1,
            "replacement publishes using own full ActorRef identity");
        Require(topics[1].TryRead(replacement, out string current)
                && current == "fresh", "replacement sees only fresh data");
        runtime.Stop(replacement);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0,
                TimeSpan.FromSeconds(5)), "replacement finalization");
    }


    private static void GlobalQueueSlotsAreBoundedAndReclaimed()
    {
        using var runtime = new ActorRuntime();
        using var group = runtime.PubSub.CreateGroup();
        var topics = new List<ActorPubSub.ActorTopic<string>>();
        for (int i = 0; i < 17; i++)
            topics.Add(group.DefineTopic<string>(
                "slots-" + i, ActorTopicDeliveryPolicy.Reliable, 65_536));
        ActorRef actor = runtime.Spawn(
            ActorKind.Shared, static (_, _) => { });
        Require(group.Join(actor), "queue-slot quota membership");
        for (int i = 0; i < 16; i++)
            Require(topics[i].Subscribe(actor),
                "preallocate 65,536 slots for edge " + i);

        bool quotaRejected = false;
        try { _ = topics[16].Subscribe(actor); }
        catch (InvalidOperationException) { quotaRejected = true; }
        Require(quotaRejected,
            "more than 1,048,576 total fixed queue slots must fail closed");
        Require(topics[0].Unsubscribe(actor), "remove one preallocated queue");
        Require(topics[16].Subscribe(actor),
            "unsubscribe releases exactly one bounded queue reservation");

        bool oversizedRejected = false;
        try { _ = group.DefineTopic<string>(
            "oversized", ActorTopicDeliveryPolicy.Reliable, 65_537); }
        catch (ArgumentOutOfRangeException) { oversizedRejected = true; }
        Require(oversizedRejected, "per-edge capacity beyond 65,536 rejected");

        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0,
            TimeSpan.FromSeconds(5)), "slot-owning actor finalization");

        ActorRef next = runtime.Spawn(
            ActorKind.Shared, static (_, _) => { });
        Require(group.Join(next), "replacement after slot reclamation");
        for (int i = 0; i < 16; i++)
            Require(topics[i].Subscribe(next),
                "retired subscriptions return all aggregate queue slots");
        runtime.Stop(next);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0,
            TimeSpan.FromSeconds(5)), "replacement slot reclamation");
    }

    private static void InvalidUtf8AndOversizedStringFailBeforeAdmission()
    {
        using var runtime = new ActorRuntime();
        using var group = runtime.PubSub.CreateGroup();
        var topic = group.DefineTopic<string>(
            "strict-utf8", ActorTopicDeliveryPolicy.Reliable, 1);
        ActorRef actor = runtime.Spawn(
            ActorKind.Shared, static (_, _) => { });
        Require(group.Join(actor), "UTF-8 test actor joined");
        Require(topic.Subscribe(actor), "UTF-8 test subscription");

        bool malformedRejected = false;
        try { _ = topic.Publish(actor, "\uD800"); }
        catch (ArgumentException) { malformedRejected = true; }
        Require(malformedRejected, "lone UTF-16 surrogate must not be replaced");

        // The budget is charged in UTF-8 bytes. Multi-byte characters
        // cannot escape the limit by exploiting a smaller UTF-16 Length.
        bool oversizedRejected = false;
        try { _ = topic.Publish(actor, new string('\u00E9', 8 * 1024 * 1024 + 1)); }
        catch (InvalidOperationException) { oversizedRejected = true; }
        Require(oversizedRejected, "16MiB UTF-8 payload limit enforced");
        Require(!topic.TryRead(actor, out _),
            "invalid or over-budget payload must not reach the queue");

        Require(topic.Publish(actor, "valid").Accepted == 1,
            "rejected deliveries must not consume queue slots");
        Require(topic.TryRead(actor, out string value) && value == "valid",
            "valid payload preserved after rejection");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0,
            TimeSpan.FromSeconds(5)), "UTF-8 test actor retirement");
    }

    private static void RequireDenied(Action action, string description)
    {
        try { action(); }
        catch (SecurityException) { return; }
        throw new Exception(
            "Actor pubsub guest manager authority leaked: " + description);
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new Exception("Actor pubsub authority regression: " + description);
    }
}
