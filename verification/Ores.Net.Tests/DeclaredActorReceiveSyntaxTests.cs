using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ores.Net.Compiler;

internal static class DeclaredActorReceiveSyntaxTests
{
    private static void Require(bool result, string reason)
    {
        if (!result) throw new Exception("declared Counter receive: " + reason);
    }

    private static void Reject(string source, string description)
    {
        try { _ = OresParser.InspectDeclaredActorReceive(source); }
        catch (FormatException) { return; }
        throw new Exception("unverified declared Counter receive admitted: " + description);
    }

    public static void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "contracts", "actor-counter-identical-source-v1", "corpus.json");
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = corpus.RootElement;
        Require(root.GetProperty("status").GetString() == "CANDIDATE_NOT_EXECUTED"
            && root.GetProperty("verifiedBackends").GetArrayLength() == 0,
            "static corpus cannot claim full backend execution");
        var units = root.GetProperty("sourceUnits");
        var blobs = root.GetProperty("sourceUnitBlobs");
        Require(units.EnumerateObject().Count() == 7 &&
                blobs.EnumerateObject().Count() == 7,
            "must use the exact seven-file source graph");
        foreach (var unit in units.EnumerateObject())
        {
            string source = unit.Value.GetString()!;
            var payload = Encoding.UTF8.GetBytes(source);
            var header = Encoding.UTF8.GetBytes($"blob {payload.Length}\0");
            var bytes = new byte[header.Length + payload.Length];
            header.CopyTo(bytes, 0);
            payload.CopyTo(bytes, header.Length);
            string sha = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            Require(sha == blobs.GetProperty(unit.Name).GetString(),
                "canonical Git blob changed: " + unit.Name);
        }

        string receive = units.GetProperty("src/counter/receive.atto").GetString()!;
        var checkedSyntax = OresParser.InspectDeclaredActorReceive(receive);
        Require(checkedSyntax.Namespace == "counter" &&
                checkedSyntax.ActorName == "Counter" &&
                checkedSyntax.ReceiverName == "c" &&
                checkedSyntax.MailName == "mail" &&
                checkedSyntax.StateField == "total" &&
                checkedSyntax.SnapshotName == "snapshot" &&
                checkedSyntax.StopOnMailValue == 4L &&
                DeclaredActorReceiveSyntax.EvidenceScope == "PARSED_SOURCE_ONLY_NOT_EXECUTED",
            "typed, unprivileged receive AST differs from canonical source");

        Reject(receive.Replace("critical-read |s| -> s.total",
            "critical-read |s| -> { self.send(s.total); }",
            StringComparison.Ordinal), "mailbox publication inside live read loan");
        Reject(receive.Replace("critical-read |s| -> s.total",
            "critical-write |s| -> s.total", StringComparison.Ordinal),
            "value form cannot mint a write loan");
        Reject(receive.Replace("critical-read", "critical -read", StringComparison.Ordinal),
            "spaced critical mode cannot mint read loan");
        Reject(receive.Replace("critical-write", "critical- write", StringComparison.Ordinal),
            "spaced critical mode cannot mint write loan");
        Reject(receive.Replace("self.send(snapshot)", "self.send(s.total)",
            StringComparison.Ordinal), "borrowed state view cannot be sent");
        Reject(receive.Replace("self.send(snapshot)", "self.send(mail.value)",
            StringComparison.Ordinal), "snapshot must be the published value");
        Reject(receive.Replace("val int snapshot", "let int snapshot",
            StringComparison.Ordinal), "snapshot must be immutable");
        Reject(receive.Replace("s.total + mail.value", "s.total + 1",
            StringComparison.Ordinal), "checked add must use owned ActorMail input");
        Reject(receive.Replace("critical-read |s|", "critical-read |c|",
            StringComparison.Ordinal).Replace("-> s.total);", "-> c.total);",
            StringComparison.Ordinal), "state view cannot shadow external actor receiver");
        Reject(receive.Replace("self.end();", "self.send(11);",
            StringComparison.Ordinal), "stop-on-mail must actually seal actor");
        Reject(receive.Replace("return;", "return s.total;",
            StringComparison.Ordinal), "state capability must not escape through return");
        Reject(receive.Replace("external mtd", "pub mtd", StringComparison.Ordinal),
            "runtime-only receive must not become callable public method");

        Console.WriteLine("NET_DECLARED_COUNTER_RECEIVE_SYNTAX_PASS|" +
            "7_source_blobs_verified|guest_not_executed");
    }
}
