using Ores.Net.Compiler;

// Compiler semantic contract ONLY. Passing this test is not real .atto
// actor-to-actor CIL execution and is not Native AOT guest parity.
internal static class ActorEmitPipeContractV3SelfTest
{
    private static ActorEmitStageV3 Stage(string id, string actor, string input,
        params string[] emits) => new(id, actor, input, emits);

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception("ActorEmitPipeV3: " + message);
    }

    private static void Reject(IReadOnlyList<ActorEmitStageV3> stages,
        string code)
    {
        try { _ = ActorEmitPipeContractV3.Validate(stages); }
        catch (FormatException e) when (
            e.Message.StartsWith(code + ":", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("ActorEmitPipeV3 unexpectedly admitted: " + code);
    }

    public static void Run()
    {
        var scalar = ActorEmitPipeContractV3.Validate(new[]
        {
            Stage("p0", "Parse", "int", "int"),
            Stage("v0", "Validate", "int", "int", "int"),
            Stage("s0", "Sink", "int")
        });
        Require(scalar.Count == 2 && scalar[0] ==
            new ActorEmitLinkV3("p0", "v0", "int") &&
            scalar[1] == new ActorEmitLinkV3("v0", "s0", "int"),
            "scalar source-to-sink chain");

        var boolLinks = ActorEmitPipeContractV3.Validate(new[]
        {
            Stage("parse-0", "Worker", "int", "bool"),
            Stage("validate-1", "Worker", "bool", "bool"),
            Stage("sink-2", "Sink", "bool")
        });
        Require(boolLinks.Count == 2 &&
                boolLinks[0].PayloadType == "bool" &&
                boolLinks[1].PayloadType == "bool",
            "output type must come from emit(expr), not incoming ActorMail type");

        Reject(new[] { Stage("a", "Parse", "int", "int", "bool"),
            Stage("b", "Sink", "int") }, "E_ACTOR_PIPE_EMIT_TYPE");
        Reject(new[] { Stage("a", "Parse", "int", "bool"),
            Stage("b", "Sink", "int") }, "E_ACTOR_PIPE_EMIT_TYPE");
        Reject(new[] { Stage("a", "Sink", "int"),
            Stage("b", "Validate", "int") }, "E_ACTOR_PIPE_STAGE_NO_EMIT");
        Reject(new[] { Stage("a", "Parse", "int", "int"),
            Stage("b", "Sink", "int", "int") }, "E_ACTOR_PIPE_SINK_EMIT");
        Reject(new[] { Stage("same", "Parse", "int", "int"),
            Stage("same", "Sink", "int") }, "E_ACTOR_PIPE_DUPLICATE_INSTANCE");
        Reject(new[] { Stage("a", "Parse", "int", "int"),
            new ActorEmitStageV3("b", "Supervisor", "int",
                Array.Empty<string>(), supervisor: true) }, "E_ACTOR_PIPE_SUPERVISOR");
        Reject(new[] { Stage("a", "Parse", "int", "int") }, "E_ACTOR_PIPE_LIMIT");

        var tooMany = Enumerable.Range(0, 17)
            .Select(i => Stage("instance" + i, "Worker", "int",
                i == 16 ? Array.Empty<string>() : new[] { "int" })).ToArray();
        Reject(tooMany, "E_ACTOR_PIPE_LIMIT");

        static void ConstructorReject(Action action)
        {
            try { action(); }
            catch (FormatException) { return; }
            throw new Exception("ActorEmitPipeV3 constructor accepted invalid identity/domain");
        }
        ConstructorReject(() => ActorEmitPipeContractV3.Validate(new[]
        {
            Stage("", "Parse", "int", "int"), Stage("b", "Sink", "int")
        }));
        ConstructorReject(() => ActorEmitPipeContractV3.Validate(new[]
        {
            Stage("a", new string((char)0xD800, 1), "int", "int"), Stage("b", "Sink", "int")
        }));
        ConstructorReject(() => ActorEmitPipeContractV3.Validate(new[]
        {
            Stage(new string('x', 257), "Parse", "int", "int"),
            Stage("b", "Sink", "int")
        }));
        ConstructorReject(() => ActorEmitPipeContractV3.Validate(new[]
        {
            Stage("a", "Parse", "int", "float"), Stage("b", "Sink", "int")
        }));
        ConstructorReject(() => ActorEmitPipeContractV3.Validate(new[]
        {
            Stage("a", "Parse", "int", "int"), Stage("b", "Sink", "string")
        }));

        for (int length = 2; length <= 16; length++)
        {
            var chain = Enumerable.Range(0, length)
                .Select(i => Stage("instance-" + i, "Worker",
                    i == 0 ? "int" : "bool",
                    i == length - 1 ? Array.Empty<string>() : new[] { "bool" }))
                .ToArray();
            Require(ActorEmitPipeContractV3.Validate(chain).Count == length - 1,
                "v3 stage limit positive");
            chain[1] = Stage("instance-1", "Worker", "int",
                length == 2 ? Array.Empty<string>() : new[] { "bool" });
            Reject(chain, "E_ACTOR_PIPE_EMIT_TYPE");
        }

        var unsafeList = new List<string> { "int" };
        var frozen = new ActorEmitStageV3("a", "Worker", "int", unsafeList);
        unsafeList.Add("bool");
        Require(frozen.EmitTypes.Count == 1 && frozen.EmitTypes[0] == "int",
            "parser-owned mutable emit list must be copied");
        Require(frozen.EmitTypes is not string[], "typed observations must not expose writable array");

        Console.WriteLine("ACTOR_PIPE_V3_TYPED_EMIT_CONTRACT_PASS|java_llvm_net|"
            + "HOST_COMPILER_ONLY_NOT_GUEST_EXECUTION");
    }
}
