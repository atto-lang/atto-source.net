using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

internal static class NominalEnumV1GuestTests
{
    internal const string Source = """
        define enum Light as
          Red = 1
          Green = 2
          Yellow = 3
        end
        pub fnc main(): int {
          val Light active = Light.Green;
          match active over
            on Light.Red -> { return 10; }
            on Light.Green -> { return 20; }
            on Light.Yellow -> { return 30; }
          end
        }
        """;

    public static void Run()
    {
        var verified = OresNominalEnumV1.LowerModule(Source);
        if (verified.Helpers.Count != 0 || verified.Main.Name != "main" ||
            CfgReferenceEvaluator.Evaluate(verified.Main) != 20)
            throw new Exception("ADT v1 typed guest source/CFG differs from expected result");

        // Unlike a host-only descriptor model, this is actual independently
        // verified guest CFG serialized to CIL with runtime dispatch/compare.
        string dir = Path.Combine(Path.GetTempPath(),
            "atto-adt-nominal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a", "OresGenerated.dll");
            string b = Path.Combine(dir, "b", "OresGenerated.dll");
            CilEmitter.EmitLibrary(verified, a);
            CilEmitter.EmitLibrary(verified, b);
            if (!File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)))
                throw new Exception("ADT v1 emits nondeterministic CIL");
            using var asm = AssemblyDefinition.ReadAssembly(a);
            var run = asm.MainModule.Types.Single(type =>
                type.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(method => method.Name == "Run");
            if (!run.IsPublic || !run.IsStatic ||
                run.ReturnType.MetadataType != MetadataType.Int64 ||
                run.Parameters.Count != 0)
                throw new Exception("ADT v1 returned wrong CIL entry ABI");
            var instructions = run.Body.Instructions;
            if (!instructions.Any(i => i.OpCode.Code == Code.Ceq) ||
                !instructions.Any(i => i.OpCode.Code is Code.Brtrue or Code.Brtrue_S))
                throw new Exception("ADT v1 source match did not lower into native CIL comparisons and branches");
            if (CfgCilTestInterpreter.Evaluate(run) != 20)
                throw new Exception("ADT v1 emitted CIL result differs from verified source");
        }
        finally { Directory.Delete(dir, recursive: true); }

        int rejects = 0;
        void Reject(string name, string source, string diagnostic)
        {
            try { _ = OresNominalEnumV1.LowerModule(source); }
            catch (FormatException ex) when (ex.Message.Contains(diagnostic,
                StringComparison.OrdinalIgnoreCase))
            {
                rejects++;
                return;
            }
            throw new Exception("ADT v1 unexpectedly admitted or misclassified " + name);
        }
        Reject("foreign constructor owner",
            Source.Replace("Light.Green;", "Other.Green;", StringComparison.Ordinal),
            "foreign nominal owner");
        Reject("unknown constructor case",
            Source.Replace("Light.Green;", "Light.Blue;", StringComparison.Ordinal),
            "unknown nominal constructor");
        Reject("foreign match owner",
            Source.Replace("on Light.Red", "on Other.Red", StringComparison.Ordinal),
            "foreign nominal owner");
        Reject("missing match case",
            Source.Replace("on Light.Yellow -> { return 30; }", "", StringComparison.Ordinal),
            "nonexhaustive");
        Reject("duplicate match arm",
            Source.Replace("on Light.Red", "on Light.Green", StringComparison.Ordinal),
            "duplicate case arm");
        Reject("duplicate case name",
            Source.Replace("Yellow = 3", "Green = 3", StringComparison.Ordinal),
            "duplicate nominal case");
        Reject("duplicate tag number",
            Source.Replace("Yellow = 3", "Yellow = 2", StringComparison.Ordinal),
            "duplicate/invalid");
        Reject("tag zero",
            Source.Replace("Red = 1", "Red = 0", StringComparison.Ordinal),
            "positive signed i32");
        Reject("tag overflow",
            Source.Replace("Red = 1", "Red = 2147483648", StringComparison.Ordinal),
            "positive signed i32");
        Reject("raw integer fakes nominal value",
            Source.Replace("Light.Green;", "2;", StringComparison.Ordinal),
            "unreserved identifier");
        Reject("payload enum requires checked layout",
            Source.Replace("Green = 2", "Green: int = 2", StringComparison.Ordinal),
            "unreserved identifier");
        Reject("guarded match cannot prove coverage",
            Source.Replace("on Light.Red ->", "on Light.Red when true ->", StringComparison.Ordinal),
            "expected '->'");
        Reject("no public enum parameter ABI",
            Source.Replace("main(): int", "main(Light a): int", StringComparison.Ordinal),
            "expected ')'");
        Reject("no source setter/mutable enum",
            Source.Replace("val Light active", "let Light active", StringComparison.Ordinal),
            "expected 'val'");
        Reject("no extra guest source after verified entry",
            Source + "define enum Other as Case = 1 end", "expected ''");

        // Hidden keywords must not change which program tokens are parsed.
        var commented = "/* define enum Fake as Green = 1 end */\n" +
            Source + "\n// on Fake.Green -> { return 99; }\n";
        var c = OresNominalEnumV1.LowerModule(commented);
        if (CfgReferenceEvaluator.Evaluate(c.Main) != 20)
            throw new Exception("ADT v1 lexer admitted a nominal symbol from trivia");

        Console.WriteLine($"ATTO_NET_NOMINAL_ENUM_V1_SOURCE_CFG_CIL_PASS|rejects={rejects}|guest_result=20");
    }
}
