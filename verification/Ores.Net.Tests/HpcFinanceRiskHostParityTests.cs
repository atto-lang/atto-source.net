using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ores.Net.Compiler;

// HOST-ONLY reference. The .NET scalar compiler has not compiled the
// finance float/Region Oreslang guest programs or produced NativeAOT evidence.
internal static class HpcFinanceRiskHostParityTests
{
    private readonly record struct Decision(bool Approved, int Reason);
    private readonly record struct Input(int Side, double Quantity, double Price, double Limit);

    private static void Require(bool accepted, string why)
    {
        if (!accepted) throw new InvalidOperationException("HPC host-only parity: " + why);
    }

    private static Decision Decide(Input value)
    {
        var reason = 0;
        if (!double.IsFinite(value.Quantity) || !double.IsFinite(value.Price) ||
            !double.IsFinite(value.Limit) || value.Limit < 0.0) reason = 4;
        else if (value.Side == 0) reason = 1;
        else if (value.Side != 1 && value.Side != -1) reason = 5;
        else if (value.Quantity < 0.0) reason = 2;
        else if (value.Quantity == 0.0) reason = 7;
        else if (value.Price <= 0.0) reason = 6;
        else if (value.Quantity > value.Limit) reason = 3;
        return new Decision(reason == 0, reason);
    }

    private static double Value(string text)
    {
        if (text == "NaN") return double.NaN;
        if (text == "+Infinity") return double.PositiveInfinity;
        if (text == "-Infinity") return double.NegativeInfinity;
        Require(text.Length is > 0 and < 48, "invalid numeric width");
        Require(double.TryParse(text, NumberStyles.AllowLeadingSign |
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
            out var parsed) && double.IsFinite(parsed), "invalid finite scalar");
        return parsed;
    }

    private static string[] ReadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "hpc-finance-risk-v1.tsv");
        var bytes = File.ReadAllBytes(path);
        Require(bytes.Length is > 0 and <= 8192, "invalid corpus length");
        var header = Encoding.ASCII.GetBytes("blob " +
            bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        digest.AppendData(header);
        digest.AppendData(bytes);
        var blob = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        Require(blob == "cec489c1f7603617797340dd1b4a71cc85acfebd",
            "finance fixture Git blob differs from reviewed bytes");
        return Encoding.UTF8.GetString(bytes).Split('\n')
            .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
    }

    private static void VerifyInventory(IReadOnlyList<string> rows)
    {
        Require(rows.Count == 23, "wrong 23-case inventory");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var accepted = 0;
        var rejected = 0;
        foreach (var row in rows)
        {
            Require(row.Length <= 256, "oversized row");
            var fields = row.Split('|');
            Require(fields.Length == 7, "wrong number of fields");
            Require(fields[0].Length > 0 && ids.Add(fields[0]), "duplicate case id");
            Require(int.TryParse(fields[1], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var side), "invalid side");
            Require(fields[5] is "true" or "false", "invalid approval flag");
            Require(int.TryParse(fields[6], NumberStyles.None,
                CultureInfo.InvariantCulture, out var expectedReason), "invalid reason");
            var output = Decide(new Input(side, Value(fields[2]), Value(fields[3]), Value(fields[4])));
            Require(output.Approved == (fields[5] == "true"), "approval mismatch: " + fields[0]);
            Require(output.Reason == expectedReason, "reason mismatch: " + fields[0]);
            if (output.Approved) accepted++;
            else rejected++;
        }
        Require(accepted == 3 && rejected == 20, "accepted/rejected inventory drift");
    }

    private static void MustReject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("finance contract mutation accepted");
    }

    private static void CheckUnsupportedGuestAdmission()
    {
        // No fake float support: preserve negative source admission until
        // typed f64, Region and execution parity actually exist.
        foreach (var source in new[]
        {
            "fnc main():float{return 1.0;}",
            "fnc main():int{return 1.25;}",
            "compute fnc risk_gate(Region<int> orders):void { return; }"
        })
        {
            try { _ = OresParser.Parse(source); }
            catch (FormatException) { continue; }
            throw new InvalidOperationException(
                "unsupported finance guest admitted by .NET scalar compiler: " + source);
        }
    }

    public static void Run()
    {
        var rows = ReadCorpus();
        VerifyInventory(rows);
        var duplicate = (string[])rows.Clone();
        duplicate[22] = duplicate[0];
        MustReject(() => VerifyInventory(duplicate));
        var removed = rows.Take(22).ToArray();
        MustReject(() => VerifyInventory(removed));
        var tamperedReason = (string[])rows.Clone();
        tamperedReason[0] = tamperedReason[0][..tamperedReason[0].LastIndexOf('|')] + "|5";
        MustReject(() => VerifyInventory(tamperedReason));
        var tamperedApproval = (string[])rows.Clone();
        tamperedApproval[0] = tamperedApproval[0].Replace("|true|", "|false|",
            StringComparison.Ordinal);
        MustReject(() => VerifyInventory(tamperedApproval));

        var counts = new int[8];
        var total = 0;
        foreach (var side in new[] { -2, -1, 0, 1, 2 })
        foreach (var quantity in new[] { -1.0, 0.0, 1.0, 3.0 })
        foreach (var price in new[] { -1.0, 0.0, 10.0 })
        foreach (var limit in new[] { -1.0, 0.0, 2.0 })
        {
            var decision = Decide(new Input(side, quantity, price, limit));
            Require(decision.Approved == (decision.Reason == 0), "inconsistent decision");
            counts[decision.Reason]++;
            total++;
        }
        Require(total == 180, "wrong risk cross-product");
        Require(counts.SequenceEqual(new[] { 2, 24, 12, 6, 60, 48, 16, 12 }),
            "risk precedence drift");
        CheckUnsupportedGuestAdmission();
        Console.WriteLine(
            "HPC_FINANCE_HOST_ONLY=PASS 23-fixture 180-cross-product 4-mutations; guest f64/Region unsupported");
    }
}
