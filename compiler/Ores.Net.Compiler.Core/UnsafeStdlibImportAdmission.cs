namespace Ores.Net.Compiler;

/// <summary>
/// G0 only: canonical and historical unsafe standard-library spellings are
/// permanently unavailable to guest source in every VM-isolate. No FFI
/// implementation or host-issued native grant is provided by this helper.
/// </summary>
public static class UnsafeStdlibImportAdmission
{
    private static readonly string[] ReservedRoots =
    {
        "ores.std/unsafe", "std/unsafe",
        "ores.std.unsafe", "std.unsafe",
        "ores.std:unsafe", "std:unsafe"
    };

    public static bool IsReserved(string? specifier)
    {
        if (specifier is null) return false;
        var path = specifier.Replace('\\', '/');
        foreach (string root in ReservedRoots)
        {
            if (path == root || path.StartsWith(root + "/", StringComparison.Ordinal)
                             || path.StartsWith(root + ".", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static void RequireGuestSafe(string specifier)
    {
        if (IsReserved(specifier))
            throw new FormatException(
                "unsafe standard-library import unavailable in VM-isolate and untrusted guest source");
    }
}
