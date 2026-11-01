using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Ores.Net.Compiler;

// Tests are managed compiler/CIL differential checks, not NativeAOT guest receipts.
// They intentionally become RED if the production source parser still accepts
// a forbidden operator (in particular legacy '%').
internal static class PortableScalarV1Tests
{
    private const string ExpectedGitBlob = "001e8894cbeb87509878a4de36263a1a327805b1";

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("portable scalar/v1: " + message);
    }

    public static void Run()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "contracts", "parity",
            "portable-scalar-v1-20261101.json");
        byte[] bytes = File.ReadAllBytes(file);
        Require(bytes.Length is > 0 and < 16384, "bounded manifest bytes");

        byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0");
        byte[] blob = new byte[header.Length + bytes.Length];
        header.CopyTo(blob, 0);
        bytes.CopyTo(blob, header.Length);
        string gitBlobSha = Convert.ToHexString(SHA1.HashData(blob)).ToLowerInvariant();
        Require(gitBlobSha == ExpectedGitBlob, "source fixture differs from all five backends");

        using var doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        Require(root.GetProperty("schema_version").GetString() == "2026-11-01.scalar-v1",
            "manifest schema drift");
        Require(root.GetProperty("scope").GetString() == "import-free-pure-i64",
            "manifest scope drift");
        JsonElement positive = root.GetProperty("positive");
        JsonElement negative = root.GetProperty("negative");
        Require(positive.GetArrayLength() == 16 && negative.GetArrayLength() == 6,
            "must execute every reviewed positive and negative case");

        using var work = new TemporaryDirectory();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        int checks = 0;
        foreach (JsonElement row in positive.EnumerateArray())
        {
            string id = row.GetProperty("id").GetString() ?? "";
            string expression = row.GetProperty("expression").GetString() ?? "";
            string expectedText = row.GetProperty("expected").GetString() ?? "";
            Require(Regex.IsMatch(id, "^[a-z][a-z0-9-]{1,79}$") && seenIds.Add(id),
                "duplicate or unsafe positive ID");
            Require(Regex.IsMatch(expression, "^[0-9_()+* -]{1,128}$"),
                "positive case contains unsupported operator");
            Require(long.TryParse(expectedText, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out long expected), "invalid expected i64");
            string source = "pub fnc main():int{return " + expression + ";}";
            var ir = OresIrLowering.Lower(OresParser.Parse(source));
            Require(OresIrReferenceEvaluator.Evaluate(ir) == expected,
                "typed compiler IR mismatch: " + id);
            string emitted = Path.Combine(work.Path, id + ".dll");
            CilEmitter.EmitLibrary(ir, emitted);
            using var assembly = AssemblyDefinition.ReadAssembly(emitted);
            var method = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
            Require(CilReferenceEvaluator.Evaluate(method) == expected,
                "emitted compiler CIL differs from i64 value: " + id);
            checks++;
        }

        foreach (JsonElement row in negative.EnumerateArray())
        {
            string id = row.GetProperty("id").GetString() ?? "";
            string expression = row.GetProperty("expression").GetString() ?? "";
            string reason = row.GetProperty("reason").GetString() ?? "";
            Require(Regex.IsMatch(id, "^[a-z][a-z0-9-]{1,79}$") && seenIds.Add(id),
                "duplicate or unsafe negative ID");
            Require(Regex.IsMatch(expression, "^[0-9_()+*%/^ -]{1,128}$"),
                "unreviewed negative syntax");
            Require(reason is "unsupported-int-division" or "removed-operator" or
                "literal-out-of-range", "unsupported rejection category");
            try
            {
                _ = OresParser.Parse("pub fnc main():int{return " + expression + ";}");
            }
            catch (FormatException)
            {
                checks++;
                continue;
            }
            throw new InvalidOperationException(
                "portable scalar/v1: source parser admitted forbidden operator/literal: " + id);
        }
        Require(checks == 22, "partial source/CIL matrix was credited as complete");
        Console.WriteLine("portable scalar/v1: 16 typed IR+CIL positives, 6 parser negatives; " +
            ExpectedGitBlob);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "oresnet-portable-v1-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
