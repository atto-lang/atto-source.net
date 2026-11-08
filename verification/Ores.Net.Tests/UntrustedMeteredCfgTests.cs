using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

// Real source -> verified CFG -> metadata CIL -> independent CIL interpreter.
// This is a *trap-only*, per-method-call prototype: not actor suspension,
// tenant lifetime fuel, a CLR security boundary, or a NativeAOT guest receipt.
internal static class UntrustedMeteredCfgTests
{
    private static MethodDefinition GetRun(AssemblyDefinition assembly) =>
        assembly.MainModule.Types.Single(t =>
            t.FullName == "Oreslang.Generated.OresProgram")
            .Methods.Single(m => m.Name == "Run");

    private static void Check(
        string source, string path, long? expected, bool mustTrap)
    {
        var verified = OresStructuredCfgLowering.Lower(source);
        CilEmitter.EmitMeteredUntrustedPrototypeLibrary(verified, path);
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var run = GetRun(assembly);
        var code = run.Body.Instructions;

        // Every original verified CFG basic block must have a cold trap and
        // two compiler-private countdown subtractions. Removing a guard
        // makes these mutation-oriented assertions fail.
        int trapSites = code.Count(i => i.OpCode.Code == Code.Throw);
        int subtractions = code.Count(i => i.OpCode.Code == Code.Sub);
        int nullLoads = code.Count(i => i.OpCode.Code == Code.Ldnull);
        if (trapSites != verified.Blocks.Count ||
            subtractions != 2 * verified.Blocks.Count ||
            nullLoads != verified.Blocks.Count)
            throw new Exception("Untrusted CIL block-entry checkpoint coverage lost");
        if (run.Body.Variables.Count != verified.Blocks.Sum(b =>
                b.Parameters.Count + b.Instructions.Count) +
                verified.Blocks.Sum(b => b.Parameters.Count) + 2)
            throw new Exception("Metered CIL compiler-private local slots drifted");

        if (mustTrap)
        {
            try
            {
                _ = CfgCilTestInterpreter.Evaluate(run, maxSteps: 1_000_000);
                throw new Exception("Metered untrusted CIL ran through its hard quota");
            }
            catch (InvalidOperationException ex) when
                (ex.Message == "untrusted CIL hard fuel exhausted") { }
        }
        else
        {
            if (CfgCilTestInterpreter.Evaluate(run, maxSteps: 1_000_000) != expected)
                throw new Exception("Metered source CFG-to-CIL result mismatch");
        }
    }

    private static void MustReject(VerifiedCfgFunction graph, string path,
        string message)
    {
        try
        {
            CilEmitter.EmitMeteredUntrustedPrototypeLibrary(graph, path);
            throw new Exception("Metered CIL incorrectly admitted " + message);
        }
        catch (FormatException) { }
        if (File.Exists(path))
            throw new Exception("Metered CIL published rejected " + message);
    }

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "atto-net-metered-cil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string FileAt(string name) => Path.Combine(root, name + ".dll");
            string Loop(int n) => "fnc main():int{let int i=0;" +
                "while i<" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "{i=i+1;}return i;}";
            Check("fnc main():int{return 42;}", FileAt("acyclic"),
                expected: 42, mustTrap: false);
            foreach (int iterations in new[] { 0, 1, 127, 128, 129, 511, 512, 513 })
                Check(Loop(iterations), FileAt("loop-" + iterations),
                    expected: iterations, mustTrap: false);
            Check("fnc main():int{let int i=0;let int total=0;" +
                "while i<3{let int j=0;while j<3{total=total+1;j=j+1;}" +
                "i=i+1;}return total;}", FileAt("nested-loops"),
                expected: 9, mustTrap: false);
            Check(Loop(100_000), FileAt("exhausted"),
                expected: null, mustTrap: true);

            // Compiler decision is deterministic and does not depend on
            // the destination file path, host timing, or runtime JIT.
            var graph = OresStructuredCfgLowering.Lower(Loop(129));
            CilEmitter.EmitMeteredUntrustedPrototypeLibrary(graph, FileAt("copy"));
            if (!File.ReadAllBytes(FileAt("copy")).AsSpan()
                .SequenceEqual(File.ReadAllBytes(FileAt("loop-129"))))
                throw new Exception("Metered CIL output is not deterministic");

            // Untrusted calls/recursive helpers must not become a backdoor
            // around the local method-entry budget.
            var module = OresStructuredCfgLowering.LowerModule(
                "fnc helper(int x):int{return x+1;}" +
                "fnc main():int{return helper(41);}");
            MustReject(module.Main, FileAt("call-rejected"), "guest helper call");

            // Positive support for cyclic CFG is opt-in; the older
            // loop-free admission remains fail-closed on the same graph.
            try
            {
                CilEmitter.EmitLoopFreeUntrustedPrototypeLibrary(
                    OresStructuredCfgLowering.Lower(Loop(129)),
                    FileAt("old-gate"));
                throw new Exception("Old untrusted gate admitted unchecked cycle");
            }
            catch (FormatException ex) when
                (ex.Message.Contains("mandatory checkpoints", StringComparison.Ordinal)) { }
            if (File.Exists(FileAt("old-gate")))
                throw new Exception("Old gate published an uninstrumented loop");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("NET_METERED_VERIFIED_CFG_CIL_TRAP_ONLY: positive loops, 128 boundaries, hard budget, call rejects");
    }
}
