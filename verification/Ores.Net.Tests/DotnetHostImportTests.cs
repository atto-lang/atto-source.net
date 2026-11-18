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
        var grant = DotnetHostImportPolicy.MathI64V0;
        Reject(() => OresParser.Parse(source), "host import admitted without capability");
        Reject(() => OresIrLowering.Lower(new DotnetMathAbsI64(new Literal(7))),
            "forged CLR AST admitted without capability");
        var typed = OresIrLowering.Lower(OresParser.Parse(source, grant), policy: grant);
        Check(typed.Instructions.Count(x => x.Opcode == IrOpcode.DotnetMathAbsI64) == 1,
            "exactly one typed CLR call lowered through verified SSA");
        // Advisory static facts v0 intentionally asserts an empty call graph.
        // A verified external CLR call must not be misreported as zero edges.
        Reject(() => StaticFactsV0Exporter.FromCompiledScalarSource(source, grant),
            "CLR host call erased from zero-edge static-facts exporter");
        Reject(() => ManagedSourceEmitter.Emit(typed, ManagedSourceTarget.CSharp),
            "host CLR call silently rewritten into unsupported C# source");
        Reject(() => ManagedSourceEmitter.Emit(typed, ManagedSourceTarget.FSharp),
            "host CLR call silently rewritten into unsupported F# source");

        const string maxSource =
            "import dotnet \"System.Math\" as Math; " +
            "pub fnc main() -> int { return Math.Max(10,42); }";
        Reject(() => OresParser.Parse(maxSource), "Math.Max admitted without capability");
        var maxTyped = OresIrLowering.Lower(OresParser.Parse(maxSource, grant),
            policy: grant);
        Check(maxTyped.Instructions.Count(x => x.Opcode == IrOpcode.DotnetMathMaxI64) == 1,
            "exactly one typed CLR Max(i64,i64) call lowered through verified SSA");
        Reject(() => StaticFactsV0Exporter.FromCompiledScalarSource(maxSource, grant),
            "Max CLR host call erased from zero-edge static facts");

        const string nestedSource =
            "import dotnet \"System.Math\" as Math; " +
            "pub fnc main() -> int { return Math.Max(Math.Abs(-42), 10); }";
        var nestedTyped = OresIrLowering.Lower(OresParser.Parse(nestedSource, grant),
            policy: grant);
        Check(nestedTyped.Instructions.Count(x => x.Opcode == IrOpcode.DotnetMathAbsI64) == 1 &&
              nestedTyped.Instructions.Count(x => x.Opcode == IrOpcode.DotnetMathMaxI64) == 1,
            "nested Math.Abs -> Math.Max must preserve two independently typed host calls");

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
            string maxOutput = Path.Combine(root, "max.dll");
            CilEmitter.EmitLibrary(maxTyped, maxOutput);
            Check(!File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(maxOutput)),
                "Distinct CLR methods must affect deterministic generated assembly identity");

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

            string nestedOutput = Path.Combine(root, "nested.dll");
            CilEmitter.EmitLibrary(nestedTyped, nestedOutput);
            using (var nestedAssembly = AssemblyDefinition.ReadAssembly(nestedOutput))
            {
                var nestedRun = nestedAssembly.MainModule.Types.Single(x =>
                    x.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(x => x.Name == "Run");
                var nestedCalls = nestedRun.Body.Instructions
                    .Where(x => x.OpCode == OpCodes.Call)
                    .Select(x => x.Operand as MethodReference).ToArray();
                Check(nestedCalls.Length == 2 &&
                      nestedCalls[0]?.Name == "Abs" &&
                      nestedCalls[1]?.Name == "Max" &&
                      nestedCalls.All(x => x?.DeclaringType.FullName == "System.Math" &&
                          x.ReturnType.FullName == "System.Int64"),
                    "nested CIL must call exact Math.Abs then Math.Max without dynamic dispatch");
            }

            using var maxAssembly = AssemblyDefinition.ReadAssembly(maxOutput);
            var maxRun = maxAssembly.MainModule.Types.Single(x =>
                x.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(x => x.Name == "Run");
            var maxCalls = maxRun.Body.Instructions.Where(x => x.OpCode == OpCodes.Call)
                .Select(x => x.Operand as MethodReference).ToArray();
            Check(maxCalls.Length == 1 && maxCalls[0] is not null &&
                  maxCalls[0]!.Name == "Max" &&
                  maxCalls[0]!.DeclaringType.FullName == "System.Math" &&
                  maxCalls[0]!.ReturnType.FullName == "System.Int64" &&
                  maxCalls[0]!.Parameters.Count == 2 &&
                  maxCalls[0]!.Parameters.All(p =>
                      p.ParameterType.FullName == "System.Int64"),
                "CIL must statically pin exact Math.Max(Int64,Int64) overload");
            Check(assembly.MainModule.Mvid != maxAssembly.MainModule.Mvid,
                "Distinct CLR method contracts must not reuse the generated module identity");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        foreach (var rejected in new[]
        {
            "import dotnet \"System.Math\"; fnc main():int{return 42;}",
            "import dotnet \"System.Reflection.Assembly\" as Reflect; fnc main():int{return 42;}",
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
            "import dotnet \"System.Math\" as Math; fnc main():int{return Math.Max(1);}",
            "import dotnet \"System.Math\" as Math; fnc main():int{return Math.Max(1,2,3);}",
            "import dotnet \"System.Math\" as Math; fnc main():int{return Math.Max(1.5,2);}",
        })
        {
            Reject(() => OresIrLowering.Lower(OresParser.Parse(rejected, grant),
                policy: grant),
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
            }, 1), grant), "unverified extra CLR argument allowed");
        Reject(() => OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64,
            new[] { new IrInstruction(0, IrOpcode.DotnetMathAbsI64,
                IrValueType.I64, Left: 99) }, 0), grant), "forged CLR SSA predecessor allowed");

        Reject(() => OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64,
            new[] {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 2),
                new IrInstruction(1, IrOpcode.DotnetMathAbsI64, IrValueType.I64, Left: 0)
            }, 1)), "direct untrusted SSA CLR call bypassed capability");
        Reject(() => OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64,
            new[] {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 1),
                new IrInstruction(1, IrOpcode.ConstantI64, IrValueType.I64, Constant: 2),
                new IrInstruction(2, IrOpcode.DotnetMathMaxI64, IrValueType.I64,
                    Left: 0, Right: 1)
            }, 2)), "direct untrusted SSA Max call bypassed capability");
        Reject(() => OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64,
            new[] {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 2),
                new IrInstruction(1, IrOpcode.DotnetMathMaxI64, IrValueType.I64,
                    Left: 0)
            }, 1), grant), "malformed CLR Max arity allowed");
        Reject(() => OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64,
            new[] {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 2),
                new IrInstruction(1, IrOpcode.DotnetMathMaxI64, IrValueType.I64,
                    Left: 0, Right: 4)
            }, 1), grant), "forged forward SSA Max argument allowed");
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
