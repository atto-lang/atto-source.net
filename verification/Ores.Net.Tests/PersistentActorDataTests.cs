using System.Numerics;
using Ores.Net.Runtime;

static class PersistentActorDataTests
{
    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("Persistent actor data: " + why);
    }

    internal static void Run()
    {
        var empty = PersistentActorMap.Empty;
        var root = empty;
        for (int i = 0; i < 4096; i++)
            root = root.With("key-" + i.ToString("D5"), i);
        Require(root.Count == 4096, "count");
        Require(empty.Count == 0, "old empty root mutated");
        Require((int?)root.Get("key-01024") == 1024, "lookup");

        var prior = root;
        var updated = root.With("key-01024", 9001);
        Require((int?)prior.Get("key-01024") == 1024, "prior root changed");
        Require((int?)updated.Get("key-01024") == 9001, "updated root wrong");
        Require(ReferenceEquals(updated, updated.With("key-01024", 9001)),
            "scalar no-op should preserve root identity");

        var nested = PersistentActorMap.Empty.With("v", new BigInteger(12));
        var parent = PersistentActorMap.Empty.With("nested", nested);
        Require(ReferenceEquals(nested, parent.Get("nested")), "nested identity lost");
        Require(ReferenceEquals(parent, parent.With("nested", nested)),
            "same nested root must be O(1) no-op");
        var equalButDistinct = PersistentActorMap.Empty.With("v", new BigInteger(12));
        var replaced = parent.With("nested", equalButDistinct);
        Require(ReferenceEquals(equalButDistinct, replaced.Get("nested")),
            "distinct nested root should replace without deep equality");

        var deleted = updated.Without("key-01024");
        Require(deleted.Get("key-01024") is null, "delete failed");
        Require((int?)updated.Get("key-01024") == 9001, "delete mutated prior root");
        try
        {
            _ = root.With("mutable", new List<int>());
            throw new Exception("mutable List admitted");
        }
        catch (ArgumentException) { }

        var port = new PersistentActorMapPort(1, 64);
        Require(port.Offer(parent, 32), "first port offer");
        Require(!port.Offer(replaced, 32), "quota did not reject second offer");
        Require(port.Used == (1, 32L), "receive credit accounting");
        var delivery = port.TryReceive() ?? throw new Exception("missing delivery");
        Require(ReferenceEquals(parent, delivery.State), "immutable root identity copied");
        Require(port.Used == (1, 32L), "receive prematurely refunded credit");
        try
        {
            port.Close();
            throw new Exception("close accepted unacknowledged delivery");
        }
        catch (InvalidOperationException) { }
        Require(delivery.Acknowledge(), "first acknowledgement");
        Require(!delivery.Acknowledge(), "duplicate acknowledgement");
        Require(port.Used == (0, 0L), "credit not returned exactly once");
        // Close is idempotently safe only after every checked-out frame settles.
        port.Close();
    }
}
