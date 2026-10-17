using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

static class LambdaTextSourceSelfTest
{
    private const string Fixture = """
        define module handler as
          // Preserve the existing provider-neutral Oreslang Lambda shape.
          pub fnc invoke(String event): String {
            if event == "ping"; then return "pong"; fi
            return "unsupported";
          }
        end
        """;

    public static void Run()
    {
        ParseAndReferenceEvaluate();
        RejectUnsupportedShapes();
        EmitDeterministicTypedCil();
    }

    private static void ParseAndReferenceEvaluate()
    {
        var program = OresLambdaTextParser.Parse(Fixture);
        Require(program.ModuleName == "handler", "Lambda module identity");
        Require(program.FunctionName == "invoke", "Lambda function identity");
        Require(program.ParameterName == "event", "Lambda parameter identity");
        Require(program.Arms.Count == 1, "Lambda guard count");
        Require(program.Arms[0] == new LambdaTextArm("ping", "pong"),
            "Lambda guard semantics");
        Require(program.Invoke("ping") == "pong", "reference ping response");
        Require(program.Invoke("other") == "unsupported",
            "reference fallback response");

        var topLevel = OresLambdaTextParser.Parse(
            "define pub fnc invoke(String event) -> String {" +
            "return \"ok\"; }");
        Require(topLevel.ModuleName is null && topLevel.Invoke("x") == "ok",
            "top-level canonical arrow form");
    }

    private static void RejectUnsupportedShapes()
    {
        Reject(
            "fnc invoke(String event): String { return \"x\"; }",
            "Lambda entry must be public");
        Reject(
            "pub fnc invoke(String event): String {" +
            "if other == \"x\"; then return \"y\"; fi return \"z\"; }",
            "condition cannot read undeclared identifier");
        Reject(
            "pub fnc invoke(String event): String { return event; }",
            "arbitrary expression return is not admitted");
        Reject(
            "pub fnc invoke(String event): String { return \"\\q\"; }",
            "unsupported escape is rejected");
        Reject(
            "pub fnc invoke(String event): String {" +
            "return \"x\"; } trailing",
            "trailing source is rejected");
    }

    private static void EmitDeterministicTypedCil()
    {
        var program = OresLambdaTextParser.Parse(Fixture);
        var folder = Path.Combine(
            Path.GetTempPath(),
            "oresnet-lambda-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "OresGenerated.dll");
            var second = Path.Combine(folder, "OresGenerated.repro.dll");
            LambdaTextCilEmitter.EmitLibrary(program, first);
            LambdaTextCilEmitter.EmitLibrary(program, second);
            Require(
                File.ReadAllBytes(first).AsSpan().SequenceEqual(
                    File.ReadAllBytes(second)),
                "Lambda generated CIL is byte-for-byte deterministic");

            using var assembly = AssemblyDefinition.ReadAssembly(first);
            var type = assembly.MainModule.Types.Single(
                t => t.FullName == "Oreslang.Generated.OresProgram");
            var invoke = type.Methods.Single(m => m.Name == "Invoke");
            Require(invoke.IsPublic && invoke.IsStatic,
                "Lambda CIL entry is public static");
            Require(invoke.ReturnType.MetadataType == MetadataType.String,
                "Lambda CIL return is String");
            Require(invoke.Parameters.Count == 1 &&
                    invoke.Parameters[0].Name == "event" &&
                    invoke.Parameters[0].ParameterType.MetadataType ==
                        MetadataType.String,
                "Lambda CIL parameter is named String event");
            Require(!type.Methods.Any(m => m.Name == "Run"),
                "Lambda artifact cannot masquerade as scalar artifact");

            var instructions = invoke.Body.Instructions;
            Require(instructions.Any(i => i.OpCode.Code == Code.Ldarg_0),
                "Lambda CIL reads payload argument");
            Require(instructions.Count(i => i.OpCode.Code == Code.Ldstr) == 3,
                "Lambda CIL emits match, response and fallback strings");
            Require(instructions.Any(i =>
                    i.OpCode.Code is Code.Brfalse or Code.Brfalse_S),
                "Lambda CIL branches on string guard");
            Require(instructions.Count(i => i.OpCode.Code == Code.Ret) == 2,
                "Lambda CIL returns from guarded and fallback paths");

            var calls = instructions
                .Where(i => i.OpCode.Code == Code.Call)
                .Select(i => i.Operand)
                .OfType<MethodReference>()
                .ToArray();
            Require(calls.Length == 1 &&
                    calls[0].DeclaringType.FullName == "System.String" &&
                    calls[0].Name == "op_Equality",
                "Lambda CIL only calls ordinal CLR string equality primitive");

            var literals = instructions
                .Where(i => i.OpCode.Code == Code.Ldstr)
                .Select(i => (string)i.Operand)
                .ToArray();
            Require(literals.SequenceEqual(
                    new[] { "ping", "pong", "unsupported" }),
                "Lambda CIL preserves exact source literals");

            var changed = Path.Combine(folder, "OresGenerated.changed.dll");
            LambdaTextCilEmitter.EmitLibrary(
                OresLambdaTextParser.Parse(
                    "pub fnc invoke(String event): String {" +
                    "return \"different\"; }"),
                changed);
            Require(
                !File.ReadAllBytes(first).AsSpan().SequenceEqual(
                    File.ReadAllBytes(changed)),
                "Lambda literal changes alter deterministic artifact identity");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void Reject(string source, string why)
    {
        try { _ = OresLambdaTextParser.Parse(source); }
        catch (FormatException) { return; }
        throw new Exception("Expected Lambda parser rejection: " + why);
    }

    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("FAILED: " + why);
    }
}
