using Ores.Net.Compiler;

int checks = 0;
void Reject(string source, string expected)
{
    try
    {
        _ = OresStructuredParser.Parse(source);
        throw new InvalidOperationException("Compiler accepted unlowered feature: " + expected);
    }
    catch (FormatException ex)
    {
        if (!ex.Message.Contains(expected, StringComparison.Ordinal) ||
            !ex.Message.Contains("offset", StringComparison.Ordinal))
            throw new InvalidOperationException("Wrong diagnostic for " + expected + ": " + ex.Message);
    }
    checks++;
    try
    {
        _ = OresStructuredCfgLowering.Lower(source);
        throw new InvalidOperationException("CFG published for unsupported feature: " + expected);
    }
    catch (FormatException ex)
    {
        if (!ex.Message.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException("CFG error did not preserve feature boundary: " + ex.Message);
    }
    checks++;
}
foreach (var source in new[]
{
    "fnc main():int{return match<int> 99 over on 1 -> {11;} default -> {42;} end;}",
    "fnc main():int{return match 99 over default -> {42;} end;}",
    "fnc main():int{return match 1 over on default -> {42;} end;}"
}) Reject(source, "MATCH_DEFAULT_BACKEND_UNSUPPORTED");

foreach (var source in new[]
{
    "fnc main():int{const m = ~map<string,int>(1 => 42);return 0;}",
    "fnc main():int{const r = ~(foo: 42);return 0;}",
    "fnc main():int{const m = ~map();return 0;}"
}) Reject(source, "TILDE_AGGREGATE_BACKEND_UNSUPPORTED");

// Feature spellings in comments remain invisible. Baseline scalar parsing
// still works; no accidental global-string blacklist is permitted.
var safe = OresStructuredParser.Parse("fnc main():int{/* match<int> x over */ // ~map()\nreturn 42;}");
if (safe.Body.Count != 1) throw new InvalidOperationException("valid scalar parse changed");
checks++;
Console.WriteLine($"NET_MATCH_TILDE_FAIL_CLOSED_PASS checks={checks} GUEST_NATIVE_AOT_NOT_EXECUTED");
