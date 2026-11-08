using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

public static class DotnetStaticInteropSelfTest
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("DOTNET_IMPORT_V0: " + message);
    }

    private static void Reject(string source, string diagnostic)
    {
        try { OresStructuredCfgLowering.LowerModule(source); }
        catch (FormatException ex) when (ex.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase))
        { return; }
        throw new Exception("DOTNET_IMPORT_V0: must reject " + diagnostic);
    }

    public static void Run()
    {
        const string source = """
            import dotnet "System.Math" as Math;
            pub fnc main():int { return Math.Abs(-42); }
            """;
        var verified = OresStructuredCfgLowering.LowerModule(source);
        Require(CfgReferenceEvaluator.Evaluate(verified.Main) == 42,
            "verified CFG evaluator runs typed host scalar semantics");
        Require(verified.Main.Blocks.SelectMany(b => b.Instructions)
            .Count(op => op.Opcode == CfgOpcode.DotnetMathAbs) == 1,
            "source import must produce a verified, typed host opcode");

        var temporary = Path.Combine(Path.GetTempPath(), "atto-dotnet-import-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var output = Path.Combine(temporary, "OresGenerated.dll");
            CilEmitter.EmitLibrary(verified, output);
            byte[] first = File.ReadAllBytes(output);
            CilEmitter.EmitLibrary(verified, output);
            Require(first.AsSpan().SequenceEqual(File.ReadAllBytes(output)),
                "host MemberRef emission must remain deterministic");
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var method = assembly.MainModule.GetType("Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            Require(CfgCilTestInterpreter.Evaluate(method) == 42,
                "test-only CIL interpreter executes the exact host MemberRef");
            var calls = method.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Call).ToArray();
            Require(calls.Length == 1 && calls[0].Operand is MethodReference,
                "exactly one static host call emitted");
            var callee = (MethodReference)calls[0].Operand;
            Require(callee.Name == "Abs" &&
                    callee.DeclaringType.FullName == "System.Math" &&
                    callee.Parameters.Count == 1 &&
                    callee.Parameters[0].ParameterType.FullName == "System.Int64" &&
                    callee.ReturnType.FullName == "System.Int64" &&
                    !callee.HasThis,
                "System.Runtime Math.Abs(Int64) static signature, not arbitrary reflection");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }

        var overflow = OresStructuredCfgLowering.LowerModule(
            "import dotnet \"System.Math\" as Math; fnc main():int { return Math.Abs(-9223372036854775808); }");
        try { CfgReferenceEvaluator.Evaluate(overflow.Main); }
        catch (OverflowException) { goto OverflowVerified; }
        throw new Exception("DOTNET_IMPORT_V0: Math.Abs(long.MinValue) must overflow");
    OverflowVerified:
        Reject("import dotnet \"System.Math\" as Math; fnc Math():int { return 1; } fnc main():int { return 1; }",
            "DOTNET_IMPORT_UNSUPPORTED");
        Reject("import dotnet \"System.IO.File\" as File; fnc main():int { return 1; }",
            "DOTNET_IMPORT_UNSUPPORTED");
        Reject("import dotnet \"System.Math\" as Math; fnc main():int { return Math.Max(3, 2); }",
            "DOTNET_IMPORT_UNSUPPORTED");
        Reject("import dotnet \"System.Math\" as Math; import dotnet \"System.Math\" as Other; fnc main():int { return 1; }",
            "DOTNET_IMPORT_UNSUPPORTED");
        Reject("import dotnet \"System.Math\" as Math; fnc main():int { return Math.Abs(true); }",
            "System.Math.Abs requires one int");
        Reject("import dotnet \"System.Math\" as Math; fnc main():int { return Math.Abs(4, 5); }",
            "Expected ')'");
        Reject("fnc main():int { return Math.Abs(-42); }",
            "Undefined");
        Reject("import dotnet \"System.Math\" as Math; fnc main():int { return Math.Abs(rt borrow 42); }",
            "Ownership operation");
        Reject("import dotnet \"System.Math\" as Math; fnc main():int { return Math.Abs(await 42); }",
            "E_SUSPENDS_UNSUPPORTED");

        // The production scalar compiler must remain closed to host imports.
        try { OresParser.Parse(source); }
        catch (FormatException) { return; }
        throw new Exception("DOTNET_IMPORT_V0: shipping scalar parser silently admitted host import");
    }
}
