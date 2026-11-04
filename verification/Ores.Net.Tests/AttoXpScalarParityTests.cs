using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Ores.Net.Compiler;

// Host verification: exact AttoXP .atto bytes -> production scalar parser ->
// checked OresIR -> emitted CIL -> test-only CIL evaluator. This does NOT
// establish NativeAOT publication, mobile UI, callbacks, actors or hardware.
internal static class AttoXpScalarParityTests
{
    private static readonly (string File, int Expected, string Sha)[] Cases =
    {
        ("answer-42.atto", 42, "08a415734e9992f47702dc9b1ccf2c5dceb74df05245eaa5fbb00512d18afc8a"),
        ("decrement-41.atto", 41, "90da332e7fb4e13443a7c6f741e03d872c8d0422359842f9291326c3c98e1fdf"),
        ("reset-zero.atto", 0, "c26a72daa25e2d8da6ce0f5b8e1373c444db82537b74da9f687f43307277d6cf"),
        ("multiply-42.atto", 42, "e07b8ff18f11896a087db1c608d5206df1e36832f5ae6f4b2f8cc505338e342f")
    };
    private static void Need(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("attoxp CIL source parity: " + message);
    }
    private static void Pinned(byte[] bytes, string digest)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Need(actual == digest, "fixture bytes differ from LLVM and Wasm contract");
    }
    public static void Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "contracts", "attoxp-scalar-v1");
        Need(Directory.Exists(root), "missing scalar corpus");
        var files = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        var allowed = Cases.Select(c => c.File).Append("cases.json")
            .ToHashSet(StringComparer.Ordinal);
        Need(files.SetEquals(allowed), "unexpected or missing corpus files");
        var manifest = File.ReadAllBytes(Path.Combine(root, "cases.json"));
        Need(manifest.Length is > 0 and <= 16384, "invalid corpus manifest size");
        using var doc = JsonDocument.Parse(manifest);
        var corpus = doc.RootElement;
        Need(corpus.GetProperty("contract").GetString() == "attolang.attoxp.scalar-parity/1",
            "contract mismatch");
        Need(corpus.GetProperty("truth").GetString() == "SCALAR_ONLY_NOT_UI_ABI_OR_ACTOR",
            "truth label mismatch");
        var entries = corpus.GetProperty("cases");
        Need(entries.GetArrayLength() == Cases.Length, "missing cases");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var file = entry.GetProperty("file").GetString() ?? "";
            Need(seen.Add(file), "duplicate manifest file");
            var row = Cases.SingleOrDefault(c => c.File == file);
            Need(row.File == file, "unrecognized fixture");
            Need(entry.GetProperty("sha256").GetString() == row.Sha &&
                entry.GetProperty("expectedExit").GetInt32() == row.Expected,
                "manifest proof drift");
        }
        var temp = Path.Combine(Path.GetTempPath(), "attoxp-cil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var row in Cases)
            {
                var path = Path.Combine(root, row.File);
                var info = new FileInfo(path);
                Need(info.Exists && (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0 &&
                    info.Length is > 0 and <= 4096, "unsafe source fixture");
                var bytes = File.ReadAllBytes(path);
                Pinned(bytes, row.Sha);
                var source = new UTF8Encoding(false, true).GetString(bytes);
                var typed = OresIrLowering.Lower(OresParser.Parse(source));
                Need(OresIrReferenceEvaluator.Evaluate(typed) == row.Expected,
                    "typed IR evaluator mismatch: " + row.File);
                var assemblyPath = Path.Combine(temp, row.File + ".dll");
                CilEmitter.EmitLibrary(typed, assemblyPath);
                using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
                var method = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
                Need(CilReferenceEvaluator.Evaluate(method) == row.Expected,
                    "emitted CIL evaluator mismatch: " + row.File);
                var mutated = (byte[])bytes.Clone(); mutated[0] ^= 1;
                Need(Convert.ToHexString(SHA256.HashData(mutated)).ToLowerInvariant() != row.Sha,
                    "fixture mutation must fail pin");
            }
        }
        finally { Directory.Delete(temp, recursive: true); }
        Console.WriteLine("AttoXP scalar CIL: 4 pinned .atto sources verified by IR and emitted CIL reference evaluators; NOT NativeAOT guest execution");
    }
}
