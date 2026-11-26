using Mono.Cecil;
using Ores.Net.Compiler;

// The source parser, typed OresIR and CIL emitter are the production paths.
// This is a restricted scalar callable body profile, not generic actor/method
// parity or proof that NativeAOT emits a complete Atto program.
internal static class AttoCallableLanguageVersionTests
{
    private static long CompileAndExecute(string source, int version)
    {
        var expression = OresParser.Parse(source, languageVersion: version);
        var typed = OresIrLowering.Lower(expression);
        string root = Path.Combine(Path.GetTempPath(),
            "atto-callable-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string output = Path.Combine(root, "OresGenerated.dll");
            CilEmitter.EmitLibrary(typed, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var type = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram");
            var run = type.Methods.Single(m => m.Name == "Run");
            return CfgCilTestInterpreter.Evaluate(run);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void MustReject(string source, int version, string marker)
    {
        try { _ = OresParser.Parse(source, languageVersion: version); }
        catch (FormatException ex) when (ex.Message.Contains(marker, StringComparison.Ordinal))
        { return; }
        throw new Exception("Versioned callable admitted invalid program or wrong diagnostic: " + marker);
    }

    public static void Run()
    {
        const string v1 = "pub fnc main() -> int 1, 42 end";
        const string marked = "pub fnc main() -> int :: 1, 42 end";
        const string statements = "pub fnc main() -> int return 42; end";
        const string caret = "pub fnc main() -> int ^ 42; end";

        if (CompileAndExecute(v1, 1) != 42 ||
            CompileAndExecute(marked, 1) != 42 ||
            CompileAndExecute(marked, 2) != 42 ||
            CompileAndExecute(statements, 2) != 42 ||
            CompileAndExecute(caret, 2) != 42)
            throw new Exception("Versioned .NET native CIL scalar result mismatch");

        MustReject(v1, 2, "E_LANGUAGE_VERSION");
        MustReject(statements, 1, "E_SYNTAX");
        MustReject("pub fnc main() -> int :: 1; 42 end", 2, "E_SYNTAX");
        MustReject("pub fnc main() -> int :: 1, end", 2, "E_SYNTAX");
        MustReject("pub fnc main() -> int :: 1, 2, end", 2, "E_SYNTAX");
        MustReject(marked, 0, "E_LANGUAGE_VERSION");
        MustReject(marked, 3, "E_LANGUAGE_VERSION");
        MustReject(marked, -1, "E_LANGUAGE_VERSION");
        MustReject(marked, int.MaxValue, "E_LANGUAGE_VERSION");

        // No flags: preserve the preexisting Oreslang compiler behavior.
        _ = OresParser.Parse("pub fnc main() -> int :: return 42; end");
        Console.WriteLine("ATTO_NET_CALLABLE_VERSION_SCALAR_PASS|compiled=5|negative=9|full_parity=false");
    }
}
