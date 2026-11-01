using Ores.Net.Compiler;

internal static class ActorPubSubSourceAdmissionTests
{
    private static readonly string[] Fixtures =
    {
        "actor-pubsub-v1.ores",
        "actor-pubsub-multigroup-v1.ores",
        "actor-pubsub-reject-payload-type.ores",
        "actor-pubsub-reject-actor-authority.ores",
        "actor-pubsub-reject-private-subscribe.ores",
        "actor-pubsub-reject-mailbox-capability.ores",
        "actor-pubsub-reject-async-capability.ores"
    };

    public static void Run()
    {
        foreach (string fixture in Fixtures)
        {
            string source = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory,
                    "contracts", "v1", "fixtures", fixture));

            RequireRejected(
                () => ActorPubSubSourceAdmission.RejectUnsupported(source),
                fixture + " shared admission");
            RequireRejected(
                () => _ = OresParser.Parse(source),
                fixture + " scalar parser");
            RequireRejected(
                () => _ = OresStructuredParser.Parse(source),
                fixture + " structured parser");
        }

        ActorPubSubSourceAdmission.RejectUnsupported(
            "fnc main():int{return 42;} // actor.group( Topic<String> Subscription<String>\n");
        ActorPubSubSourceAdmission.RejectUnsupported(
            "fnc main():int{return 42;} /* .topic<String> */");
        ActorPubSubSourceAdmission.RejectUnsupported(
            "fnc main():int{return 42;} // nested /* actor.group */ inert\n");

        Console.WriteLine(
            "PASS .NET pub-sub source corpus fails closed before scalar/structured lowering");
    }

    private static void RequireRejected(Action action, string scenario)
    {
        try
        {
            action();
        }
        catch (FormatException error)
            when (error.Message.Contains(
                ActorPubSubSourceAdmission.Diagnostic,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new Exception(
            "Actor pub-sub source admission regression: " + scenario);
    }
}
