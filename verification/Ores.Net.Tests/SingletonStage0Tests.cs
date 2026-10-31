using Mono.Cecil;
using Ores.Net.Compiler;

// An explicit stage-0 compiler/IR/CIL test only. Neither ordinary parser nor
// NativeAOT guest execution is promoted by this host verification.
internal static class SingletonStage0Tests
{
    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("singleton stage0: " + why);
    }

    private static void Reject(string category, Action action)
    {
        try { action(); }
        catch (FormatException error) when (error.Message.Contains(category, StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("singleton stage0 failed to reject " + category);
    }

    public static void Run()
    {
        const string scalar = "fnc main():int{return 42;}";
        using var scratch = new Scratch();
        int number = 0;
        foreach (var head in new[]
        {
            "define singleton Settings as end",
            "define pub singleton Settings as end",
            "pub define singleton Settings as end",
            "/* Settings /* define singleton */ ignored */ define singleton Settings as /* Settings */ end"
        })
        {
            string source = head + "\n" + scalar;
            Reject("ORES-SINGLETON-UNSUPPORTED", () => OresParser.Parse(source));
            Reject("ORES-SINGLETON-UNSUPPORTED", () => OresStructuredParser.Parse(source));
            var plan = SingletonStage0Compiler.Compile(source, "app/settings.ores");
            var same = SingletonStage0Compiler.Compile(source, "app/settings.ores");
            var foreign = SingletonStage0Compiler.Compile(source, "other/settings.ores");
            Require(plan.Identity == same.Identity, "same module and declaration changed identity");
            Require(plan.Identity != foreign.Identity, "distinct module identity collision");
            Require(plan.Identity.Declaration == "Settings", "wrong declaration identity");
            Require(plan.IsPublic == (head.StartsWith("define pub", StringComparison.Ordinal) ||
                    head.StartsWith("pub define", StringComparison.Ordinal)),
                "public visibility not stable");
            Require(plan.Initializer.Name == "main", "initializer is not compiler-verified main");
            Require(OresIrReferenceEvaluator.Evaluate(plan.Initializer) == 42,
                "checked scalar initializer IR returned wrong result");

            string generated = Path.Combine(scratch.Directory, "stage0-" + number++ + ".dll");
            CilEmitter.EmitLibrary(plan.Initializer, generated);
            using var assembly = AssemblyDefinition.ReadAssembly(generated);
            var run = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
            Require(CilReferenceEvaluator.Evaluate(run) == 42,
                "emitted CIL reference evaluation returned wrong result");
        }

        const string valid = "define singleton Settings as end\n" + scalar;
        foreach (var source in new[]
        {
            "singleton Settings as end\n" + scalar,
            "define define singleton Settings as end\n" + scalar,
            "define pub pub singleton Settings as end\n" + scalar,
            "define singleton as end\n" + scalar,
            "define singleton Settings\n" + scalar,
            "define singleton Settings as\n" + scalar,
            "define singleton Settings as /* unterminated\n" + scalar
        })
            Reject("ORES-SINGLETON-STAGE0-SYNTAX", () =>
                SingletonStage0Compiler.Compile(source, "app/settings.ores"));

        Reject("ORES-SINGLETON-UNSUPPORTED", () =>
            SingletonStage0Compiler.Compile(
                "define singleton Settings as end\ndefine singleton Other as end\n" + scalar,
                "app/settings.ores"));

        Reject("ORES-SINGLETON-PREPUBLICATION-ESCAPE", () =>
            SingletonStage0Compiler.Compile(
                "define singleton Settings as\n Settings\n end\n" + scalar,
                "app/settings.ores"));
        Reject("ORES-SINGLETON-STAGE0-BODY-UNSUPPORTED", () =>
            SingletonStage0Compiler.Compile(
                "define singleton Settings as\n value\n end\n" + scalar,
                "app/settings.ores"));
        Reject("ORES-SINGLETON-STAGE0-BODY-UNSUPPORTED", () =>
            SingletonStage0Compiler.Compile(
                "define singleton Settings as\n SettingsValue\n end\n" + scalar,
                "app/settings.ores"));
        foreach (string badModule in new[] { "", "/root/settings.ores", "../other/settings.ores", "app/../settings.ores", "app//settings.ores", "app\\settings.ores" })
            Reject("ORES-SINGLETON-STAGE0-SYNTAX", () =>
                SingletonStage0Compiler.Compile(valid, badModule));
        Reject("ORES-SINGLETON-STAGE0-SYNTAX", () =>
            SingletonStage0Compiler.Compile(valid + "\ud800", "app/settings.ores"));
        Reject("ORES-SINGLETON-STAGE0-SYNTAX", () =>
            SingletonStage0Compiler.Compile(new string('x', 1_048_577), "app/settings.ores"));

        // Compiler plan creation is not a one-shot runtime publication or
        // cross-isolate capability. Normal admission must stay disabled.
        Console.WriteLine("singleton stage0: source shell, compiler-owned identity, typed IR and CIL reference PASS (not NativeAOT/guest parity)");
    }

    private sealed class Scratch : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(),
            "ores-net-singleton-stage0-" + Guid.NewGuid().ToString("N"));

        public Scratch() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
