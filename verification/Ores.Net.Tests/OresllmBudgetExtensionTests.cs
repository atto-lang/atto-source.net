using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Ores.Net.Compiler;

// Compiler/IR/CIL-reference checks only; not a published NativeAOT guest.
internal static class OresllmBudgetExtensionTests
{
    private const string Header = "// oreslang:checked-i64-v1\n";
    public static void Run()
    {
        foreach (var (expression, expected) in new (string, long)[]
        {
            ("100 - (32 + 16)", 52L),
            ("9223372036854775807 - (2 * 64)", 9223372036854775679L),
            ("17 - 8 - 3", 6L),
            ("(-3) * (-7)", 21L),
            ("-9223372036854775808 + 0", long.MinValue)
        })
        {
            var ast = OresParser.Parse(Header + "pub fnc main():int{return " + expression + ";}");
            var ir = OresIrLowering.Lower(ast);
            if (OresIrReferenceEvaluator.Evaluate(ir) != expected)
                throw new Exception("Budget i64 IR value mismatch: " + expression);
            var dir = Path.Combine(Path.GetTempPath(), "ores-budget-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(dir, "OresGenerated.dll");
            try
            {
                CilEmitter.EmitLibrary(ir, file);
                using var asm = AssemblyDefinition.ReadAssembly(file);
                var method = asm.MainModule.Types
                    .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(m => m.Name == "Run");
                if (CilReferenceEvaluator.Evaluate(method) != expected)
                    throw new Exception("Budget i64 CIL value mismatch: " + expression);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        foreach (var expression in new[]
        {
            "9223372036854775807 - (-1)",
            "-9223372036854775808 * -1",
            "0 * (9223372036854775807 + 1)"
        })
        {
            var ast = OresParser.Parse(Header + "pub fnc main():int{return " + expression + ";}");
            var ir = OresIrLowering.Lower(ast);
            try { _ = OresIrReferenceEvaluator.Evaluate(ir); }
            catch (OverflowException) { continue; }
            throw new Exception("Budget i64 overflow was not detected: " + expression);
        }
        foreach (var expression in new[] { "1 + )", "(1 + 2", "1 2" })
        {
            try { _ = OresParser.Parse(Header + "pub fnc main():int{return " + expression + ";}"); }
            catch (FormatException) { continue; }
            throw new Exception("Budget i64 invalid source was admitted: " + expression);
        }
    }
}
