using System.Text;

namespace Ores.Net.Runtime;

// Restricted root-only output capture. No guest Console access, ambient
// environment grant, cross-thread authority or actor IO. The host flushes
// asynchronously after the synchronous invocation. This is not live stdout.
internal sealed class GuestDeferredStdoutException : Exception { }

public static class GuestDeferredStdout
{
    [ThreadStatic] private static bool active;
    [ThreadStatic] private static StringBuilder? output;
    private const int Capacity = 4096;

    internal static long Execute(Func<long> entry, bool granted, out string captured)
    {
        captured = "";
        if (active) throw new GuestDeferredStdoutException();
        active = true;
        try
        {
            output = granted ? new StringBuilder(Capacity, Capacity) : null;
            return entry();
        }
        finally
        {
            try { captured = output?.ToString() ?? ""; }
            finally { output = null; active = false; }
        }
    }

    public static bool WriteLiteral(string value)
    {
        if (!active || output is null || value is null || value.Length > 256 ||
            value.Length > Capacity - output.Length)
            throw new GuestDeferredStdoutException();
        foreach (char ch in value)
            if (ch < ' ' || ch > '~') throw new GuestDeferredStdoutException();
        output.Append(value);
        return true;
    }
}
