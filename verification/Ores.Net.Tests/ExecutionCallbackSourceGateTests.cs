using Ores.Net.Compiler;
using System.Security.Cryptography;
using System.Text;

internal static class ExecutionCallbackSourceGateTests
{
    private const string ExpectedFixtureBlob =
        "83f7db76fd6437d7d4994210e78034b93a5bff26";
    private const string ExpectedDiagnostic =
        "async/Future callback source requires scheduler-aware continuation lowering";

    internal static void Run()
    {
        static void Require(bool condition, string reason)
        {
            if (!condition) throw new Exception(
                "Execution callback source gate: " + reason);
        }

        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "execution-context-callbacks",
            "v1",
            "callback_sync_resolve.ores");
        Require(File.Exists(fixture), "canonical fixture missing from test output");

        var bytes = File.ReadAllBytes(fixture);
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        var gitObject = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, gitObject, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, gitObject, header.Length, bytes.Length);
        var blob = Convert.ToHexString(SHA1.HashData(gitObject)).ToLowerInvariant();
        Require(blob == ExpectedFixtureBlob,
            "canonical fixture Git blob drift: " + blob);

        var source = new UTF8Encoding(false, true).GetString(bytes);
        bool rejected = false;
        try
        {
            _ = OresParser.Parse(source);
        }
        catch (FormatException ex) when
            (ex.Message.Contains(ExpectedDiagnostic, StringComparison.Ordinal))
        {
            rejected = true;
        }
        Require(rejected,
            "unsupported callback source was admitted or misclassified");

        var control = OresParser.Parse("fnc main():int{return 42;}");
        Require(control is Literal { Value: 42 },
            "positive scalar parser control failed");
    }
}
