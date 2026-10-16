using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Bounded package-root resolution substrate. The bootstrap parser still rejects
/// imports: this does not implement graph identity, CIL linking, or parity.
/// </summary>
public static class ProjectRootSourceResolver
{
    public static string Resolve(string packageDirectory, string specifier)
    {
        ArgumentNullException.ThrowIfNull(packageDirectory);
        ArgumentNullException.ThrowIfNull(specifier);
        if (!specifier.StartsWith("+/", StringComparison.Ordinal))
            throw new FormatException("expected canonical +/ source import");
        if (Encoding.UTF8.GetByteCount(specifier) > 4096)
            throw new FormatException("rooted import length exceeds 4096 bytes");
        var suffix = specifier.AsSpan(2);
        if (suffix.IsEmpty)
            throw new FormatException("empty project-root import");
        foreach (var c in suffix)
            if (char.IsControl(c) || c == '\\' || c == ':')
                throw new FormatException("invalid root import separator or control byte");
        var segments = specifier[2..].Split('/');
        foreach (var part in segments)
            if (part.Length == 0 || part is "." or "..")
                throw new FormatException("noncanonical project-root import component");

        var root = Path.GetFullPath(packageDirectory);
        var manifest = Path.Combine(root, ".ores.pkg.toml");
        if (!File.Exists(manifest))
            throw new FormatException("verified project manifest required");
        CheckNotReparsePoint(manifest);
        RequireV1Manifest(manifest);

        // Reject every symlink/reparse component inside the chosen package.
        // This is deliberately stricter than the Java resolver until a
        // descriptor-relative, race-safe identity mechanism exists.
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
                CheckNotReparsePoint(current);
        }
        var candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var relative = Path.GetRelativePath(root, candidate);
        if (relative is ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new FormatException("project-root source escapes package");
        if (Path.GetExtension(candidate).Length == 0) candidate += ".ores";
        if (Path.GetExtension(candidate) != ".ores")
            throw new FormatException("non-Oreslang sources unsupported in .NET bootstrap");
        if (!File.Exists(candidate))
            throw new FormatException("project-root source not found");
        CheckNotReparsePoint(candidate);
        return candidate;
    }

    // Restricted bootstrap marker check, not a full TOML parser.
    // Read through one opened descriptor with a strict byte budget. This does
    // not make path lookup or reparse-point checks race-free.
    private static void RequireV1Manifest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[65_537];
        int length = 0;
        while (length < bytes.Length)
        {
            int read = stream.Read(bytes, length, bytes.Length - length);
            if (read == 0) break;
            length += read;
        }
        if (length > 65_536)
            throw new FormatException("project manifest exceeds 64 KiB");
        string contents;
        try { contents = new UTF8Encoding(false, true).GetString(bytes, 0, length); }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("project manifest must be valid UTF-8", ex);
        }
        if (contents.Contains('\0') || contents.Contains("\"\"\"", StringComparison.Ordinal) ||
            contents.Contains("'''", StringComparison.Ordinal))
            throw new FormatException("unsupported project manifest literal");
        bool inRoot = true;
        int versionCount = 0;
        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart(' ', '\t');
            if (line.StartsWith("[", StringComparison.Ordinal))
                inRoot = false;
            if (!inRoot) continue;
            int equals = line.IndexOf('=');
            if (equals < 0) continue;
            var key = line[..equals].TrimEnd(' ', '\t');
            if (key is not ("schema_version" or "\"schema_version\"" or "'schema_version'"))
                continue;
            var value = line[(equals + 1)..].TrimStart(' ', '\t');
            if (!value.StartsWith("\"1\"", StringComparison.Ordinal) ||
                (value.Length > 3 && !string.IsNullOrEmpty(value[3..].Trim()) &&
                 !value[3..].TrimStart().StartsWith("#", StringComparison.Ordinal)))
                throw new FormatException("project manifest requires schema_version = 1");
            versionCount++;
        }
        if (versionCount != 1)
            throw new FormatException("project manifest requires exactly one schema_version = 1");
    }

    private static void CheckNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("project-root source symlink/reparse point denied");
    }
}
