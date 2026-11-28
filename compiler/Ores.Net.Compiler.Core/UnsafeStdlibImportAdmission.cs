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
        // Canonicalize incrementally, preserving unsafe intermediate identity.
        var prefix = new System.Collections.Generic.List<string>(3);
        var stack = new System.Collections.Generic.List<int>();
        int rootDepth = 0;
        foreach (string component in path.Split('/'))
        {
            if (component.Length == 0 || component == ".") continue;
            if (component == "..")
            {
                if (rootDepth != 0 && stack.Count <= rootDepth) return true;
                if (stack.Count != 0)
                {
                    int previous = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    prefix.RemoveRange(previous, prefix.Count - previous);
                }
                continue;
            }
            stack.Add(prefix.Count);
            foreach (string segment in component.Split(new[] { '.', ':' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (prefix.Count == 3) break;
                prefix.Add(segment);
            }
            int offset = prefix.Count > 0 && (prefix[0] == "atto" || prefix[0] == "ores") ? 1 : 0;
            if (prefix.Count > offset && prefix[offset] == "std")
            {
                if (rootDepth == 0) rootDepth = stack.Count;
                if (prefix.Count > offset + 1 && prefix[offset + 1] == "unsafe") return true;
            }
        }
        return false;
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
