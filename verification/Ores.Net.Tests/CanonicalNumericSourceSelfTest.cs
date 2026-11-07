using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Ores.Net.Compiler;

/// <summary>
/// Compiles the byte-identical Oreslang core source through the actual .NET
/// module parser, typed CFG, deterministic CIL emitter and CIL interpreter.
/// No numeric algorithms are supplied by host C#; the JSON expectations
/// are reviewed independent reference vectors.
/// </summary>
internal static class CanonicalNumericSourceSelfTest
{
    private const string PinnedSourceSha256 =
        "3d1d7b77068dd02b3d60b30b72f29a4537a933134ca79e1e81df096dc56f5f33";

    [ModuleInitializer]
    internal static void Run()
    {
        string repository = FindRepository();
        string sourcePath = Path.Combine(repository, "library", "ores.core", "numeric.ores");
        byte[] canonical = File.ReadAllBytes(sourcePath);
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(canonical));
        Require(sourceHash == PinnedSourceSha256, "vendored canonical source digest drifted");
        string canonicalText = new UTF8Encoding(false, true).GetString(canonical);
        Require(canonicalText.Contains("define module numeric as", StringComparison.Ordinal),
            "source must remain an Oreslang module, not a host-generated substitute");
        Require(!canonicalText.Contains("java:", StringComparison.Ordinal),
            "canonical core must not import Java");
        Require(canonicalText.EndsWith("\n", StringComparison.Ordinal),
            "canonical source has no terminal LF");

        string contractPath = Path.Combine(repository, "verification", "contracts", "core-math-i64.json");
        using var json = JsonDocument.Parse(File.ReadAllBytes(contractPath));
        var contract = json.RootElement;
        Require(contract.GetProperty("contract").GetString() == "ores-core-math-signed-i64-v1",
            "unexpected reference contract");
        var cases = contract.GetProperty("cases");
        Require(cases.GetArrayLength() == 24, "canonical corpus must contain 24 independent vectors");

        string temporary = Path.Combine(Path.GetTempPath(),
            "ores-net-native-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            File.WriteAllBytes(Path.Combine(temporary, "numeric.ores"), canonical);
            foreach (var vector in cases.EnumerateArray())
            {
                string id = vector.GetProperty("id").GetString()!;
                string callable = vector.GetProperty("function").GetString()!;
                Require(callable is "min_int" or "max_int" or "clamp_int",
                    "unrecognized canonical function: " + id);
                string[] args = vector.GetProperty("args").EnumerateArray()
                    .Select(a => a.GetString()!).ToArray();
                string expected = vector.GetProperty("expected").GetString()!;
                Require(long.TryParse(expected, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out long expectedValue),
                    "reference expected value is not i64: " + id);

                string caller = "import module numeric from \"./numeric\";\n" +
                    "pub fnc main(): int { return numeric." + callable +
                    "(" + string.Join(", ", args) + "); }\n";
                File.WriteAllText(Path.Combine(temporary, "main.ores"),
                    caller, new UTF8Encoding(false));
                var linked = OresSourceModuleCompiler.Compile(
                    Path.Combine(temporary, "main.ores"), new[] { temporary });
                Require(linked.Sources.Count == 2, "not a two-unit guest compilation: " + id);
                Require(linked.Sources.Any(s =>
                    s.Path.EndsWith("/numeric.ores", StringComparison.Ordinal) && s.Sha256 == PinnedSourceSha256),
                    "canonical numeric source was not the compiled dependency: " + id);
                string output = Path.Combine(temporary, "OresGenerated.dll");
                CilEmitter.EmitLibrary(linked.Module, output);
                using var image = AssemblyDefinition.ReadAssembly(output);
                var entry = image.MainModule.Types.Single(t => t.Name == "OresProgram")
                    .Methods.Single(m => m.Name == "Run");
                long observed = CfgCilTestInterpreter.Evaluate(entry);
                Require(observed == expectedValue,
                    id + ": guest CIL returned " + observed +
                    ", reference expects " + expectedValue);
            }

            // Unsupported source in a canonical module must be rejected by
            // the actual compiler, not lowered to host C# or silently skipped.
            File.WriteAllText(Path.Combine(temporary, "numeric.ores"),
                canonicalText.Replace("define module numeric as",
                    "define module numeric as\n  define class Forbidden as end",
                    StringComparison.Ordinal), new UTF8Encoding(false));
            try
            {
                _ = OresSourceModuleCompiler.Compile(
                    Path.Combine(temporary, "main.ores"), new[] { temporary });
                throw new Exception("Canonical core source injection unexpectedly compiled");
            }
            catch (FormatException denied)
            {
                Require(denied.Message.Contains("Nominal class/actor", StringComparison.Ordinal),
                    "unsupported class source failed without classified diagnostic");
            }
            Console.WriteLine("PASS 24 canonical .ores numeric vectors: real module parser -> verified CFG -> emitted/interpreted CIL");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static string FindRepository()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory());
            current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "library", "ores.core", "numeric.ores")) &&
                File.Exists(Path.Combine(current.FullName, "verification", "contracts", "core-math-i64.json")))
                return current.FullName;
        }
        throw new Exception("Canonical core sources not found in this verified source checkout");
    }

    private static void Require(bool success, string message)
    {
        if (!success) throw new Exception("Canonical numeric CIL conformance: " + message);
    }
}
