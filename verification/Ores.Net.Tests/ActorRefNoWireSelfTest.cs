using System.Text.Json;
using Ores.Net.Runtime;

// Enforce local actor reference authority non-export across all common JSON
// container shapes. This is not an implementation of network actor routing.
internal static class ActorRefNoWireSelfTest
{
    public static void Run()
    {
        var actor = new ActorMailbox(65001);
        var reference = actor.CurrentRef;
        ExpectDenied(() => _ = JsonSerializer.Serialize(reference), "direct");
        ExpectDenied(() => _ = JsonSerializer.Serialize<object>(reference), "boxed");
        ExpectDenied(() => _ = JsonSerializer.Serialize(new { Child = reference }), "record member");
        ExpectDenied(() => _ = JsonSerializer.Serialize(new[] { reference }), "array");
        ExpectDenied(() => _ = JsonSerializer.Serialize(
            new Dictionary<string, ActorRef> { ["child"] = reference }), "map value");
        ExpectDenied(() => _ = JsonSerializer.Serialize(
            new Dictionary<ActorRef, string> { [reference] = "child" }), "map key");
        ExpectDenied(() => _ = JsonSerializer.Deserialize<ActorRef>(
            "{\"Id\":65001,\"Generation\":1}"), "wire reconstruction");
        // Nullable and polymorphic containers must resolve the same deny-only
        // ActorRef converter; typed nested wire reconstruction is not a
        // legitimate local-capability minting mechanism.
        ExpectDenied(() => _ = JsonSerializer.Serialize<ActorRef?>(reference),
            "nullable ref");
        ExpectDenied(() => _ = JsonSerializer.Serialize(
            new List<object> { new { Nested = reference } }), "polymorphic nested");
        ExpectDenied(() => _ = JsonSerializer.Serialize(
            new List<ActorRef> { reference }), "list");
        ExpectDenied(() => _ = JsonSerializer.Deserialize<List<ActorRef>>(
            "[{\"Id\":65001,\"Generation\":1}]"), "list reconstruction");
        ExpectDenied(() => _ = JsonSerializer.Deserialize<Dictionary<string, ActorRef>>(
            "{\"child\":{\"Id\":65001,\"Generation\":1}}"),
            "map reconstruction");
        // Closing/restarting does not turn a previously private handle into a
        // network value: the converter rejects stale and current refs equally.
        actor.Stop();
        var fresh = actor.Restart();
        ExpectDenied(() => _ = JsonSerializer.Serialize(reference), "stale ref");
        ExpectDenied(() => _ = JsonSerializer.Serialize(fresh), "fresh ref");
        Console.WriteLine("PASS ActorRef JSON direct, boxed, nested, collection, map and restart denials");
    }

    private static void ExpectDenied(Action action, string scenario)
    {
        try { action(); }
        catch (NotSupportedException) { return; }
        throw new InvalidOperationException("ActorRef became JSON data: " + scenario);
    }
}
