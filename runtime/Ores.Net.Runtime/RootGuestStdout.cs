namespace Ores.Net.Runtime;

// Standalone synchronous entry only. Host-granted bounded buffer, never a
// process-global writer or actor capability. No guest allocation per append.
// Physical IO is asynchronous after the entry has returned or unwound.
public static class RootGuestStdout
{
    private const int Capacity = 16 * 1024;
    [ThreadStatic] private static bool active;
    [ThreadStatic] private static char[]? buffer;
    [ThreadStatic] private static int count;

    internal static void Begin(bool granted)
    {
        if (active) throw new RootGuestStdoutDeniedException();
        buffer = granted ? new char[Capacity] : null;
        count = 0;
        active = true;
    }

    public static bool Append(string text)
    {
        if (!active || buffer is null || text is null ||
            text.Length > 1024 || text.Length > Capacity - count)
            throw new RootGuestStdoutDeniedException();
        text.AsSpan().CopyTo(buffer.AsSpan(count));
        count += text.Length;
        return true;
    }

    internal static string? End()
    {
        char[]? current = buffer;
        int length = count;
        buffer = null; count = 0; active = false;
        if (current is null) return null;
        try { return length == 0 ? null : new string(current, 0, length); }
        finally { Array.Clear(current); }
    }
}

internal sealed class RootGuestStdoutDeniedException : Exception { }
