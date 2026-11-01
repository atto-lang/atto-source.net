using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Ores.Net.Compiler;

/// <summary>
/// Canonical fifty-source scalar conformance: real .NET compiler AST -> typed
/// OresIR -> independently evaluated emitted CIL. This does not claim that a
/// Native AOT binary was executed or that other backends passed.
/// </summary>
internal static class SharedScalarFiftyParityTests
{
    private const string ExpectedBlob = "a948dbaf594916a6ee07f65d49a0cf5ae398ca7b";

    public static void Run()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "contracts", "parity", "scalar50", "v1.tsv");
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("scalar50 corpus is missing or symlinked");

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is <= 0 or > 65536)
            throw new InvalidOperationException("scalar50 corpus size drift");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"));
        hash.AppendData(bytes);
        var actualBlob = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (actualBlob != ExpectedBlob)
            throw new InvalidOperationException("scalar50 corpus Git blob differs from reviewed source");

        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (!text.EndsWith('\n') || text.Contains('\r'))
            throw new InvalidOperationException("scalar50 is not LF-only TSV");

        var count = 0;
        using var workspace = new ScalarWorkspace();
        foreach (var row in text.Split('\n'))
        {
            if (row.Length == 0 || row.StartsWith('#')) continue;
            var fields = row.Split('\t');
            ++count;
            var id = $"scalar-{count:000}";
            if (fields.Length != 3 || fields[0] != id)
                throw new InvalidOperationException($"scalar50 case inventory drift at {count}");
            if (!Regex.IsMatch(fields[1], "^[1-9][0-9]*$") ||
                !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var expected) || expected >= 128)
                throw new InvalidOperationException($"scalar50 invalid expected result: {id}");
            var source = fields[2];
            if (!Regex.IsMatch(source,
                    @"^pub fnc main\(\): int \{ return [-+*() 0-9]+; \}$"))
                throw new InvalidOperationException($"scalar50 changed source shape: {id}");

            var typed = OresIrLowering.Lower(OresParser.Parse(source));
            var interpreted = OresIrReferenceEvaluator.Evaluate(typed);
            if (interpreted != expected)
                throw new InvalidOperationException(
                    $"{id}: typed IR result mismatch {interpreted} != {expected}");
            var assemblyPath = Path.Combine(workspace.Path, id + ".dll");
            CilEmitter.EmitLibrary(typed, assemblyPath);
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            var method = assembly.MainModule.Types
                .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            var cilResult = CilReferenceEvaluator.Evaluate(method);
            if (cilResult != expected)
                throw new InvalidOperationException(
                    $"{id}: generated CIL result mismatch {cilResult} != {expected}");
            Console.WriteLine($"PASS {id} .NET OresIR/CIL result={expected}");
        }
        if (count != 50) throw new InvalidOperationException("scalar50 incomplete execution");
        Console.WriteLine($"SHARED_SCALAR50_NET_CIL_EXECUTED cases={count}");
    }

    private sealed class ScalarWorkspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ores-net-scalar50-" + Guid.NewGuid().ToString("N"));
        public ScalarWorkspace() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
