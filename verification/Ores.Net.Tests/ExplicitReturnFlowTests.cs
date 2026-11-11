using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

// The authoritative source parser and CFG verifier are used directly.
// No independently invented host parser or executable lambda shortcut.
internal static class ExplicitReturnFlowTests
{
    private static string Entry(string helper) =>
        helper + "\npub fnc main(): int { return 0; }";

    public static void Run()
    {
        var samples = new[]
        {
            Entry("fnc unused(): void {}"),
            Entry("fnc unused(): void { return; }"),
            Entry("fnc unused(): void { val int x = 42; }"),
            Entry("fnc unused(bool b): void { if b { return; } else { val int x = 2; } }"),
            Entry("fnc unused(bool b): void { if b { return; } else { return; } }"),
            Entry("fnc unused(): int { return 42; }"),
            Entry("fnc unused(bool b): int { if b { return 1; } else { return 2; } }"),
            Entry("fnc unused(): bool { return true; }"),
        };
        int passes = 0;
        foreach (string sample in samples)
        {
            var verified = OresStructuredCfgLowering.LowerModule(sample);
            if (verified.Main.Name != "main" || verified.Helpers.Count != 1)
                throw new Exception("Incorrect explicit-return function proof");
            string dir = Path.Combine(Path.GetTempPath(), "atto-return-flow-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var output = Path.Combine(dir, "OresGenerated.dll");
                CilEmitter.EmitLibrary(verified, output);
                using var assembly = AssemblyDefinition.ReadAssembly(output);
                var type = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram");
                var main = type.Methods.Single(m => m.Name == "Run");
                var helper = type.Methods.Single(m => m.Name == "helper_unused");
                if (helper.ReturnType.MetadataType !=
                    (verified.Helpers[0].ResultType == IrValueType.Void
                        ? MetadataType.Void
                        : verified.Helpers[0].ResultType == IrValueType.Bool
                            ? MetadataType.Boolean : MetadataType.Int64))
                    throw new Exception("Incorrect verified CIL return type");
                if (helper.Body.Instructions.Last().OpCode.Code != Code.Ret ||
                    CfgCilTestInterpreter.Evaluate(main) != 0)
                    throw new Exception("Compiled explicit return or void fallthrough failed");
            }
            finally { Directory.Delete(dir, recursive: true); }
            passes++;
        }

        int rejects = 0;
        void Reject(string label, string source, string marker)
        {
            try { _ = OresStructuredCfgLowering.LowerModule(source); }
            catch (FormatException ex) when (ex.Message.Contains(marker,
                StringComparison.OrdinalIgnoreCase))
            {
                rejects++;
                return;
            }
            throw new Exception("Incorrect return-flow acceptance/diagnostic: " + label);
        }
        Reject("int implicit fallthrough",
            Entry("fnc unused(): int { val int x = 1; }"),
            "RETURN_EXPLICIT_REQUIRED");
        Reject("bool implicit fallthrough",
            Entry("fnc unused(): bool { val bool x = true; }"),
            "RETURN_EXPLICIT_REQUIRED");
        Reject("main implicit fallthrough",
            "pub fnc main(): int { val int x = 1; }",
            "RETURN_EXPLICIT_REQUIRED");
        Reject("int return without a value",
            Entry("fnc unused(): int { return; }"), "RETURN_VALUE_REQUIRED");
        Reject("bool return without a value",
            Entry("fnc unused(): bool { return; }"), "RETURN_VALUE_REQUIRED");
        Reject("void return with a value",
            Entry("fnc unused(): void { return 1; }"), "RETURN_VOID_VALUE");
        Reject("void return with a boolean",
            Entry("fnc unused(): void { return true; }"), "RETURN_VOID_VALUE");
        Reject("missing else return",
            Entry("fnc unused(bool x): int { if x { return 1; } }"),
            "RETURN_EXPLICIT_REQUIRED");
        Reject("nonreturning else",
            Entry("fnc unused(bool x): int { if x { return 1; } else { val int y = 2; } }"),
            "RETURN_EXPLICIT_REQUIRED");
        Reject("loop cannot prove return",
            Entry("fnc unused(): int { while true { return 1; } }"),
            "RETURN_EXPLICIT_REQUIRED");
        Reject("braced lambda is not an implicit outer return",
            Entry("fnc unused(): int { val int f = |x| -> { x; }; }"),
            "Unsupported");
        Reject("wrong return type",
            Entry("fnc unused(): int { return true; }"),
            "Return value does not match function result type");
        Reject("unreachable after void return",
            Entry("fnc unused(): void { return; val int x = 1; }"),
            "Unreachable statement");

        void InvalidCfg(string label, CfgFunction function, string marker)
        {
            try { _ = OresCfgVerifier.Verify(function); }
            catch (FormatException ex) when (ex.Message.Contains(marker,
                StringComparison.OrdinalIgnoreCase))
            {
                rejects++;
                return;
            }
            throw new Exception("Unsafe forged CFG return flow accepted: " + label);
        }
        InvalidCfg("void method with value",
            new CfgFunction("bad", IrValueType.Void, "entry",
                new CfgBlock[] { new("entry", Array.Empty<CfgParameter>(),
                    new CfgInstruction[] { new(0, CfgOpcode.ConstantI64,
                        IrValueType.I64, I64Constant: 1) }, new CfgReturn(0)) }),
            "Void CFG method cannot return a value");
        InvalidCfg("nonvoid method with empty return",
            new CfgFunction("bad", IrValueType.I64, "entry",
                new CfgBlock[] { new("entry", Array.Empty<CfgParameter>(),
                    new CfgInstruction[] { new(0, CfgOpcode.ConstantI64,
                        IrValueType.I64, I64Constant: 1) }, new CfgReturnVoid()) }),
            "Non-void CFG method requires explicit return value");

        Console.WriteLine($"ATTO_NET_EXPLICIT_RETURN_FLOW_PASS|compiled={passes}|rejects={rejects}");
    }
}
