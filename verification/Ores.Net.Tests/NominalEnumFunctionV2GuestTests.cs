using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

// This is a source-level nominal signature proof + real private static
// MethodDef ABI proof. General CFG types remain I64/Bool until nominal
// types are represented natively in the shared compiler IR.
internal static class NominalEnumFunctionV2GuestTests
{
    internal const string Source = """
        define enum Light as
          Red = 1
          Green = 2
          Yellow = 3
        end
        fnc next(Light state): Light {
          match state over
            on Light.Red -> { return Light.Green; }
            on Light.Green -> { return Light.Yellow; }
            on Light.Yellow -> { return Light.Red; }
          end
        }
        pub fnc main(): int {
          val Light active = next(Light.Red);
          match active over
            on Light.Red -> { return 10; }
            on Light.Green -> { return 0; }
            on Light.Yellow -> { return 30; }
          end
        }
        """;

    public static void Run()
    {
        long[] expected = { 0, 30, 10 };
        string[] constructors = { "Light.Red", "Light.Green", "Light.Yellow" };
        int guestCases = 0;
        for (int index = 0; index < constructors.Length; index++)
        {
            string source = Source.Replace("next(Light.Red)", "next(" + constructors[index] + ")",
                StringComparison.Ordinal);
            var module = OresNominalEnumV1.LowerModule(source);
            if (module.Helpers.Count != 1 || module.Main.Name != "main" ||
                module.Helpers[0].Name != "next" ||
                module.Helpers[0].Parameters.Count != 1 ||
                module.Helpers[0].ResultType != IrValueType.I64 ||
                !module.Main.Blocks.SelectMany(b => b.Instructions)
                    .Any(i => i.Opcode == CfgOpcode.Call && i.Target == "next"))
                throw new Exception("ADT v2 missing independently verified static enum method call");

            string folder = Path.Combine(Path.GetTempPath(),
                "atto-adt-fn-v2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string first = Path.Combine(folder, "a", "OresGenerated.dll");
                string second = Path.Combine(folder, "b", "OresGenerated.dll");
                CilEmitter.EmitLibrary(module, first);
                CilEmitter.EmitLibrary(module, second);
                if (!File.ReadAllBytes(first).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(second)))
                    throw new Exception("ADT v2 emitted nondeterministic static method metadata");

                using var asm = AssemblyDefinition.ReadAssembly(first);
                var type = asm.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram");
                var run = type.Methods.Single(m => m.Name == "Run");
                var helper = type.Methods.Single(m => m.Name == "helper_next");
                if (!helper.IsPrivate || !helper.IsStatic ||
                    helper.ReturnType.MetadataType != MetadataType.Int64 ||
                    helper.Parameters.Count != 1 ||
                    helper.Parameters[0].ParameterType.MetadataType != MetadataType.Int64)
                    throw new Exception("ADT v2 private MethodDef ABI mismatch");
                if (!run.Body.Instructions.Any(i => i.OpCode.Code == Code.Call &&
                    i.Operand is MethodDefinition m && m.Name == "helper_next"))
                    throw new Exception("ADT v2 CIL did not call the helper MethodDef");
                if (CfgCilTestInterpreter.Evaluate(run) != expected[index])
                    throw new Exception("ADT v2 actual emitted-CIL result mismatch");
            }
            finally { Directory.Delete(folder, recursive: true); }
            guestCases++;
        }

        int rejects = 0;
        void Reject(string label, string source, string diagnostic)
        {
            try { _ = OresNominalEnumV1.LowerModule(source); }
            catch (FormatException ex) when (ex.Message.Contains(diagnostic,
                StringComparison.OrdinalIgnoreCase))
            {
                rejects++;
                return;
            }
            throw new Exception("ADT v2 nominal checker admitted or misclassified: " + label);
        }
        Reject("foreign helper parameter owner",
            Source.Replace("next(Light state)", "next(Other state)", StringComparison.Ordinal),
            "expected 'Light'");
        Reject("foreign helper return owner",
            Source.Replace("): Light {", "): Other {", StringComparison.Ordinal),
            "expected 'Light'");
        Reject("helper foreign pattern",
            Source.Replace("on Light.Red -> { return Light.Green; }",
                "on Other.Red -> { return Light.Green; }", StringComparison.Ordinal),
            "helper pattern has foreign nominal owner");
        Reject("helper duplicate arm",
            Source.Replace("on Light.Yellow -> { return Light.Red; }",
                "on Light.Green -> { return Light.Red; }", StringComparison.Ordinal),
            "helper has duplicate nominal match case");
        Reject("helper nonexhaustive",
            Source.Replace("on Light.Yellow -> { return Light.Red; }", "",
                StringComparison.Ordinal), "helper has nonexhaustive");
        Reject("helper wrong owner return",
            Source.Replace("return Light.Yellow;", "return Other.Yellow;",
                StringComparison.Ordinal), "helper return has foreign nominal owner");
        Reject("helper unknown case return",
            Source.Replace("return Light.Yellow;", "return Light.Blue;",
                StringComparison.Ordinal), "helper returns unknown nominal case");
        Reject("helper raw integer return",
            Source.Replace("return Light.Yellow;", "return 3;",
                StringComparison.Ordinal), "unreserved identifier");
        Reject("helper raw integer argument",
            Source.Replace("next(Light.Red)", "next(1)", StringComparison.Ordinal),
            "unreserved identifier");
        Reject("helper wrong enum call argument",
            Source.Replace("next(Light.Red)", "next(Other.Red)", StringComparison.Ordinal),
            "foreign nominal owner");
        Reject("helper missing argument",
            Source.Replace("next(Light.Red)", "next()", StringComparison.Ordinal),
            "unreserved identifier");
        Reject("helper wrong match binder",
            Source.Replace("match state over", "match other over",
                StringComparison.Ordinal), "expected 'state'");
        Reject("helper duplicate declaration",
            Source.Replace("pub fnc main", "fnc extra(Light v): Light { } pub fnc main",
                StringComparison.Ordinal), "expected 'pub'");
        Reject("helper public nominal ABI",
            Source.Replace("fnc next(", "pub fnc next(", StringComparison.Ordinal),
            "expected 'main'");
        Reject("helper wrongly tagged return",
            Source.Replace("return Light.Yellow;", "return Light.Red(2);",
                StringComparison.Ordinal), "expected ')'");
        Reject("main foreign tag return",
            Source.Replace("on Light.Red -> { return 10; }",
                "on Other.Red -> { return 10; }", StringComparison.Ordinal),
            "match uses a foreign nominal owner");

        Console.WriteLine($"ATTO_NET_NOMINAL_FN_V2_CIL_PASS|cases={guestCases}|rejects={rejects}|result=0");
    }
}
