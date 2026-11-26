// Fixed observation adapter. All arithmetic and function calls are in the
// Atto-generated ECMA-335 CIL, never calculated by this .NET host.
using System.Globalization;
internal static class Program
{
    private static int Main()
    {
        long result = AttoGenerated.Functions.main();
        Console.WriteLine(result.ToString(CultureInfo.InvariantCulture));
        return 0;
    }
}
