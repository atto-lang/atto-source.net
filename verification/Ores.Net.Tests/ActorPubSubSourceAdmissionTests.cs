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

        // The actor pipe operator is not the eager functional |> operator.
        foreach (string source in new[] {
            "return spawn Source() pipe spawn Sink();",
            "return ActorUtils::pipe(spawn Source(), spawn Sink());",
            "return ActorUtils.pipe(spawn Source(), spawn Sink());",
            "return first pipe second;"
        })
        {
            RequirePipeRejected(
                () => ActorPubSubSourceAdmission.RejectUnsupported(source),
                "actor pipeline scanner " + source);
            RequirePipeRejected(
                () => _ = OresParser.Parse(source),
                "scalar parser " + source);
            RequirePipeRejected(
                () => _ = OresStructuredParser.Parse(source),
                "structured parser " + source);
        }

        foreach (string inert in new[] {
            "fnc pipe(x:int):int{return x;}",
            "fnc main(pipe:int):int{return pipe+1;}",
            "fnc main(x:int):int{return x |> add_one;}",
            "fnc main():int{return 42;} // spawn A() pipe spawn B()",
            "fnc main():int{return 42;} \"ActorUtils::pipe(A,B)\""
        })
        {
            ActorPubSubSourceAdmission.RejectUnsupported(inert);
        }

        Console.WriteLine(
            "PASS .NET pub-sub source corpus fails closed before scalar/structured lowering");
    }

    private static void RequirePipeRejected(Action action, string scenario)
    {
        try { action(); }
        catch (FormatException error)
            when (error.Message.Contains(
                ActorPubSubSourceAdmission.PipeDiagnostic, StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Actor pipe source admission regression: " + scenario);
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
