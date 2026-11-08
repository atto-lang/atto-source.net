using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

internal static class DotnetHostImportTests
{
    public static void Run()
    {
        const string source =
            "import dotnet \"System.Math\" as HostMath; " +
            "pub fnc main() -> int { return HostMath.Abs(-42); }";
        var typed = OresIrLowering.Lower(OresParser.Parse(source));
        Check(typed.Instructions.Count(x => x.Opcode == IrOpcode.DotnetMathAbsI64) == 1,
            "exactly one typed CLR call lowered through verified SSA");
        // Advisory static facts v0 intentionally asserts an empty call graph.
        // A verified external CLR call must not be misreported as zero edges.
        Reject(() => StaticFactsV0Exporter.FromCompiledScalarSource(source),
            "CLR host call erased from zero-edge static-facts exporter");
        Reject(() => ManagedSourceEmitter.Emit(typed, ManagedSourceTarget.CSharp),
            "host CLR call silently rewritten into unsupported C# source");
        Reject(() => ManagedSourceEmitter.Emit(typed, ManagedSourceTarget.FSharp),
            "host CLR call silently rewritten into unsupported F# source");

        string root = Path.Combine(Path.GetTempPath(),
            "atto-dotnet-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "OresGenerated.dll");
            string second = Path.Combine(root, "repeat.dll");
            CilEmitter.EmitLibrary(typed, first);
            CilEmitter.EmitLibrary(typed, second);
            Check(File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second)),
                "CLR host import must emit byte-identical deterministic CIL");

            // Mono.Cecil is test-only: the compiler itself must never load or
            // reflect over generated or foreign assemblies at runtime.
            using var assembly = AssemblyDefinition.ReadAssembly(first);
            var run = assembly.MainModule.Types.Single(x =>
                x.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(x => x.Name == "Run");
            var calls = run.Body.Instructions
                .Where(x => x.OpCode == OpCodes.Call)
                .Select(x => x.Operand as MethodReference)
                .ToArray();
            Check(calls.Length == 1 && calls[0] is not null,
                "exactly one non-dynamic CLR call expected");
            var method = calls[0]!;
            Check(method.DeclaringType.FullName == "System.Math" &&
                  method.Name == "Abs" &&
                  method.ReturnType.FullName == "System.Int64" &&
                  method.Parameters.Count == 1 &&
                  method.Parameters[0].ParameterType.FullName == "System.Int64",
                "CIL method reference must pin System.Math.Abs(Int64)");
            Check(method.DeclaringType.Scope is AssemblyNameReference reference &&
                  reference.Name == "System.Runtime" &&
                  reference.Version.Major == 10,
                "CLR host call must use pinned .NET 10 framework reference");
            Check(!run.Body.Instructions.Any(x =>
                x.OpCode == OpCodes.Callvirt || x.OpCode == OpCodes.Newobj),
                "host call must not allocate or dispatch through arbitrary CLR objects");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        foreach (var rejected in new[]
        {
            "import dotnet \"System.IO.File\" as File; fnc main():int{return 42;}",
            "import dotnet \"System.Math\" as Math; fnc main():int{return Math.Pow(2,3);}",
            "import dotnet \"System.Math\" as Math; fnc main():int{return Math.Abs(1,2);}",
            "import dotnet \"System.Math\" as Math; fnc main():int{return Other.Abs(1);}",
            "import dotnet \"System.Math\" as Math; import dotnet \"System.Math\" as Other; fnc main():int{return 1;}",
            "import dotnet \"System.Math\" as Integer; fnc main():int{return Integer.Abs(1);}",
            "import dotnet \"System.Math\" as Math; fnc main():int{return Math.Abs(1).GetType();}",
            "import dotnet \"System.Reflection.Assembly\" as Assembly; fnc main():int{return 1;}",
            "import jvm \"java.lang.Math\" as Math; fnc main():int{return 1;}",
            "import fnc abs from \"dotnet:System.Math\"; fnc main():int{return 1;}",
            "fnc main():int{return Math.Abs(7);}",
        })
        {
            Reject(() => OresIrLowering.Lower(OresParser.Parse(rejected)),
                "unsupported host surface admitted: " + rejected);
        }
        Check(OresParser.Parse("fnc main():int{return 42;} // import dotnet \"System.IO.File\"") is Literal,
            "inert comment text changed host import admission");

        Reject(() => OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64,
            new[]
            {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 42),
                new IrInstruction(1, IrOpcode.DotnetMathAbsI64, IrValueType.I64,
                    Left: 0, Right: 0)
            }, 1)), "unverified extra CLR argument allowed");
        Reject(() => OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64,
            new[] { new IrInstruction(0, IrOpcode.DotnetMathAbsI64,
                IrValueType.I64, Left: 99) }, 0)), "forged CLR SSA predecessor allowed");
    }

    private static void Check(bool ok, string description)
    {
        if (!ok) throw new Exception("Dotnet import failure: " + description);
    }

    private static void Reject(Action check, string description)
    {
        try { check(); }
        catch (FormatException) { return; }
        throw new Exception("Dotnet import negative test failed: " + description);
    }
}
