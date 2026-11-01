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

        // JSON parsers can silently select one of two repeated keys.
        // Never let ambiguous or extra fields become trusted loan metadata.
        string rawBorrow = root.GetProperty("borrow").GetRawText();
        using var duplicateIdentity = JsonDocument.Parse(
            rawBorrow[..^1] + ",\"executionId\":1002}");
        Expect<InvalidOperationException>(
            () => _ = Read(duplicateIdentity.RootElement),
            "duplicate execution identity must be rejected");
        using var unknownAuthority = JsonDocument.Parse(
            rawBorrow[..^1] + ",\"memory\":1}");
        Expect<InvalidOperationException>(
            () => _ = Read(unknownAuthority.RootElement),
            "unknown guest memory authority field must be rejected");

        // WASM/Node safe-integer admission must agree with the .NET JSON
        // conformance reader. Native ulong scheduler counters are independent.
        string rawMax = root.GetProperty("borrow").GetRawText();
        string originalId = "\\"executionId\\":1001";
        Require(rawMax.Contains(originalId, StringComparison.Ordinal),
            "exact expected canonical executionId");
        using var safeId = JsonDocument.Parse(rawMax.Replace(
            originalId, "\\"executionId\\":9007199254740991",
            StringComparison.Ordinal));
        Require(Read(safeId.RootElement).ExecutionId == MaxJsonSafeId,
            "maximum JS-safe integer JSON ID admitted");
        foreach (string unsafeId in new[] {
            "9007199254740992", "18446744073709551615", "0",
            "-1", "1.5", "\\"9007199254740991\\""
        })
        {
            using var forged = JsonDocument.Parse(rawMax.Replace(
                originalId, "\\"executionId\\":" + unsafeId,
                StringComparison.Ordinal));
            Expect<InvalidOperationException>(
                () => _ = Read(forged.RootElement),
                "JSON identity must reject unsafe value: " + unsafeId);
        }

        Expect<ArgumentOutOfRangeException>(() => _ =
            new StateRegionLoanProvenance(
                9, 3, 7, 1001, 0, 0, 501, "inventory.stock",
                StateRegionLoanProvenance.AccessKind.Write,
                StateRegionLoanProvenance.LoanAction.Borrow),
            "unbound execution handle rejected");

        Expect<ArgumentOutOfRangeException>(() => _ =
            new StateRegionLoanProvenance(
                9, 3, 7, 1001, 41, 6, 501, "inventory.stock",
                (StateRegionLoanProvenance.AccessKind)255,
                StateRegionLoanProvenance.LoanAction.Borrow),
            "forged runtime access enum rejected");
        Expect<ArgumentOutOfRangeException>(() => _ =
            new StateRegionLoanProvenance(
                9, 3, 7, 1001, 41, 6, 501, "inventory.stock",
                StateRegionLoanProvenance.AccessKind.Write,
                (StateRegionLoanProvenance.LoanAction)255),
            "forged runtime action enum rejected");
        var defaultBorrow = default(StateRegionLoanProvenance);
        Require(!defaultBorrow.AuthorizesRelease(valid),
            "default-initialized runtime receipt must not authorize release");
        Expect<InvalidOperationException>(() => _ = defaultBorrow.ReleaseRecord(),
            "default runtime receipt cannot mint release");

        var requirement = new StateRegionLoanRequirement(
            "inventory",
            StateRegionLoanRequirement.ExecutionBinding.CurrentLogicalExecution,
            true,
            501,
            "inventory.stock",
            StateRegionLoanProvenance.AccessKind.Write,
            StateRegionLoanProvenance.LoanAction.Borrow);
        Require(requirement.MatchesRelease(requirement.ReleaseRequirement()),
            "symbolic compiler requirement release");
        Expect<ArgumentException>(() => _ =
            new StateRegionLoanRequirement(
                "inventory",
                StateRegionLoanRequirement.ExecutionBinding.CurrentLogicalExecution,
                false,
                501,
                "inventory.stock",
                StateRegionLoanProvenance.AccessKind.Write,
                StateRegionLoanProvenance.LoanAction.Borrow),
            "compiler requirement cannot disable generation fence");

        Expect<ArgumentOutOfRangeException>(() => _ =
            new StateRegionLoanRequirement(
                "inventory",
                StateRegionLoanRequirement.ExecutionBinding.CurrentLogicalExecution,
                true, 501, "inventory.stock",
                (StateRegionLoanProvenance.AccessKind)255,
                StateRegionLoanProvenance.LoanAction.Borrow),
            "forged symbolic mode rejected");
        Expect<ArgumentOutOfRangeException>(() => _ =
            new StateRegionLoanRequirement(
                "inventory",
                StateRegionLoanRequirement.ExecutionBinding.CurrentLogicalExecution,
                true, 501, "inventory.stock",
                StateRegionLoanProvenance.AccessKind.Write,
                (StateRegionLoanProvenance.LoanAction)255),
            "forged symbolic action rejected");
        var defaultRequirement = default(StateRegionLoanRequirement);
        Require(!defaultRequirement.MatchesRelease(requirement.ReleaseRequirement()),
            "default symbolic requirement must not match release");
        Expect<InvalidOperationException>(() => _ = defaultRequirement.ReleaseRequirement(),
            "default symbolic requirement cannot mint release");

        Console.WriteLine(
            "PASS .NET state-region requirement/provenance v1 (1 positive, 9 negative vectors)");
    }

    private static StateRegionLoanProvenance Read(JsonElement value)
    {
        // Match the shared schema's additionalProperties=false semantics.
        // GetProperty() alone chooses the last duplicate JSON name, allowing
        // contradictory execution/loan identity fields to be smuggled.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "contractVersion", "regionId", "regionGeneration", "runtimeDomain",
            "executionId", "executionSlot", "executionGeneration", "loanId",
            "place", "kind", "action"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("state-region record must be an object");
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidOperationException(
                    "unknown or duplicate state-region provenance field: " + property.Name);
        }
        if (seen.Count != allowed.Count)
            throw new InvalidOperationException("missing state-region provenance field");
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
            ReadJsonSafeId(value, "regionId"),
            ReadJsonSafeId(value, "regionGeneration"),
            ReadJsonSafeId(value, "runtimeDomain"),
            ReadJsonSafeId(value, "executionId"),
            ReadJsonSafeId(value, "executionSlot"),
            ReadJsonSafeId(value, "executionGeneration"),
            ReadJsonSafeId(value, "loanId"),
            value.GetProperty("place").GetString()
                ?? throw new Exception("missing state-region place"),
            kind,
            action);
    }

    // JSON/JS interoperability contract: numeric IDs must round-trip without
    // precision loss through WebAssembly's Number.isSafeInteger boundary.
    // Native .NET runtime IDs remain full ulong; serialize larger IDs as
    // reviewed decimal strings in a future explicitly versioned contract.
    private const ulong MaxJsonSafeId = 9_007_199_254_740_991UL;

    private static ulong ReadJsonSafeId(JsonElement record, string name)
    {
        var field = record.GetProperty(name);
        if (field.ValueKind != JsonValueKind.Number ||
            !field.TryGetUInt64(out var id) || id == 0 || id > MaxJsonSafeId)
            throw new InvalidOperationException(
                "state-region JSON identity must be a positive safe integer: " + name);
        return id;
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
