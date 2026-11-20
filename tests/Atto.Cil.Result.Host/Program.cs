using Atto.Cil.ResultV1;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
    private static int Main()
    {
        var alice = new TenantParser(101);
        var bob = new TenantParser(102);
        for (long value = -20000; value <= 20000; value++)
        {
            var result = alice.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Check(result is Ok ok && ok.Value == value, $"round-trip {value}");
        }
        foreach (var (text, value) in new (string, long)[] {
            ("0", 0), ("+42", 42), ("-42", -42), ("-0", 0),
            ("00042", 42), ("-9223372036854775808", long.MinValue),
            ("9223372036854775807", long.MaxValue)
        })
            Check(alice.Parse(text) is Ok ok && ok.Value == value, $"valid {text}");
        foreach (string bad in new[] {
            "", "+", "-", " 42", "42 ", "--1", "+-1", "4_2", "42x",
            "4.2", "9223372036854775808", "-9223372036854775809",
            "٤٢", "０", "\0", "4\0" + "2"
        })
        {
            Check(alice.Parse(bad) is Err, $"must return Err {bad}");
            var result = (Err)alice.Parse(bad);
            Check(result.Error.Message == "invalid integer", "stable Error message");
            Check(alice.CanObserve(result.Error), "same tenant");
            Check(!bob.CanObserve(result.Error), "cross tenant denied");
        }
        Check(alice.Parse(new string('0', 1024)) is Ok, "1024 admissible");
        Check(alice.Parse(new string('0', 1025)) is Err, "1025 invalid");
        Check(alice.Parse(null) is Err, "host null invalid");
        var first = (Err)alice.Parse("bad");
        ParseResult returned = first;
        var second = (Err)alice.Parse("bad");
        Check(ReferenceEquals(((Err)returned).Error, first.Error), "Error object identity preserved");
        Check(!ReferenceEquals(second.Error, first.Error), "distinct errors stay distinct");
        Check(!bob.CanObserve(first.Error), "foreign Error denied");
        Check(bob.CanObserve(((Err)bob.Parse("bad")).Error), "local Error admitted");
        bool denied = false;
        try { _ = new TenantParser(0); }
        catch (ArgumentOutOfRangeException) { denied = true; }
        Check(denied, "tenant scope required");
        Console.WriteLine($"ATTO_CIL_RESULT_HOST_ONLY_PASS checks={_checks}");
        Console.WriteLine("ATTO_CIL_RESULT_GUEST=NOT_EXECUTED");
        return 0;
    }
}
