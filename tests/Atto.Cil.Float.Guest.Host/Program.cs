// The divide and checked arithmetic execute in *generated guest CIL*.
// This fixed NativeAOT-compatible bridge only asserts their observed values.
internal static class Program
{
    private static int Main()
    {
        if (AttoGenerated.Functions.quotient(5, 2) != 2.5d ||
            AttoGenerated.Functions.quotient(-5, 2) != -2.5d ||
            AttoGenerated.Functions.quotient(5, -2) != -2.5d ||
            AttoGenerated.Functions.passthrough(5, 2) != 2.5d ||
            AttoGenerated.Functions.quotient(long.MinValue, 2) != -4611686018427387904d)
            return 13;
        // The integer operand must overflow *before* conversion to binary64.
        try
        {
            _ = AttoGenerated.Functions.checked_before_division(long.MaxValue, 2);
            return 14;
        }
        catch (OverflowException)
        {
            // Expected checked signed-i64 operand overflow.
        }
        return checked((int)AttoGenerated.Functions.main());
    }
}
