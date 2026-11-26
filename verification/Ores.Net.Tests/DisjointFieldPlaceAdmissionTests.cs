using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

// Rust #477's byte-identical source fixtures are TARGET-ONLY on .NET.
// A syntax error or arbitrary FormatException is NOT valid proof that the
// nominal/field-loan source boundary is enforced correctly.
internal static class DisjointFieldPlaceAdmissionTests
{
    private const string NominalDiagnostic = "NET_NOMINAL_METHODS_UNSUPPORTED";
    private const string OwnershipDiagnostic = "OWNERSHIP_BANG_BACKEND_UNSUPPORTED: 'borrow!'";

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

    private static void RejectStructuredOwnership(Action parse, string fixture, int loanOffset)
    {
        try { parse(); }
        catch (FormatException ex) when (
            ex.Message.StartsWith(OwnershipDiagnostic, StringComparison.Ordinal) &&
            ex.Message.EndsWith("at offset " + loanOffset, StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Expected structured ownership denial at exact borrow offset " +
            loanOffset + " for " + fixture);
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

        // The structured lexer rejects ownership before the later nominal
        // capability pass. Independently prove that a nominal declaration
        // without ownership syntax still reaches the exact nominal gate.
        const string nominalOnly =
            "define class Person as\n  pub let int age = 30;\nend\nfnc main():int{return 42;}";
        RejectNominal(() => { _ = OresParser.Parse(nominalOnly); },
            "scalar", "nominal-only-control", 7);
        RejectNominal(() => { _ = OresStructuredParser.Parse(nominalOnly); },
            "structured", "nominal-only-control", 7);

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
            // Each SHA-pinned Rust fixture contains an actual borrow! token
            // after the class declaration. In the structured lane the earlier
            // lexical ownership gate must reject that exact token; neither
            // an arbitrary parser error nor nominal acceptance counts.
            int loanOffset = source.IndexOf("borrow!", classOffset, StringComparison.Ordinal);
            if (loanOffset < 0)
                throw new Exception("Rust ownership token missing: " + fixtureName);
            RejectStructuredOwnership(() => { _ = OresStructuredParser.Parse(source); },
                fixtureName, loanOffset);
        }
        Console.WriteLine("ATTO_NET_DISJOINT_FIELD_LOAN_ADMISSION_DENIED blobs=4 frontends=2; GUEST=NOT_EXECUTED");
    }
}
