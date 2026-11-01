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
        Require(first.Accepted == 2 && !first.Backpressured,
            "first RELIABLE publication admitted to all subscribers");

        ActorPublishReceipt blocked = reliable.Publish(publisher, "two");
        Require(blocked.Backpressured && blocked.Accepted == 0,
            "RELIABLE backpressure is all-or-nothing");

        Require(reliable.TryRead(left, out string leftOne) && leftOne == "one",
            "left receives first RELIABLE value");
        Require(reliable.TryRead(right, out string rightOne) && rightOne == "one",
            "right receives first RELIABLE value");

        ActorPublishReceipt second = reliable.Publish(publisher, "two");
        Require(second.Accepted == 2 && !second.Backpressured,
            "RELIABLE capacity is reclaimed after reads");

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
        Require(afterLeave.Backpressured && afterLeave.Accepted == 0,
            "leave prunes only departed subscription and preserves remaining bound");
        Require(reliable.TryRead(left, out string leftTwo) && leftTwo == "two",
            "remaining A subscriber retained queued value");
        Require(reliable.Publish(publisher, "three").Accepted == 1,
            "publication reaches only remaining A subscriber");

        var latest = b.DefineTopic<string>(
            "latest", ActorTopicDeliveryPolicy.Latest, capacity: 1);
        Require(latest.Subscribe(left), "left LATEST subscription");
        ActorPublishReceipt latestOne = latest.Publish(publisher, "old");
        ActorPublishReceipt latestTwo = latest.Publish(publisher, "new");
        Require(latestOne.Accepted == 1 && latestTwo.Coalesced == 1,
            "LATEST coalesces unread older value");
        Require(latest.TryRead(left, out string latestValue) &&
                latestValue == "new",
            "LATEST retains newest value");

        var lossy = b.DefineTopic<string>(
            "lossy", ActorTopicDeliveryPolicy.Lossy, capacity: 1);
        Require(lossy.Subscribe(left), "left LOSSY subscription");
        Require(lossy.Publish(publisher, "one").Accepted == 1,
            "LOSSY first value admitted");
        Require(lossy.Publish(publisher, "two").Dropped == 1,
            "LOSSY drops new value when bounded queue is full");

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

        runtime.Stop(left);
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
