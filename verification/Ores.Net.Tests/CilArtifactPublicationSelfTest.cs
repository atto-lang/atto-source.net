using System.Runtime.CompilerServices;
using System.Reflection.PortableExecutable;
using Ores.Net.Compiler;

internal static class CilArtifactPublicationSelfTest
{
    [ModuleInitializer]
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "atto-cil-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "main.atto");
            File.WriteAllText(input, "pub fnc main(): int { return 42; }");
            var first = OresSourceModuleCompiler.Compile(input, new[] { root });
            string output = Path.Combine(root, "OresGenerated.dll");
            CilEmitter.EmitLibrary(first.Module, output);
            byte[] initial = File.ReadAllBytes(output);
            using (var reader = new PEReader(new MemoryStream(initial)))
                Require(reader.HasMetadata, "initial CIL image has metadata");
            File.WriteAllText(input, "pub fnc main(): int { return 43; }");
            var second = OresSourceModuleCompiler.Compile(input, new[] { root });
            CilEmitter.EmitLibrary(second.Module, output);
            byte[] changed = File.ReadAllBytes(output);
            Require(!initial.AsSpan().SequenceEqual(changed), "compatible edit changes CIL artifact");
            string Digest(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var candidate = (byte[])initial.Clone();
            var admitted = PinnedScalarCilArtifact.Admit(candidate, Digest(initial));
            candidate[0] ^= 1;
            byte[] copy = admitted.CopyBytes();
            copy[0] ^= 1;
            Require(admitted.CopyBytes().AsSpan().SequenceEqual(initial), "immutable snapshot");
            void RejectAdmission(byte[] bytes, string digest)
            {
                try { PinnedScalarCilArtifact.Admit(bytes, digest); }
                catch (FormatException e) when (e.Message.Contains("E_CIL_ADMISSION", StringComparison.Ordinal)) { return; }
                throw new InvalidOperationException("Expected CIL admission denial");
            }
            RejectAdmission(changed, Digest(initial));
            RejectAdmission(candidate, Digest(candidate));
            RejectAdmission(initial, "invalid");
            byte[] truncated = initial[..128];
            RejectAdmission(truncated, Digest(truncated));
            var updated = PinnedScalarCilArtifact.Admit(changed, Digest(changed));
            Require(updated.CopyBytes().AsSpan().SequenceEqual(changed), "edited artifact admission");
            Console.WriteLine("CIL_PINNED_SNAPSHOT|actual-compiler|mutation-isolated|stale-pin-denied|rehashed-invalid-denied");
            string directory = Path.Combine(root, "directory.dll");
            Directory.CreateDirectory(directory);
            Reject(() => CilEmitter.EmitLibrary(second.Module, directory));
            Require(Directory.Exists(directory), "directory destination preserved");
            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(root, "link.dll");
                File.CreateSymbolicLink(link, output);
                Reject(() => CilEmitter.EmitLibrary(first.Module, link));
                Require(new FileInfo(link).LinkTarget is not null, "symlink preserved");
                Require(changed.AsSpan().SequenceEqual(File.ReadAllBytes(output)), "symlink target preserved");
                File.Delete(link);
                string absent = Path.Combine(root, "absent.dll");
                File.CreateSymbolicLink(link, absent);
                Reject(() => CilEmitter.EmitLibrary(first.Module, link));
                Require(new FileInfo(link).LinkTarget is not null && !File.Exists(absent), "dangling symlink preserved");
            }
            Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "no abandoned owned staging");
            Console.WriteLine("CIL_ARTIFACT_PUBLICATION_PASS|actual-compiler|replacement|directory-and-reparse-denial");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (IOException e) when (e.Message.Contains("E_ARTIFACT_PUBLICATION", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Expected destination admission rejection");
    }
}
