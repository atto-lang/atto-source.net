using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Ores.Net.Compiler;

// Source-owned .atto negative controls: real .NET scalar parser/CIL emitter.
// This is NOT NativeAOT guest execution, actor parity or full language support.
internal static class AttoSemanticNegativeGateTests
{
    private sealed record Case(string Id, string Source, string GitBlob,
        string? RequiredDiagnostic, bool SemanticCredit);

    // Same source bytes/Git object IDs as Java #2656 and LLVM #1573.
    private static readonly Case[] Negative =
    {
        new("removed-modulo", "pub fnc main(): int { return 9 % 2; }\n",
            "45d5794870e93074d9b11b90833a849e96aaa4c1",
            "noncanonical '%' source operator", true),
        new("removed-caret", "pub fnc main(): int { return 2 ^ 3; }\n",
            "6889c7e83c8d94b906fbfe78723caee9580da6aa",
            "Reserved AttoLang operator '^'", true),
        new("removed-double-caret", "pub fnc main(): int { return 2 ^^ 3; }\n",
            "7ee99948018e927cce74c9ad0edc1975e4251689",
            "Reserved AttoLang operator '^^'", true),
        new("type-first-reassignment",
            "pub fnc main(): int { int x = 7; x = 8; return x; }\n",
            "b118f53e68565dcb9e6acd58deb8ee47dca67606",
            null, false),
        new("typed-const-reassignment",
            "pub fnc main(): int { const int x = 7; x = 8; return x; }\n",
            "784ebd47a50211e6f00cb732cc74319ed1e58fac",
            null, false),
        new("legacy-async-modifier",
            "// Legacy async is forbidden for functions; use suspends instead.\n"
            + "async fnc main(): int {\n  return 0;\n}\n",
            "26d392c8f609a730e81b503d9495de7e29b46755",
            "E_CALLABLE_MODIFIERS", true),
        new("critical-suspends-modifier",
            "// A critical function must never carry a suspending modifier.\n"
            + "critical suspends fnc main(): int {\n  return 0;\n}\n",
            "6be781ff89f9ebc48ee5870d3401943d50b0f9db",
            "E_CALLABLE_MODIFIERS",
            true),
        new("suspends-critical-modifier",
            "// Reversing the illegal modifiers must be rejected too.\n"
            + "suspends critical fnc main(): int {\n  return 0;\n}\n",
            "c8bb7c921749f06e9d187a6dfbd71685952e3d88",
            "E_CALLABLE_MODIFIERS", true)
    };

    private static void Need(bool success, string detail)
    {
        if (!success) throw new InvalidOperationException("Atto .NET gate: " + detail);
    }

    private static string Blob(string source)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(source);
        var prefix = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        var buffer = new byte[prefix.Length + bytes.Length];
        Buffer.BlockCopy(prefix, 0, buffer, 0, prefix.Length);
        Buffer.BlockCopy(bytes, 0, buffer, prefix.Length, bytes.Length);
        return Convert.ToHexString(SHA1.HashData(buffer)).ToLowerInvariant();
    }

    private static bool IsIntended(Case row, string diagnostic)
    {
        if (!row.SemanticCredit || row.RequiredDiagnostic is null ||
            string.IsNullOrWhiteSpace(diagnostic) || diagnostic.Length > 65536)
            return false;
        var text = diagnostic.ToLowerInvariant();
        if (new[] { "unexpected token", "invalid character", "internal error",
            "unknown symbol", "stack overflow" }.Any(text.Contains)) return false;
        return diagnostic.Contains(row.RequiredDiagnostic, StringComparison.Ordinal)
            && text.Contains("offset", StringComparison.Ordinal);
    }

    private static void CheckOracleMutations()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Need(Negative.Length == 8, "expected eight negative inputs");
        foreach (var row in Negative)
        {
            Need(seen.Add(row.Id) && Blob(row.Source) == row.GitBlob,
                "duplicate/drifted canonical .atto source " + row.Id);
            Need(!IsIntended(row, "Unexpected token " + row.RequiredDiagnostic + " at offset 3"),
                "generic syntax failure credited as semantic rule");
            Need(!IsIntended(row, "internal error " + row.RequiredDiagnostic + " at offset 3"),
                "compiler crash credited as semantic rule");
            Need(!IsIntended(row, "compiler refused source"),
                "generic rejection credited as semantic rule");
            if (!row.SemanticCredit)
                Need(row.RequiredDiagnostic is null, "unsupported local semantics promoted");
        }
        Need(Negative.Count(c => c.SemanticCredit) == 6,
            "semantic evidence inflated by unsupported local bindings");
    }

    public static void Run()
    {
        CheckOracleMutations();
        var acceptedSemantic = 0;
        var unsupported = 0;
        foreach (var row in Negative)
        {
            FormatException error;
            try
            {
                _ = OresParser.Parse(row.Source); // real compiler lexer/parser
                throw new InvalidOperationException("forbidden source admitted: " + row.Id);
            }
            catch (FormatException ex)
            {
                error = ex;
            }
            if (row.SemanticCredit)
            {
                Need(IsIntended(row, error.Message),
                    "wrong semantic compiler diagnostic " + row.Id + ": " + error.Message);
                acceptedSemantic++;
            }
            else
            {
                Need(error.Message.Length is > 0 and < 65536,
                    "invalid unsupported-source diagnostic " + row.Id);
                unsupported++;
            }
            Console.WriteLine("ATTO_NET_NEGATIVE|" + row.Id +
                "|stage=" + (row.SemanticCredit ? "SEMANTIC_REJECT" : "UNSUPPORTED_NOT_SEMANTIC_PROOF"));
        }

        using var directory = new TemporaryDirectory();
        foreach (var (label, source, expected) in new[] {
            ("canonical-add", "pub fnc main(): int { return 40 + 2; }\n", 42L),
            ("canonical-multiply", "pub fnc main(): int { return 7 * 6; }\n", 42L),
            ("effect-words-in-comment", "pub fnc main(): int { /* async critical suspends */ return 42; }\n", 42L)
        })
        {
            var typed = OresIrLowering.Lower(OresParser.Parse(source));
            Need(OresIrReferenceEvaluator.Evaluate(typed) == expected,
                "valid source rejected or wrong scalar IR " + label);
            var output = Path.Combine(directory.Path, label + ".dll");
            CilEmitter.EmitLibrary(typed, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var method = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
            Need(CilReferenceEvaluator.Evaluate(method) == expected,
                "wrong emitted CIL for legal scalar source " + label);
        }
        Console.WriteLine($"ATTO_NET_SOURCE_GATE|semantic={acceptedSemantic}|unsupported={unsupported}|positive_ir_cil=3|NATIVE_AOT_NOT_EXECUTED");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "atto-net-negative-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
