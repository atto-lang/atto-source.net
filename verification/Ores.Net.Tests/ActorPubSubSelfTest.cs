using Ores.Net.Runtime;

internal static class ActorPubSubSelfTest
{
    public static void Run()
    {
        using var runtime = new ActorRuntime();
        using var a = runtime.PubSub.CreateGroup(maxMembers: 8);
        using var b = runtime.PubSub.CreateGroup(maxMembers: 8);

        ActorRef publisher = runtime.Spawn(ActorKind.Shared, static (_, _) => { });
        ActorRef left = runtime.Spawn(ActorKind.Shared, static (_, _) => { });
        ActorRef right = runtime.Spawn(ActorKind.Shared, static (_, _) => { });
        ActorRef isolated = runtime.Spawn(ActorKind.Private, static (_, _) => { });

        Require(a.Join(publisher), "publisher joins group A");
        Require(a.Join(left), "left joins A");
        Require(a.Join(right), "right joins A");
        Require(b.Join(publisher), "publisher joins B");
        Require(b.Join(left), "left joins B");
        Require(!b.Join(left), "duplicate group join is idempotent");

        var reliable = a.DefineTopic<string>(
            "reliable", ActorTopicDeliveryPolicy.Reliable, capacity: 1);
        Require(reliable.Subscribe(left), "left reliable subscription");
        Require(reliable.Subscribe(right), "right reliable subscription");
        Require(!reliable.Subscribe(right), "duplicate subscription is idempotent");

        ActorPublishReceipt first = reliable.Publish(publisher, "one");
        Require(first.Sequence == 1 && first.Accepted == 2 && !first.Backpressured,
            "first RELIABLE publication receives topic sequence 1");

        ActorPublishReceipt blocked = reliable.Publish(publisher, "two");
        Require(blocked.Sequence == 0 && blocked.Backpressured && blocked.Accepted == 0,
            "RELIABLE backpressure is all-or-nothing and allocates no sequence");

        Require(reliable.TryReadDelivery(left, out ActorPubSubDelivery<string> leftDelivery) &&
                leftDelivery.Sequence == 1 && leftDelivery.Value == "one",
            "left receives the value with its topic sequence");
        Require(reliable.TryReadDelivery(right, out ActorPubSubDelivery<string> rightDelivery) &&
                rightDelivery.Sequence == 1 && rightDelivery.Value == "one",
            "right receives the same admitted topic sequence");

        ActorPublishReceipt second = reliable.Publish(publisher, "two");
        Require(second.Sequence == 2 && second.Accepted == 2 && !second.Backpressured,
            "next committed RELIABLE publish increments the topic sequence");

        Require(a.Leave(right), "right leaves only A");
        Require(!a.Contains(right), "right no longer belongs to A");
        bool departedPublishRejected = false;
        try { _ = reliable.Publish(right, "forged-after-leave"); }
        catch (InvalidOperationException) { departedPublishRejected = true; }
        Require(departedPublishRejected,
            "leaving a group revokes actor publication authority even with a cached topic handle");
        Require(b.Contains(left), "A membership change does not affect B");
        ActorPublishReceipt afterLeave = reliable.Publish(publisher, "three");
        // left still has unread "two", so the one remaining subscriber applies
        // bounded backpressure rather than partially accepting the publication.
        Require(afterLeave.Sequence == 0 && afterLeave.Backpressured && afterLeave.Accepted == 0,
            "failed publication after leave does not consume a topic sequence");
        Require(reliable.TryRead(left, out string leftTwo) && leftTwo == "two",
            "remaining A subscriber retained queued value");
        ActorPublishReceipt third = reliable.TryPublish(publisher, "three");
        Require(third.Sequence == 3 && third.Accepted == 1,
            "TryPublish commits next sequence after capacity returns");

        var latest = b.DefineTopic<string>(
            "latest", ActorTopicDeliveryPolicy.Latest, capacity: 1);
        Require(latest.Subscribe(left), "left LATEST subscription");
        ActorPublishReceipt latestOne = latest.Publish(publisher, "old");
        ActorPublishReceipt latestTwo = latest.Publish(publisher, "new");
        Require(latestOne.Sequence == 1 && latestTwo.Sequence == 2 &&
                latestOne.Accepted == 1 && latestTwo.Coalesced == 1,
            "LATEST publishes monotonically increasing topic-local sequences");
        Require(latest.TryReadDelivery(left, out ActorPubSubDelivery<string> latestDelivery) &&
                latestDelivery.Sequence == 2 && latestDelivery.Value == "new",
            "LATEST retains newest value and its sequence");

        var lossy = b.DefineTopic<string>(
            "lossy", ActorTopicDeliveryPolicy.Lossy, capacity: 1);
        Require(lossy.Subscribe(left), "left LOSSY subscription");
        ActorPublishReceipt lossyFirst = lossy.Publish(publisher, "one");
        ActorPublishReceipt lossyDrop = lossy.Publish(publisher, "two");
        Require(lossyFirst.Sequence == 1 && lossyFirst.Accepted == 1,
            "LOSSY first publication receives sequence 1");
        Require(lossyDrop.Sequence == 2 && lossyDrop.Dropped == 1,
            "LOSSY advances sequence even when every recipient drops the event");
        Require(lossy.TryReadDelivery(left, out ActorPubSubDelivery<string> lossyDelivery) &&
                lossyDelivery.Sequence == 1 && lossyDelivery.Value == "one",
            "LOSSY read preserves the sequence of the event actually retained");

        Require(a.Join(isolated), "private actor can cooperate as group member");
        bool privateRejected = false;
        try { reliable.Subscribe(isolated); }
        catch (InvalidOperationException) { privateRejected = true; }
        Require(privateRejected,
            "private actor cannot subscribe to zero-copy in-memory topic");

        bool typeRejected = false;
        try { _ = a.DefineTopic<int>("reliable", ActorTopicDeliveryPolicy.Reliable, 1); }
        catch (InvalidOperationException) { typeRejected = true; }
        Require(typeRejected, "topic name cannot be rebound to another payload type");

        var lateTopic = a.DefineTopic<int>(
            "late-subscriber", ActorTopicDeliveryPolicy.Reliable, 2);
        ActorPublishReceipt noSubscribers = lateTopic.TryPublish(publisher, 41);
        Require(noSubscribers.Sequence == 1 && noSubscribers.Accepted == 0,
            "publish without subscribers succeeds and consumes sequence 1");
        ActorRef late = runtime.Spawn(ActorKind.Shared, static (_, _) => { });
        Require(a.Join(late) && lateTopic.Subscribe(late), "late subscriber joins after first publish");
        ActorPublishReceipt afterSubscription = lateTopic.TryPublish(publisher, 42);
        Require(afterSubscription.Sequence == 2 && afterSubscription.Accepted == 1,
            "later subscription receives sequence 2 only, without replay");
        Require(lateTopic.TryReadDelivery(late, out ActorPubSubDelivery<int> lateDelivery) &&
                lateDelivery.Sequence == 2 && lateDelivery.Value == 42,
            "late subscriber observes actual topic sequence with no implicit replay");

        runtime.Stop(left);
        runtime.Stop(late);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 3,
                TimeSpan.FromSeconds(5)),
            "left actor finalizes");
        Require(!b.Contains(left),
            "actor finalization removes all ActorGroup memberships");

        runtime.Stop(right);
        runtime.Stop(isolated);
        runtime.Stop(publisher);
        Require(SpinWait.SpinUntil(() => runtime.LiveActors == 0,
                TimeSpan.FromSeconds(5)),
            "all pub-sub actors finalize");
        Require(a.MemberCount == 0 && b.MemberCount == 0,
            "actor retirement leaves no group membership");

        ActorPubSub cachedPubSub = runtime.PubSub;
        runtime.Dispose();
        bool shutdownFenced = false;
        try { _ = cachedPubSub.CreateGroup(); }
        catch (ObjectDisposedException) { shutdownFenced = true; }
        Require(shutdownFenced,
            "cached pub-sub registry cannot create groups after runtime shutdown");

        Console.WriteLine(
            "PASS .NET ActorGroup pub-sub bounded delivery, multi-membership and lifecycle cleanup");
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor pub-sub regression: " + scenario);
    }
}
