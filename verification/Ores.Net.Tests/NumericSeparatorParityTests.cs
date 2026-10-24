using Mono.Cecil;
using Ores.Net.Compiler;

public static class NumericSeparatorParityTests
{
    public static void Run()
    {
        static void Require(bool ok, string message)
        {
            if (!ok) throw new Exception("numeric separator parity: " + message);
        }

        static long Evaluate(string expression)
        {
            var ast = OresParser.Parse("pub fnc main():int{return " + expression + ";}");
            return OresIrReferenceEvaluator.Evaluate(OresIrLowering.Lower(ast));
        }

        foreach (var (expression, expected) in new (string, long)[] {
            ("40_0-35_8", 42),
            ("+(1_0+3_2)", 42),
            ("2_1*2", 42),
            ("+0_0_4_2", 42),
            ("9_223_372_036_854_775_807", long.MaxValue),
            ("-9_223_372_036_854_775_808", long.MinValue),
            ("0_0_0", 0)
        })
        {
            Require(Evaluate(expression) == expected, "typed SSA value: " + expression);
            var structured = OresStructuredParser.Parse(
                "pub fnc main():int{return " + expression + ";}");
            Require(structured.Body.Count == 1 && structured.Body[0] is StructuredReturn,
                "structured admission matches shipping parser: " + expression);
        }
        var structuredMin = OresStructuredParser.Parse(
            "pub fnc main():int{return -9_223_372_036_854_775_808;}");
        Require(structuredMin.Body[0] is StructuredReturn {
            Value: StructuredInt { Value: long.MinValue }
        }, "structured parser retains signed minimum");

        // Bad separators must fail before OresIR or Native AOT emission.
        foreach (var invalid in new[] {
            "1_", "1__0", "1_a", "1_+2", "1_ 2", "10__20",
            "+9_223_372_036_854_775_808", "-9_223_372_036_854_775_809",
            "9_223_372_036_854_775_808"
        })
        {
            try
            {
                _ = OresParser.Parse("pub fnc main():int{return " + invalid + ";}");
            }
            catch (FormatException)
            {
                try {
                    _ = OresStructuredParser.Parse(
                        "pub fnc main():int{return " + invalid + ";}");
                }
                catch (FormatException) { continue; }
                throw new Exception("structured parser admitted rejected value: " + invalid);
            }
            throw new Exception("shipping parser admitted invalid value: " + invalid);
        }

        // One emitted CIL executable method must preserve the source value,
        // rather than proving only the parser or a synthetic AST fixture.
        var folder = Path.Combine(Path.GetTempPath(),
            "oresnet-digit-separator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "OresGenerated.dll");
            var ir = OresIrLowering.Lower(OresParser.Parse(
                "pub fnc main():int{return +(4_0 + 2); }"));
            CilEmitter.EmitLibrary(ir, path);
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            var method = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
            Require(CilReferenceEvaluator.Evaluate(method) == 42,
                "emitted CIL matches verified typed SSA");
        }
        finally { Directory.Delete(folder, recursive: true); }
        Console.WriteLine("numeric separators: seven SSA positives, nine syntax/range rejections, one CIL differential");
    }
}
