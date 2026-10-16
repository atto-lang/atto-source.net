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

    private static void CheckNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("project-root source symlink/reparse point denied");
    }
}
