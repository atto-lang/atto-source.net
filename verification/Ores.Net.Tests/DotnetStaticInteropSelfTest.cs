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

        const string nested = """
            import dotnet "System.Math" as Bcl;
            fnc main():int {
                return Bcl.Max(Bcl.Min(42, 70), Bcl.Abs(-9));
            }
            """;
        var nestedCfg = OresStructuredCfgLowering.LowerModule(nested);
        Require(CfgReferenceEvaluator.Evaluate(nestedCfg.Main) == 42,
            "nested host method lowering evaluates eager operands left-to-right");
        var nestedTemp = Path.Combine(Path.GetTempPath(),
            "atto-dotnet-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nestedTemp);
        try
        {
            var nestedDll = Path.Combine(nestedTemp, "OresGenerated.dll");
            CilEmitter.EmitLibrary(nestedCfg, nestedDll);
            var first = File.ReadAllBytes(nestedDll);
            CilEmitter.EmitLibrary(nestedCfg, nestedDll);
            Require(first.AsSpan().SequenceEqual(File.ReadAllBytes(nestedDll)),
                "nested BCL MemberRefs must be emitted deterministically");
            using var nestedAssembly = AssemblyDefinition.ReadAssembly(nestedDll);
            Require(nestedAssembly.MainModule.AssemblyReferences
                .Count(a => a.Name == "System.Runtime") == 1,
                "nested host import must share exactly one System.Runtime AssemblyRef");
            var nestedRun = nestedAssembly.MainModule.GetType("Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            Require(CfgCilTestInterpreter.Evaluate(nestedRun) == 42,
                "CIL oracle must execute all three statically bound BCL methods");
            var calls = nestedRun.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Call)
                .Select(i => (MethodReference)i.Operand)
                .ToArray();
            Require(calls.Length == 3 &&
                    calls.Select(m => m.Name).SequenceEqual(new[] {"Min", "Abs", "Max"}),
                "nested host calls must preserve deterministic source evaluation order");
            foreach (var call in calls)
                Require(call.DeclaringType.FullName == "System.Math" &&
                        call.DeclaringType.Scope is AssemblyNameReference assemblyRef &&
                        assemblyRef.Name == "System.Runtime" &&
                        call.ReturnType.FullName == "System.Int64" &&
                        call.Parameters.Count == (call.Name == "Abs" ? 1 : 2) &&
                        call.Parameters.All(p => p.ParameterType.FullName == "System.Int64") &&
                        !call.HasThis,
                    "only explicitly linked, typed static System.Math MemberRefs");
        }
        finally
        {
            Directory.Delete(nestedTemp, recursive: true);
        }

        // A MemberRef token is just a table row number. A one-call Max and
        // one-call Min can emit identical IL bytes but have different method
        // names in the MemberRef table; the module identity MUST differ.
        var identityDirectory = Path.Combine(Path.GetTempPath(),
            "atto-dotnet-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(identityDirectory);
        try
        {
            var maxSource = OresStructuredCfgLowering.LowerModule(
                "import dotnet \"System.Math\" as Math; fnc main():int { return Math.Max(3, 2); }");
            var minSource = OresStructuredCfgLowering.LowerModule(
                "import dotnet \"System.Math\" as Math; fnc main():int { return Math.Min(3, 2); }");
            var maxDll = Path.Combine(identityDirectory, "max.dll");
            var minDll = Path.Combine(identityDirectory, "min.dll");
            CilEmitter.EmitLibrary(maxSource, maxDll);
            CilEmitter.EmitLibrary(minSource, minDll);
            using var maxAssembly = AssemblyDefinition.ReadAssembly(maxDll);
            using var minAssembly = AssemblyDefinition.ReadAssembly(minDll);
            Require(maxAssembly.MainModule.Mvid != minAssembly.MainModule.Mvid,
                "different external BCL methods cannot share the same deterministic module MVID");
            Require(!File.ReadAllBytes(maxDll).AsSpan().SequenceEqual(File.ReadAllBytes(minDll)),
                "different imported method identities cannot emit byte-identical modules");
            Require(maxAssembly.MainModule.AssemblyReferences
                .Count(a => a.Name == "System.Runtime") == 1 &&
                    minAssembly.MainModule.AssemblyReferences
                .Count(a => a.Name == "System.Runtime") == 1,
                "every statically imported BCL member must share one System.Runtime AssemblyRef");
        }
        finally
        {
            Directory.Delete(identityDirectory, recursive: true);
        }

        Reject("import dotnet \"System.Math\" as Math; fnc main():int { return Math.Max(3); }",
            "Expected ','");
        Reject("import dotnet \"System.Math\" as Math; fnc main():int { return Math.Min(2, true); }",
            "System.Math.Min requires int operands");
        Reject("import dotnet \"System.Math\" as Math; fnc main():int { return Math.Max(1, 2, 3); }",
            "Expected ')'");
        Reject("import dotnet \"System.Math\" as Math; fnc main():int { let int Math = 1; return Math.Max(1,2); }",
            "DOTNET_IMPORT_UNSUPPORTED");
        Reject("import dotnet \"System.Math\" as Math; fnc helper(int Math):int { return Math; } fnc main():int { return helper(1); }",
            "DOTNET_IMPORT_UNSUPPORTED");

        var overflow = OresStructuredCfgLowering.LowerModule(
            "import dotnet \"System.Math\" as Math; fnc main():int { return Math.Abs(-9223372036854775808); }");
        bool overflowed = false;
        try { CfgReferenceEvaluator.Evaluate(overflow.Main); }
        catch (OverflowException) { overflowed = true; }
        Require(overflowed, "Math.Abs(long.MinValue) must overflow");
        Reject("import dotnet \"System.Math\" as Math; fnc Math():int { return 1; } fnc main():int { return 1; }",
            "DOTNET_IMPORT_UNSUPPORTED");
        Reject("import dotnet \"System.IO.File\" as File; fnc main():int { return 1; }",
            "DOTNET_IMPORT_UNSUPPORTED");
        Reject("import dotnet \"System.Math\" as Math; fnc main():int { return Math.Pow(3, 2); }",
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
