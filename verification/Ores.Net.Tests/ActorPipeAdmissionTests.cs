using Ores.Net.Compiler;

// Parser/admission checks only. These tests do NOT certify .atto actor guests,
// supervisor CONTROL, delivery/backpressure or Native AOT execution.
internal static class ActorPipeAdmissionTests
{
    public static void Run()
    {
        var rejected = new (string Expression, string Operator)[]
        {
            ("p pipe c", "pipe"),
            ("spawn Producer() pipe spawn Consumer()", "pipe"),
            ("Producer() pipe Consumer()", "pipe"),
            ("(1+2) pipe (3+4)", "pipe"),
            ("p /* first */ pipe /* second */ c", "pipe"),
            ("p /* one /* nested */ two */ pipe c", "pipe"),
            ("p pipe c pipe d", "pipe"),
            ("Actor.pipe(Producer(),Consumer())", "pipe"),
            ("Actor /* gap */ . /* gap */ pipe(Producer(),Consumer())", "pipe"),
            ("Actor::pipe(Producer(),Consumer())", "pipe"),
            ("ActorUtils.pipe(Producer(),Consumer())", "pipe"),
            ("ActorUtils::pipe(Producer(),Consumer())", "pipe"),
            ("Actor.connect(Producer(),Consumer())", "connect"),
            ("Actor::connect(Producer(),Consumer())", "connect")
        };

        foreach (var (expression, keyword) in rejected)
        {
            string source = "fnc main():int{return " + expression + ";}";
            int at = source.IndexOf(keyword, StringComparison.Ordinal);
            AssertRejected(() => OresParser.Parse(source), at, "shipping scalar: " + expression);
            AssertRejected(() => OresStructuredParser.Parse(source), at,
                "structured CFG: " + expression);
        }

        // Infix and factory-looking forms in comments cannot grant a capability
        // or spuriously block ordinary numeric CIL. Comments are lexed away.
        foreach (string source in new[]
        {
            "fnc main():int{return 42;} // spawn A() pipe spawn B()",
            "fnc main():int{/* Actor.pipe(Producer(),Consumer()) */return 42;}",
            "fnc main():int{/* p /* inner pipe */ pipe q */return 42;}"
        })
        {
            _ = OresParser.Parse(source);
            _ = OresStructuredParser.Parse(source);
        }

        // Contextual spelling is not a globally reserved identifier. This
        // control must continue to parse in the structured helper/local subset.
        _ = OresStructuredParser.Parse(
            "fnc pipe():int{return 41;}fnc main():int{return pipe()+1;}");
        _ = OresStructuredParser.Parse(
            "fnc main():int{let int pipe=41;return pipe+1;}");

        // The full guest fixture is a future implementation target, not a
        // passing pipeline. Both .NET source compiler entry points deny it.
        string guest = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "actor-pipe-two-stage.atto"));
        AssertNotAccepted(() => OresParser.Parse(guest), "shipping .atto guest");
        AssertNotAccepted(() => OresStructuredParser.Parse(guest), "structured .atto guest");
    }

    private static void AssertRejected(Action parse, int offset, string label)
    {
        try { parse(); }
        catch (FormatException error)
        {
            if (!error.Message.Contains("E_ACTOR_PIPE_BACKEND_UNSUPPORTED",
                    StringComparison.Ordinal) ||
                !error.Message.Contains($"offset {offset}", StringComparison.Ordinal))
                throw new Exception(label + ": wrong diagnostic/offset: " + error.Message);
            return;
        }
        throw new Exception(label + ": unsupported actor pipe reached parser acceptance");
    }

    private static void AssertNotAccepted(Action parse, string label)
    {
        try { parse(); }
        catch (FormatException) { return; }
        throw new Exception(label + ": unsupported actor source reached parser acceptance");
    }
}
