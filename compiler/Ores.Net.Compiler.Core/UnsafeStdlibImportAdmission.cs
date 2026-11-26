namespace Ores.Net.Compiler;

/// <summary>
/// G0 only: canonical and historical unsafe standard-library spellings are
/// permanently unavailable to guest source in every VM-isolate. No FFI
/// implementation or host-issued native grant is provided by this helper.
/// </summary>
public static class UnsafeStdlibImportAdmission
{
    public static bool IsReserved(string? specifier)
    {
        if (specifier is null) return false;
        var path = specifier.Replace('\\', '/');
        // Canonicalize stdlib components before matching reserved names.
        // The typed source linker must not see std/./unsafe as an ordinary
        // local package that it then normalizes into a native stdlib import.
        if (HasUnsafeSegments(path)) return true;
        string[] slash = path.Split('/');
        string[] head = slash[0].Split(new[] { '.', ':' }, StringSplitOptions.RemoveEmptyEntries);
        bool vendor = head.Length > 0 && (head[0] == "atto" || head[0] == "ores");
        int rootDepth = head.Length == 1 && head[0] == "std" ? 1 :
            vendor && head.Length == 2 && head[1] == "std" ? 1 :
            vendor && head.Length == 1 && slash.Length > 1 && slash[1] == "std" ? 2 : 0;
        if (rootDepth != 0)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..")
                {
                    if (parts.Count <= rootDepth) return true; // Reject std root escape.
                    parts.RemoveAt(parts.Count - 1);
                }
                else parts.Add(part);
            }
            path = string.Join("/", parts);
        }
        else if (path.Contains('/'))
        {
            // Relative source imports may be normalized by a linker after
            // admission: ./std/unsafe and pkg/../std/unsafe are still reserved.
            var parts = new System.Collections.Generic.List<string>();
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..")
                {
                    if (parts.Count != 0) parts.RemoveAt(parts.Count - 1);
                }
                else parts.Add(part);
            }
            path = string.Join("/", parts);
        }
        return HasUnsafeSegments(path);
    }

    private static bool HasUnsafeSegments(string path)
    {
        string[] segments = path.Split(new[] { '/', '.', ':' }, StringSplitOptions.RemoveEmptyEntries);
        int offset = segments.Length > 0 && (segments[0] == "atto" || segments[0] == "ores") ? 1 : 0;
        return segments.Length > offset + 1 &&
               segments[offset] == "std" && segments[offset + 1] == "unsafe";
    }

    public static void RequireGuestSafe(string specifier)
    {
        if (IsReserved(specifier))
            throw new FormatException(
                "unsafe standard-library import unavailable in VM-isolate and untrusted guest source");
    }
}
