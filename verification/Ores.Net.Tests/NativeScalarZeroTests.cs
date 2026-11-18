using System.Text;
using Mono.Cecil;
using Ores.Net.Compiler;

// These sources are byte-for-byte identical Oreslang guest sources on all
// five backends. Managed IR/CIL evaluation is NOT a NativeAOT guest receipt.
internal static class NativeScalarZeroTests
{
    private static readonly (string Name, string Source)[] Positives =
    [
        ("precedence-zero.ores", "pub fnc main():int{return (2 + 3 * 4) - 14;}\n"),
        ("i64-precision-zero.ores", "pub fnc main():int{return (9007199254740993 - 9007199254740992) - 1;}\n"),
        ("signed-opposite-zero.ores", "pub fnc main():int{return (9007199254740993 + (-9007199254740992)) - 1;}\n"),
        ("nested-subtraction-zero.ores", "pub fnc main():int{return (100 - (50 - (20 - 7))) - 63;}\n"),
        ("min-boundary-zero.ores", "pub fnc main():int{return (-9223372036854775808 + 1) - (-9223372036854775807);}\n")
    ];

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition) throw new InvalidOperationException("native-scalar-zero: " + diagnostic);
    }

    private static string SourcePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "contracts", "parity",
            "native-scalar-zero-v1", fileName);

    public static void Run()
    {
        var temp = Path.Combine(Path.GetTempPath(),
            "ores-net-zero-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var executed = 0;
            foreach (var (name, expectedText) in Positives)
            {
                var bytes = File.ReadAllBytes(SourcePath(name));
                Require(bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expectedText)),
                    "reviewed fixture bytes changed: " + name);
                var ir = OresIrLowering.Lower(OresParser.Parse(expectedText));
                Require(OresIrReferenceEvaluator.Evaluate(ir) == 0,
                    "native Oreslang assertion is nonzero in typed IR: " + name);
                var dll = Path.Combine(temp, name + ".dll");
                CilEmitter.EmitLibrary(ir, dll);
                using var assembly = AssemblyDefinition.ReadAssembly(dll);
                var method = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
                Require(CilReferenceEvaluator.Evaluate(method) == 0,
                    "native Oreslang assertion is nonzero in emitted CIL: " + name);
                executed++;
            }

            const string rejected = "pub fnc main():int{return 7 % 3;}\n";
            Require(File.ReadAllBytes(SourcePath("reject-removed-operator.ores"))
                .AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(rejected)),
                "negative case source drift");
            try
            {
                _ = OresParser.Parse(rejected);
                throw new InvalidOperationException("native-scalar-zero: removed % accepted");
            }
            catch (FormatException) { }

            var wrong = OresIrLowering.Lower(
                OresParser.Parse("pub fnc main():int{return (2 + 3 * 4) - 13;}"));
            Require(OresIrReferenceEvaluator.Evaluate(wrong) == 1,
                "intentionally bad assertion unexpectedly returned zero");
            Require(executed == 5, "partial assertion matrix");
            Console.WriteLine("native-scalar-zero: 5 Oreslang IR/CIL zero assertions, 1 compiler negative");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
