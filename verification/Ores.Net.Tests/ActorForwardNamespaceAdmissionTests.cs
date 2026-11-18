using Ores.Net.Compiler;

/// <summary>
/// Compiler-backed negative admission for multi-actor forwards and namespace
/// headers. This is NOT runtime parity or proof of NativeAOT actors.
/// </summary>
internal static class ActorForwardNamespaceAdmissionTests
{
    private static void Reject(string source, string diagnostic)
    {
        foreach (Func<string, object> compile in new Func<string, object>[] {
            text => OresStructuredParser.Parse(text),
            text => OresParser.Parse(text)
        })
        {
            try { _ = compile(source); }
            catch (FormatException error) when (
                error.Message.Contains(diagnostic, StringComparison.Ordinal) &&
                error.Message.Contains("offset", StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("NativeAOT compiler admitted unsupported source or returned "
                + "the wrong capability diagnostic: " + diagnostic);
        }
    }

    public static void Run()
    {
        string corpus = Path.Combine(AppContext.BaseDirectory,
            "contracts", "actor-forwards-v1");
        Reject(File.ReadAllText(Path.Combine(corpus, "multi-actor-namespace.atto")),
            "NAMESPACE_RUNTIME_NATIVEAOT_UNSUPPORTED");
        Reject(File.ReadAllText(Path.Combine(corpus, "multi-actor-no-namespace.atto")),
            "ACTOR_FORWARD_NATIVEAOT_UNSUPPORTED");
        Reject("declare actor Foo1; declare actor Foo2;",
            "ACTOR_FORWARD_NATIVEAOT_UNSUPPORTED");
        Reject("declare pub actor Foo1; define actor Foo1 as end",
            "ACTOR_FORWARD_NATIVEAOT_UNSUPPORTED");
        Reject("pub declare actor Foo1; define actor Foo1 as end",
            "ACTOR_FORWARD_NATIVEAOT_UNSUPPORTED");
        Reject("declare isoactor Foo1; define isoactor Foo1 as end",
            "ACTOR_FORWARD_NATIVEAOT_UNSUPPORTED");
        Reject("use namespace independent; fnc main():int{return 42;}",
            "NAMESPACE_RUNTIME_NATIVEAOT_UNSUPPORTED");

        const string inert = "fnc main():int{/* declare actor Foo; use namespace fake; */ return 42;}";
        if (OresStructuredParser.Parse(inert).Body.Count != 1 ||
            OresParser.Parse(inert) is null)
            throw new Exception("Comments must not trigger actor-forward or namespace gates");
    }
}
