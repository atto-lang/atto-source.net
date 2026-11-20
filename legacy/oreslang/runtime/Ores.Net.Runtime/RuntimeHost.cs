using System.Globalization;

namespace Ores.Net.Runtime;

/// <summary>Statically calls generated CIL; this method is compiled into the Native AOT executable.</summary>
public static class RuntimeHost
{
    public static async Task<int> ExecuteAsync(Func<long> entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            await Console.Out.WriteLineAsync(entry().ToString(CultureInfo.InvariantCulture));
            return 0;
        }
        catch (OverflowException)
        {
            await Console.Error.WriteLineAsync("Oreslang arithmetic overflow");
            return 70;
        }
        catch (DivideByZeroException)
        {
            await Console.Error.WriteLineAsync("Oreslang division by zero");
            return 70;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Integer.pow uses this CLR type for a negative exponent. Do not
            // leak a host stack trace or terminate as an unhandled exception.
            await Console.Error.WriteLineAsync("Oreslang negative integer exponent");
            return 70;
        }
    }
}
