using Mono.Cecil;
using Ores.Net.Compiler;

// Two independent .NET frontends must accept the same scalar return-first
// declarations without pretending to support nominal/actor method lowering.
internal static class ReturnFirstBeginParityTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("return-first .NET parity: " + message);
    }

    private static void RejectBoth(string source)
    {
        bool scalarRejected = false;
        bool structuredRejected = false;
        try { _ = OresParser.Parse(source); }
        catch (FormatException) { scalarRejected = true; }
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException) { structuredRejected = true; }
        Require(scalarRejected, "scalar admitted malformed return-first source: " + source);
        Require(structuredRejected, "structured admitted malformed return-first source: " + source);
    }

    public static void Run()
    {
        using var temporary = new NativeCilTestDirectory();
        int compiledCases = 0;
        string[] headers = {
            "int <- main()", "int• main()", "fnc int <- main()", "fnc int• main()"
        };
        foreach (string header in headers)
        {
            foreach (string ending in new[] { "end", "end.", "end;" })
            {
                string source = header + "\nbegin\n  return 42;\n" + ending + "\n";
                var parsed = OresParser.Parse(source);
                Require(parsed is Literal, "scalar main AST: " + header);
                Require(OresStructuredParser.Parse(source).Body.Count == 1,
                    "structured main CFG: " + header);
                // Exercise actual production IR lowering and emitted CIL;
                // parser acceptance alone is not a native compiler test.
                var typed = OresIrLowering.Lower(parsed);
                Require(OresIrReferenceEvaluator.Evaluate(typed) == 42,
                    "typed scalar IR result: " + source);
                string dll = Path.Combine(temporary.Path, "case-" + compiledCases + ".dll");
                CilEmitter.EmitLibrary(typed, dll);
                using (var assembly = AssemblyDefinition.ReadAssembly(dll))
                {
                    var method = assembly.MainModule.Types.Single(t =>
                        t.FullName == "Oreslang.Generated.OresProgram")
                        .Methods.Single(m => m.Name == "Run");
                    Require(CilReferenceEvaluator.Evaluate(method) == 42,
                        "emitted CIL result: " + source);
                }
                compiledCases++;
            }
        }
        Require(compiledCases == 12, "partial return-first typed IR/CIL matrix");
        Console.WriteLine("return-first: 12 production IR and emitted CIL scalar checks; NOT NativeAOT executable proof");
        Require(OresStructuredParser.Parse(
            "fnc int <- add(int a, int b)\nbegin\n  return a + b;\nend;\n" +
            "int• main()\nbegin\n  return add(40, 2);\nend.").Body.Count == 1,
            "typed helper with return-first main");
        Require(OresParser.Parse(
            "int <- main()\r\nbegin // trivia\r\n return 42;\r\nend. // trivia") is Literal,
            "standalone CRLF begin with comment");
        foreach (string source in new[] {
            "int <- main() begin return 42; end",
            "int <- main()\nbegin return 42;\nend",
            "int <- main()\nbegin\nreturn 42;",
            "int <- main()\nbegin\nreturn 42;\nend..",
            "int <- main()\nbegin\nreturn 42;\nend; extra",
            "int < - main()\nbegin\nreturn 42;\nend",
            "fnc int <- main() -> int\nbegin\nreturn 42;\nend",
            "define actor A as\n  int <- read()\n  begin\n    return 42;\n  end\nend"
        }) RejectBoth(source);
    }
    private sealed class NativeCilTestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ores-net-return-first-" + Guid.NewGuid().ToString("N"));
        public NativeCilTestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

}
