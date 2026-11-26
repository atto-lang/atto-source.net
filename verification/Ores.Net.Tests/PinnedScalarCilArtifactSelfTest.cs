using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Ores.Net.Compiler;

internal static class PinnedScalarCilArtifactSelfTest
{
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    [ModuleInitializer]
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "atto-cil-admit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "main.atto");
            string output = Path.Combine(root, "OresGenerated.dll");
            File.WriteAllText(source, "pub fnc main(): int { return 42; }");
            var compiled = OresSourceModuleCompiler.Compile(source, new[] { root });
            CilEmitter.EmitLibrary(compiled.Module, output);
            byte[] candidate = File.ReadAllBytes(output);
            string pin = Hash(candidate);
            var admitted = PinnedScalarCilArtifact.Admit(candidate, pin);
            candidate[0] ^= 1;
            byte[] exposed = admitted.CopyBytes();
            exposed[0] ^= 1;
            Require(Hash(admitted.CopyBytes()) == pin, "snapshot mutation isolation");
            Reject(() => PinnedScalarCilArtifact.Admit(candidate, pin));
            Reject(() => PinnedScalarCilArtifact.Admit(candidate, Hash(candidate)));
            byte[] truncated = admitted.CopyBytes()[..128];
            Reject(() => PinnedScalarCilArtifact.Admit(truncated, Hash(truncated)));
            Reject(() => PinnedScalarCilArtifact.Admit(admitted.CopyBytes(), "invalid"));
            File.WriteAllText(source, "pub fnc main(): int { return 43; }");
            CilEmitter.EmitLibrary(OresSourceModuleCompiler.Compile(source, new[] { root }).Module, output);
            byte[] edited = File.ReadAllBytes(output);
            Require(Hash(edited) != pin, "edit identity changes");
            Reject(() => PinnedScalarCilArtifact.Admit(edited, pin));
            Require(PinnedScalarCilArtifact.Admit(edited, Hash(edited)).CopyBytes().AsSpan().SequenceEqual(edited), "edited snapshot");
            Console.WriteLine("NET_PINNED_CIL|actual-compiler|snapshot=DEFENSIVE|rehash-malformed=DENIED|stale-pin=DENIED");
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
        catch (FormatException) { return; }
        catch (BadImageFormatException) { return; }
        throw new InvalidOperationException("Expected scalar CIL admission rejection");
    }
}
