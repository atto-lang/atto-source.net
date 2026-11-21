using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

// Rust #477's byte-identical source fixtures are TARGET-ONLY on .NET.
// A syntax error or arbitrary FormatException is NOT valid proof that the
// nominal/field-loan source boundary is enforced correctly.
internal static class DisjointFieldPlaceAdmissionTests
{
    private const string NominalDiagnostic = "NET_NOMINAL_METHODS_UNSUPPORTED";

    private static void RejectNominal(Action parse, string frontend, string fixture)
    {
        try { parse(); }
        catch (FormatException ex) when (
            ex.Message.Contains(NominalDiagnostic, StringComparison.Ordinal) &&
            ex.Message.Contains("offset", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Expected specific nominal feature denial in " +
            frontend + " for " + fixture);
    }

    private static string GitBlobSha(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        byte[] objectBytes = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, objectBytes, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, objectBytes, header.Length, bytes.Length);
        return Convert.ToHexString(SHA1.HashData(objectBytes)).ToLowerInvariant();
    }

    internal static void Run()
    {
        const string accepted = "fnc main():int{return 42;}";
        if (OresParser.Parse(accepted) is null ||
            OresStructuredParser.Parse(accepted).Body.Count != 1)
            throw new Exception("Valid scalar control incorrectly denied");

        var fixtures = new (string Kind, string Filename, string BlobSha)[] {
            ("pass", "two-disjoint-writers.atto", "b22a031b37d9722b8a7d20b48e81136ec4552099"),
            ("pass", "int-write-through.atto", "24a84848cb97bf3e72d5fe329e4b74b4b5826fa9"),
            ("fail", "overlap-owner-read.atto", "ef941b1713711291cf17e7fbb250794bd7b0013f"),
            ("fail", "owner-take.atto", "f9df5b86a8d0ffdcdc357146ff05da7bc49dc2a3")
        };
        foreach (var (kind, filename, expectedBlob) in fixtures)
        {
            string fixtureName = kind + "/" + filename;
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "field-place-loan-parity-v1", kind, filename));
            if (GitBlobSha(bytes) != expectedBlob)
                throw new Exception("Rust exact Git blob mismatch: " + fixtureName);
            string source = new UTF8Encoding(false, true).GetString(bytes);
            RejectNominal(() => { _ = OresParser.Parse(source); },
                "scalar", fixtureName);
            RejectNominal(() => { _ = OresStructuredParser.Parse(source); },
                "structured", fixtureName);
        }
        Console.WriteLine("ATTO_NET_DISJOINT_FIELD_LOAN_ADMISSION_DENIED blobs=4 frontends=2; GUEST=NOT_EXECUTED");
    }
}
