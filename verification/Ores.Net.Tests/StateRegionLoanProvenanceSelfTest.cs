using Ores.Net.Compiler;
using System.Text.Json;

internal static class StateRegionLoanProvenanceSelfTest
{
    public static void Run()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "state-region-provenance-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Require(root.GetProperty("contractVersion").GetString() == "1.0",
            "canonical vector contract version");

        var borrow = Read(root.GetProperty("borrow"));
        var valid = Read(root.GetProperty("validRelease"));
        Require(borrow.AuthorizesRelease(valid),
            "canonical exact state-region release");

        int invalid = 0;
        foreach (var entry in root.GetProperty("invalidReleases").EnumerateArray())
        {
            string name = entry.GetProperty("name").GetString()
                ?? throw new Exception("invalid vector name");
            var release = Read(entry.GetProperty("release"));
            Require(!borrow.AuthorizesRelease(release),
                "canonical negative vector admitted: " + name);
            invalid++;
        }
        Require(invalid == 9, "all nine canonical negative vectors executed");

        Expect<ArgumentOutOfRangeException>(() => _ =
            new StateRegionLoanProvenance(
                9, 3, 7, 1001, 0, 0, 501, "inventory.stock",
                StateRegionLoanProvenance.AccessKind.Write,
                StateRegionLoanProvenance.LoanAction.Borrow),
            "unbound execution handle rejected");

        Console.WriteLine(
            "PASS .NET state-region loan provenance v1 (1 positive, 9 negative vectors)");
    }

    private static StateRegionLoanProvenance Read(JsonElement value)
    {
        Require(value.GetProperty("contractVersion").GetString() == "1.0",
            "record contract version");
        var kind = value.GetProperty("kind").GetString() switch
        {
            "read" => StateRegionLoanProvenance.AccessKind.Read,
            "write" => StateRegionLoanProvenance.AccessKind.Write,
            _ => throw new Exception("invalid state-region access kind")
        };
        var action = value.GetProperty("action").GetString() switch
        {
            "borrow" => StateRegionLoanProvenance.LoanAction.Borrow,
            "release" => StateRegionLoanProvenance.LoanAction.Release,
            _ => throw new Exception("invalid state-region loan action")
        };
        return new StateRegionLoanProvenance(
            value.GetProperty("regionId").GetUInt64(),
            value.GetProperty("regionGeneration").GetUInt64(),
            value.GetProperty("runtimeDomain").GetUInt64(),
            value.GetProperty("executionId").GetUInt64(),
            value.GetProperty("executionSlot").GetUInt64(),
            value.GetProperty("executionGeneration").GetUInt64(),
            value.GetProperty("loanId").GetUInt64(),
            value.GetProperty("place").GetString()
                ?? throw new Exception("missing state-region place"),
            kind,
            action);
    }

    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("FAILED: " + why);
    }

    private static void Expect<T>(Action action, string why) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("FAILED expected " + typeof(T).Name + ": " + why);
    }
}
