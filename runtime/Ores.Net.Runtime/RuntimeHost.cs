using System.Globalization;

namespace Ores.Net.Runtime;

/// <summary>Statically calls generated CIL; this method is compiled into the Native AOT executable.</summary>
public static class RuntimeHost
{
    public static Task<int> ExecuteAsync(Func<long> entry) => ExecuteAsync(entry, false);

    public static async Task<int> ExecuteAsync(Func<long> entry, bool allowDeferredStdout)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            string output = "";
            long result;
            try { result = GuestDeferredStdout.Execute(entry, allowDeferredStdout, out output); }
            finally
            {
                if (output.Length != 0) await Console.Out.WriteAsync(output);
            }
            await Console.Out.WriteLineAsync(result.ToString(CultureInfo.InvariantCulture));
            return 0;
        }
        catch (GuestDeferredStdoutException)
        {
            await Console.Error.WriteLineAsync("Oreslang deferred stdout denied or quota exceeded");
            return 70;
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
        catch (GuestSignal signal)
        {
            string effect = signal switch
            {
                GuestPanicSignal => "panic",
                GuestRaiseSignal => "raise",
                _ => "error"
            };
            await Console.Error.WriteLineAsync($"Oreslang {effect}: {signal.Error.Message}");
            return 70;
        }
    }
}
