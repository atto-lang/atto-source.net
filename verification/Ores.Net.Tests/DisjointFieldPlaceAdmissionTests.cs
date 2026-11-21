using Ores.Net.Compiler;

// Imported Rust #477 source bytes are a TARGET-ONLY parity boundary.
// This test asserts fail-closed admission by BOTH shipping .NET frontends.
// It does not compile, run, or validate a mutable class field loan guest.
internal static class DisjointFieldPlaceAdmissionTests
{
    internal static void Run()
    {
        var fixtures = new (string Kind, string Filename)[] {
            ("pass", "two-disjoint-writers.atto"),
            ("pass", "int-write-through.atto"),
            ("fail", "overlap-owner-read.atto"),
            ("fail", "owner-take.atto")
        };
        foreach (var (kind, filename) in fixtures)
        {
            string source = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "field-place-loan-parity-v1", kind, filename));
            bool scalarDenied = false, structuredDenied = false;
            try { _ = OresParser.Parse(source); }
            catch (FormatException) { scalarDenied = true; }
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException) { structuredDenied = true; }
            if (!scalarDenied || !structuredDenied)
                throw new Exception("Unverified field-place borrow accepted by a .NET frontend: " +
                    kind + "/" + filename);
        }
        Console.WriteLine("ATTO_NET_DISJOINT_FIELD_LOAN_ADMISSION=DENIED; GUEST=NOT_EXECUTED");
    }
}
