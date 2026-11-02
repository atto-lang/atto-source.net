using System;
using System.Linq;
using System.Threading;
using Ores.Net.Runtime;

internal static class ActorPubSubByteQuotaTests
{
    public static void Run()
    {
        using var runtime = new ActorRuntime();
        using var group = runtime.PubSub.CreateGroup();
        var topic = group.DefineTopic<string>(
            "reliable-bytes", ActorTopicDeliveryPolicy.Reliable, 2);
        var subscribers = new ActorRef[9];
        foreach (int i in Enumerable.Range(0, subscribers.Length))
        {
            subscribers[i] = runtime.Spawn(
                ActorKind.Shared, static (_, _) => { });
            Require(group.Join(subscribers[i]), "join");
            Require(topic.Subscribe(subscribers[i]), "subscribe");
        }
        string payload = new('X', 8 * 1024 * 1024);
        var blocked = topic.Publish(subscribers[0], payload);
        Require(blocked.Accepted == 0 && blocked.Backpressured,
            "9 x 8MiB RELIABLE exceeds graph byte quota");
        foreach (var actor in subscribers)
            Require(!topic.TryRead(actor, out _), "no partial publication");
        Require(topic.Unsubscribe(subscribers[8]),
            "remove one reliable edge to drop fanout to 64MiB");
        var admitted = topic.Publish(subscribers[0], payload);
        Require(admitted.Accepted == 8 && !admitted.Backpressured,
            "8 x 8MiB exactly fills graph byte budget");
        var again = topic.Publish(subscribers[0], "another");
        Require(again.Accepted == 0 && again.Backpressured,
            "additional small payload is rejected without partial fanout");
        for (int i = 0; i < 8; i++)
            Require(topic.TryRead(subscribers[i], out string original) &&
                    original.Length == payload.Length,
                "full graph rejection retains original queued message");

        var lossy = group.DefineTopic<string>(
            "lossy-bytes", ActorTopicDeliveryPolicy.Lossy, 1);
        foreach (var actor in subscribers)
            Require(lossy.Subscribe(actor), "lossy subscribe");
        var partial = lossy.Publish(subscribers[0], payload);
        Require(partial.Accepted == 8 && partial.Dropped == 1,
            "LOSSY admits 8 of 9 copies within shared byte cap");

        foreach (var actor in subscribers) runtime.Stop(actor);
        Require(SpinWait.SpinUntil(
            () => runtime.LiveActors == 0, TimeSpan.FromSeconds(10)),
            "actor retirement");
        Console.WriteLine("PASS .NET pubsub 64MiB bounded all-or-nothing");
    }

    private static void Require(bool ok, string reason)
    {
        if (!ok) throw new Exception("Pubsub byte ledger regression: " + reason);
    }
}
