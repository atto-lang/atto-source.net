using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Ores.Net.Compiler;

internal static class SharedScalarParityConformance
{
    private const string ExpectedSuiteSha256 =
        "5b5d1bc00def9b3a343cb3281e0bb0be4e5ca438095f621141049e2d7a1a25b3";

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("shared scalar parity: " + message);
    }

    private sealed record ReviewedCase(string Id, string File, string Kind,
        string? Expected, byte[] SourceBytes);

    private static void RequirePinnedSuite(byte[] manifest,
        IReadOnlyList<ReviewedCase> reviewed)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(manifest);
        foreach (var row in reviewed)
        {
            hash.AppendData(Encoding.ASCII.GetBytes(row.File));
            hash.AppendData(new byte[] { 0 });
            hash.AppendData(row.SourceBytes);
        }
        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        Require(digest == ExpectedSuiteSha256, "suite SHA-256 differs from Wasm/JS contract");
    }

    private static void CheckTamperedFixtureRegression(byte[] manifest,
        IReadOnlyList<ReviewedCase> reviewed)
    {
        Require(reviewed.Count > 0 && reviewed[0].SourceBytes.Length > 0,
            "tamper test requires fixture bytes");
        var corrupted = (byte[])reviewed[0].SourceBytes.Clone();
        corrupted[0] ^= 1;
        var altered = reviewed.ToList();
        altered[0] = altered[0] with { SourceBytes = corrupted };
        var refused = false;
        try
        {
            RequirePinnedSuite(manifest, altered);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("suite SHA-256 differs", StringComparison.Ordinal))
        {
            refused = true;
        }
        Require(refused, "mutated fixture must fail corpus admission");

        // A fixture-only mutation test does not prove that changes to
        // cases.json itself are caught before parsing or compiling a guest.
        var changedManifest = (byte[])manifest.Clone();
        changedManifest[0] ^= 1;
        var manifestRefused = false;
        try
        {
            RequirePinnedSuite(changedManifest, reviewed);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("suite SHA-256 differs", StringComparison.Ordinal))
        {
            manifestRefused = true;
        }
        Require(manifestRefused, "mutated scalar manifest must fail corpus admission");
    }

    private static void RequireSafeDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        Require(info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0,
            "unsafe scalar corpus directory: " + path);
    }

    private static void RequireSafeFile(string path)
    {
        var info = new FileInfo(path);
        Require(info.Exists && (info.Attributes &
            (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0,
            "unsafe scalar fixture file: " + path);
    }

    private static void RequireExactInventory(string fixtureDirectory,
        IReadOnlySet<string> referenced)
    {
        RequireSafeDirectory(fixtureDirectory);
        var entries = Directory.EnumerateFileSystemEntries(fixtureDirectory).ToArray();
        Require(entries.Length == referenced.Count,
            "unlisted fixture or missing reviewed fixture");
        foreach (var path in entries)
        {
            RequireSafeFile(path);
            Require(referenced.Contains(Path.GetFileName(path)),
                "unlisted fixture: " + Path.GetFileName(path));
        }
    }

    // Managed-only mutation regression: extras must fail before a test can
    // present the pinned shared corpus as complete.
    private static void CheckUnlistedFixtureRegression()
    {
        using var temp = new TemporaryDirectory();
        var fixtures = Path.Combine(temp.Path, "fixtures");
        Directory.CreateDirectory(fixtures);
        File.WriteAllText(Path.Combine(fixtures, "pinned.ores"), "42");
        var expected = new HashSet<string>(StringComparer.Ordinal) { "pinned.ores" };
        RequireExactInventory(fixtures, expected);
        File.WriteAllText(Path.Combine(fixtures, "unexpected.ores"), "43");
        var refused = false;
        try
        {
            RequireExactInventory(fixtures, expected);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("unlisted fixture", StringComparison.Ordinal))
        {
            refused = true;
        }
        Require(refused, "unlisted scalar fixture mutation must be rejected");
    }

    public static void Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "contracts", "scalar", "v1");
        RequireSafeDirectory(root);
        var fixtureDirectory = Path.Combine(root, "fixtures");
        RequireSafeDirectory(fixtureDirectory);
        var manifestPath = Path.Combine(root, "cases.json");
        RequireSafeFile(manifestPath);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        Require(manifestBytes.Length is > 0 and <= 65536, "invalid manifest size");

        using var document = JsonDocument.Parse(manifestBytes);
        var corpus = document.RootElement;
        Require(corpus.GetProperty("schema_version").GetString() == "1.0.0",
            "schema version drift");
        var cases = corpus.GetProperty("cases");
        Require(cases.ValueKind == JsonValueKind.Array && cases.GetArrayLength() == 11,
            "case inventory drift");

        var reviewed = new List<ReviewedCase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reviewedFiles = new HashSet<string>(StringComparer.Ordinal);
        var accepts = 0;
        var rejects = 0;
        foreach (var testCase in cases.EnumerateArray())
        {
            var id = testCase.GetProperty("id").GetString() ?? "";
            var file = testCase.GetProperty("file").GetString() ?? "";
            var kind = testCase.GetProperty("kind").GetString() ?? "";
            Require(System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z][a-z0-9.-]{1,79}$"),
                "unsafe case id");
            Require(seen.Add(id), "duplicate case id: " + id);
            Require(System.Text.RegularExpressions.Regex.IsMatch(file,
                "^fixtures/[a-z0-9-]+\\.ores$"), "unsafe fixture path: " + file);

            Require(reviewedFiles.Add(Path.GetFileName(file)),
                "duplicate scalar fixture path: " + file);
            var fixturePath = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            RequireSafeFile(fixturePath);
            var sourceBytes = File.ReadAllBytes(fixturePath);
            Require(sourceBytes.Length is > 0 and <= 1048576, "invalid fixture size: " + file);
            if (kind == "accept")
            {
                accepts++;
                reviewed.Add(new ReviewedCase(id, file, kind,
                    testCase.GetProperty("value").GetString(), sourceBytes));
            }
            else
            {
                Require(kind == "reject", "unknown case kind: " + kind);
                rejects++;
                reviewed.Add(new ReviewedCase(id, file, kind, null, sourceBytes));
            }
        }

        Require(accepts == 6 && rejects == 5, "accept/reject counts changed");
        // Authenticate the entire corpus before any parser, IR or CIL execution.
        RequireExactInventory(fixtureDirectory, reviewedFiles);
        RequirePinnedSuite(manifestBytes, reviewed);
        CheckUnlistedFixtureRegression();
        CheckTamperedFixtureRegression(manifestBytes, reviewed);

        using var temp = new TemporaryDirectory();
        foreach (var row in reviewed)
        {
            var source = new UTF8Encoding(false, true).GetString(row.SourceBytes);
            if (row.Kind == "accept")
            {
                Require(long.TryParse(row.Expected, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var expected),
                    "invalid expected i64: " + row.Id);
                var typed = OresIrLowering.Lower(OresParser.Parse(source));
                Require(OresIrReferenceEvaluator.Evaluate(typed) == expected,
                    "typed IR result mismatch: " + row.Id);
                var assemblyPath = Path.Combine(temp.Path, row.Id.Replace('.', '-') + ".dll");
                CilEmitter.EmitLibrary(typed, assemblyPath);
                using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
                var method = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
                Require(CilReferenceEvaluator.Evaluate(method) == expected,
                    "emitted CIL result mismatch: " + row.Id);
                continue;
            }

            try
            {
                _ = OresParser.Parse(source);
                throw new InvalidOperationException(
                    "shared scalar parity: rejected fixture admitted: " + row.Id);
            }
            catch (FormatException)
            {
                // The bootstrap parser must reject the shared negative fixtures.
            }
        }
        Console.WriteLine("shared scalar parity: 6 accepts + 5 denials; suite " +
            ExpectedSuiteSha256);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ores-net-shared-scalar-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
