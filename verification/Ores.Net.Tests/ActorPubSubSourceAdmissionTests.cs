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
        // Pin the identical .atto negative corpus used by LLVM, Wasm, JS,
        // HDL and platform conformance. This is a source-only rejection,
        // not a compiled guest or an advertised Topic<T> implementation.
        var fixturePath = Path.Combine(AppContext.BaseDirectory,
            "contracts", "atto-ensemble-v1",
            "canonical-unlowered-try-publish.atto");
        byte[] fixtureBytes = File.ReadAllBytes(fixturePath);
        byte[] header = System.Text.Encoding.ASCII.GetBytes(
            "blob " + fixtureBytes.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + "\0");
        byte[] gitBlobInput = new byte[header.Length + fixtureBytes.Length];
        Buffer.BlockCopy(header, 0, gitBlobInput, 0, header.Length);
        Buffer.BlockCopy(fixtureBytes, 0, gitBlobInput, header.Length, fixtureBytes.Length);
        string gitBlob = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(gitBlobInput)).ToLowerInvariant();
        if (gitBlob != "c562ad380bb283da9ffa26160653c1186ab63462")
            throw new Exception("Ensemble source fixture Git blob identity mismatch");
        string canonicalSource = new System.Text.UTF8Encoding(false, true)
            .GetString(fixtureBytes);
        RequireRejected(
            () => ActorPubSubSourceAdmission.RejectUnsupported(canonicalSource),
            "exact cross-backend alias-only share! try_publish fixture");
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

        // Canonical Atto Ensemble publication is NOT yet supported by the
        // .NET compiler. Bare member syntax must not bypass the actor.group
        // and Topic<T> gates, including both ownership aliases and postfix ?.
        foreach (string source in new[] {
            "val receipt = topic.try_publish(share! event)?;",
            "val receipt = topic.try_publish(share!event);",
            "val receipt = topic.try_publish(rt share event);",
            "val receipt = runtime_topic.try_publish(share! event)?;",
            "val receipt = topic /* nested /* c */ c */ . try_publish(share! event)?;",
            "val receipt = topic?.try_publish(share! event)?;",
            "val receipt = other.topic.try_publish(rt share event)?;",
            "val member = topic.try_publish;",
            "val callable = topic.try_publish;",
            "val rejected = topic.try_publish<String>(share! event);"
        })
        {
            RequireRejected(
                () => ActorPubSubSourceAdmission.RejectUnsupported(source),
                "canonical typed Topic.try_publish preflight " + source);
        }

        foreach (string inert in new[] {
            "fnc try_publish(event:int):int{return event;}",
            "fnc main():int{return 42;} // topic.try_publish(share! event)?",
            "fnc main():int{return 42;} /* topic.try_publish(rt share event) */",
            "fnc main():int{return 42;} \"topic.try_publish(share! event)?\"",
            "fnc main():int{return 42;} /* nested /* topic.try_publish(share! event)? */ end */",
            "fnc main():int{return 42;} // topic.try_publish(share! event)?\\r\\n",
            "fnc try_publisher(event:int):int{return event;}"
        })
            ActorPubSubSourceAdmission.RejectUnsupported(inert);

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
