using Mono.Cecil;
using Ores.Net.Compiler;

internal static class UntrustedLoopFreeCfgAdmissionTests
{
    public static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "atto-untrusted-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string safeFile = Path.Combine(folder, "bounded.dll");
            var safe = OresStructuredCfgLowering.Lower(
                "fnc main():int{const int n=42;if n<43{return n;}else{return 0;}}");
            CilEmitter.EmitLoopFreeUntrustedPrototypeLibrary(safe, safeFile);
            using (var assembly = AssemblyDefinition.ReadAssembly(safeFile))
            {
                var run = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(method => method.Name == "Run");
                if (CfgCilTestInterpreter.Evaluate(run) != 42)
                    throw new Exception("Bounded prototype rejected valid acyclic CIL");
            }

            string loop = """
                fnc main():int{
                    let int i=0;
                    while i<10 { i=i+1; }
                    return i;
                }
                """;
            var unsafeLoop = OresStructuredCfgLowering.Lower(loop);
            string loopOut = Path.Combine(folder, "must-not-emit-loop.dll");
            try
            {
                CilEmitter.EmitLoopFreeUntrustedPrototypeLibrary(unsafeLoop, loopOut);
                throw new Exception("Untrusted CIL admitted an unchecked loop");
            }
            catch (FormatException error) when
                (error.Message.Contains("mandatory checkpoints", StringComparison.Ordinal)) { }
            if (File.Exists(loopOut))
                throw new Exception("Uninstrumented untrusted CIL artifact was published");

            var withCall = OresStructuredCfgLowering.LowerModule(
                "fnc helper(int n):int{return n+1;}" +
                "fnc main():int{return helper(41);}");
            string callOut = Path.Combine(folder, "must-not-emit-call.dll");
            try
            {
                CilEmitter.EmitLoopFreeUntrustedPrototypeLibrary(withCall.Main, callOut);
                throw new Exception("Untrusted CIL admitted unchecked call graph");
            }
            catch (FormatException error) when
                (error.Message.Contains("hard guest fuel", StringComparison.Ordinal)) { }
            if (File.Exists(callOut))
                throw new Exception("Uninstrumented untrusted CIL helper artifact was published");

            // A verified, straight-line source can still exceed the hard
            // static instruction ceiling and must not be emitted.
            var oversizedOps = Enumerable.Range(0, 4097)
                .Select(i => new CfgInstruction(i, CfgOpcode.ConstantI64,
                    IrValueType.I64, I64Constant: i)).ToArray();
            var large = OresCfgVerifier.Verify(new CfgFunction(
                "main", IrValueType.I64, "entry", new[] {
                    new CfgBlock("entry", Array.Empty<CfgParameter>(),
                        oversizedOps, new CfgReturn(4096))
                }));
            string largeOut = Path.Combine(folder, "must-not-emit-large.dll");
            try
            {
                CilEmitter.EmitLoopFreeUntrustedPrototypeLibrary(large, largeOut);
                throw new Exception("Untrusted CIL admitted >4096 instruction graph");
            }
            catch (FormatException error) when
                (error.Message.Contains("instruction budget", StringComparison.Ordinal)) { }
            if (File.Exists(largeOut))
                throw new Exception("Untrusted static-quota rejection emitted a DLL");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
