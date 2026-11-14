using Mono.Cecil;
using Ores.Net.Compiler;

// This is compiler-produced checked scalar-handler CIL, NOT a two-actor
// runtime or Native AOT guest execution proof.
internal static class ActorPipeSourcePlanV1SelfTest
{
    public static void Run()
    {
        string guest = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "actor-pipe-two-stage.atto"));
        var plan = OresStructuredParser.PlanActorPipeV1(guest);
        Require(plan.Stages.Count == 2, "only two actors are admitted");
        Require(plan.Stages[0].Binding == "producer" &&
                plan.Stages[0].ActorType == "Producer" &&
                plan.Stages[0].MessageInput == "int" &&
                plan.Stages[0].MessageOutput == "int", "typed producer");
        Require(plan.Stages[1].Binding == "consumer" &&
                plan.Stages[1].ActorType == "Consumer" &&
                plan.Stages[1].MessageInput == "int" &&
                plan.Stages[1].MessageOutput == "int", "typed consumer");
        Require(plan.Edge.ProducerBinding == "producer" &&
                plan.Edge.ConsumerBinding == "consumer" &&
                plan.InputMessage == 2, "owned edge and literal admission");
        Require(ActorPipeGraphV1.Evidence == "TYPED_SCALAR_ORACLE_ONLY_NOT_ACTOR_EXECUTION",
            "must never certify actor execution via scalar oracle");

        // Reuse the REAL verified structured-CFG -> CIL emitter: no hand-built
        // arithmetic oracle, no host delegate pretending to be an actor.
        // The CIL interpreter is a test-only reference, NOT NativeAOT.
        var outputDir = Path.Combine(Path.GetTempPath(),
            "ores-actor-pipe-scalar-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        try
        {
            string assemblyPath = Path.Combine(outputDir, "OresGenerated.dll");
            CilEmitter.EmitLibrary(
                OresStructuredCfgLowering.LowerModule(plan.ScalarOracleSource),
                assemblyPath);
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            var generated = assembly.MainModule.Types.Single(
                t => t.Name == "OresProgram");
            Require(generated.Methods.Count == 3,
                "two compiler-produced scalar methods and one scalar Run");
            long scalarResult = CfgCilTestInterpreter.Evaluate(
                generated.Methods.Single(m => m.Name == "Run"));
            Require(scalarResult == 6,
                "typed handler scalar-oracle CIL must return six (not actor delivery)");
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }

        foreach (var (change, label) in new (string Source, string Label)[]
        {
            (guest.Replace("producer pipe consumer;", "consumer pipe producer;",
                StringComparison.Ordinal), "reverse pipe edge"),
            (guest.Replace("producer pipe consumer;", "producer pipe producer;",
                StringComparison.Ordinal), "self-cycle"),
            (guest.Replace("producer pipe consumer;", "producer pipe consumer; producer pipe consumer;",
                StringComparison.Ordinal), "duplicate links"),
            (guest.Replace("let consumer = spawn Consumer();",
                "let consumer = spawn Producer();", StringComparison.Ordinal), "reused actor type"),
            (guest.Replace("let consumer = spawn Consumer();",
                "let consumer = spawn Unknown();", StringComparison.Ordinal), "unknown actor"),
            (guest.Replace("producer.send(2);", "consumer.send(2);",
                StringComparison.Ordinal), "unauthorized direct consumer send"),
            (guest.Replace("producer.send(2);",
                "producer.send(2); producer.send(3);", StringComparison.Ordinal),
                "multiple ingress messages"),
            (guest.Replace("stop producer;", "stop consumer;",
                StringComparison.Ordinal), "invalid stop order"),
            (guest.Replace("on_message(int n): int { return n + 1; }",
                "on_message(int n): bool { return true; }", StringComparison.Ordinal),
                "pipe nominal type mismatch"),
            (guest.Replace("on_message(int n): int { return n + 1; }",
                "on_message(int n): int { return n + true; }", StringComparison.Ordinal),
                "invalid typed actor handler"),
            (guest.Replace("return 0;", "return 1;", StringComparison.Ordinal),
                "unsupported main return"),
            (guest.Replace("producer pipe consumer;", "producer |> consumer;",
                StringComparison.Ordinal), "functional pipe cannot become actor graph"),
        })
        {
            Require(change != guest, "negative mutation must change the fixture: " + label);
            try { _ = OresStructuredParser.PlanActorPipeV1(change); }
            catch (FormatException error) when (
                error.Message.Contains("E_ACTOR_PIPE_PLAN_V1", StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("Unchecked actor source graph escaped: " + label);
        }

        // Inert comments cannot synthesize an edge or damage a typed plan.
        string withComments = guest.Replace("producer pipe consumer;",
            "producer /* outer /* nested */ closed */ pipe /* second */ consumer;",
            StringComparison.Ordinal);
        Require(OresStructuredParser.PlanActorPipeV1(withComments).InputMessage == 2,
            "comment-separated pipe remains the same typed edge");

        // Crucial: the existing ordinary compilers still fail closed. This
        // narrow preview is not a backdoor to generated actor CIL or NativeAOT.
        RejectProduction(() => OresParser.Parse(guest), "shipping compiler");
        RejectProduction(() => OresStructuredParser.Parse(guest), "structured compiler");

        Console.WriteLine("PASS actor pipe V1 typed source plan + checked scalar handler CIL oracle " +
            "(NOT_NATIVE_AOT_ACTOR_EXECUTION)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Actor pipe V1: " + message);
    }

    private static void RejectProduction(Action action, string reason)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Actor pipe V1: " + reason +
            " admitted a source pipeline without typed runtime lowering");
    }
}
