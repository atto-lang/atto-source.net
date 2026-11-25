using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

// Rust #477's byte-identical source fixtures are TARGET-ONLY on .NET.
// A syntax error or arbitrary FormatException is NOT valid proof that the
// nominal/field-loan source boundary is enforced correctly.
internal static class DisjointFieldPlaceAdmissionTests
{
    private const string NominalDiagnostic = "NET_NOMINAL_METHODS_UNSUPPORTED";

    private static void RejectNominal(
        Action parse, string frontend, string fixture, int classOffset)
    {
        try { parse(); }
        catch (FormatException ex) when (
            ex.Message.Contains(NominalDiagnostic, StringComparison.Ordinal) &&
            ex.Message.Contains("at offset " + classOffset, StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Expected nominal denial at exact class offset " +
            classOffset + " in " + frontend + " for " + fixture);
    }

    // StructuredSource lexes the complete input before nominal admission.
    // Its earlier ownership gate is intentional; assert the exact gate and
    // exact first borrow offset on the pinned multi-feature fixtures.
    private static void RejectStructuredBorrow(string source, string fixture)
    {
        int offset = source.IndexOf("borrow!", StringComparison.Ordinal);
        if (offset < 0) throw new Exception("Pinned borrow site missing: " + fixture);
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException ex) when (
            ex.Message.StartsWith("OWNERSHIP_BANG_BACKEND_UNSUPPORTED: 'borrow!'", StringComparison.Ordinal) &&
            ex.Message.EndsWith("at offset " + offset, StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Expected exact structured ownership denial at " + offset + " for " + fixture);
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

        // Isolate the nominal boundary independently of the earlier lexical
        // ownership gate; both frontends must still deny ordinary classes.
        const string nominalOnly = "define class Person as end\nfnc main():int{return 42;}";
        RejectNominal(() => { _ = OresParser.Parse(nominalOnly); }, "scalar", "nominal-only", 7);
        RejectNominal(() => { _ = OresStructuredParser.Parse(nominalOnly); }, "structured", "nominal-only", 7);

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
            const string declaration = "define class ";
            int declarationOffset = source.IndexOf(declaration, StringComparison.Ordinal);
            if (declarationOffset < 0)
                throw new Exception("Rust class declaration missing: " + fixtureName);
            int classOffset = declarationOffset + "define ".Length;
            RejectNominal(() => { _ = OresParser.Parse(source); },
                "scalar", fixtureName, classOffset);
            RejectStructuredBorrow(source, fixtureName);
        }
        Console.WriteLine("ATTO_NET_DISJOINT_FIELD_LOAN_ADMISSION_DENIED blobs=4 scalar_nominal=4 structured_borrow=4 nominal_controls=2; GUEST=NOT_EXECUTED");
    }
}
