using System;
using System.Collections.Generic;
using System.Threading;
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
        catch (ObjectDisposedException) { denied = true; }
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

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new Exception("Actor pubsub authority regression: " + description);
    }
}
