using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Ores.Net.Compiler;

internal sealed record SourceImport(string Kind, string Name, string Specifier, int Offset);
internal sealed record StructuredLinkedUnit(StructuredProgram Program, string? ModuleName,
    IReadOnlyDictionary<string, StructuredFunction> Exports);
internal sealed record LinkedModuleContext(string UnitIdentity, bool Entry,
    IReadOnlyDictionary<string, StructuredFunction> Bindings,
    IReadOnlyDictionary<string, StructuredFunction> Methods, IReadOnlyList<string> MethodOrder)
{
    internal string CanonicalName(string local) => "m_" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(UnitIdentity + "\0" + local)));
}

public sealed record SourceUnitIdentity(string Path, string Sha256, int Utf8Bytes);

/// <summary>Verified static CIL module and identities of the exact admitted bytes.</summary>
public sealed class SourceModuleCompilation
{
    internal SourceModuleCompilation(VerifiedCfgModule module, IReadOnlyList<SourceUnitIdentity> sources,
        string graphSha256)
    { Module = module; Sources = sources; GraphSha256 = graphSha256; }
    public VerifiedCfgModule Module { get; }
    public IReadOnlyList<SourceUnitIdentity> Sources { get; }
    public string GraphSha256 { get; }
}

/// <summary>
/// Build-time local module graph and typed AST linker. Files are parsed in
/// their original scopes; no Oreslang source concatenation or rewriting occurs.
/// This first executable slice admits the existing checked int/bool function ABI.
/// </summary>
public static class OresSourceModuleCompiler
{
    private const int MaxUnits = 64, MaxDepth = 32, MaxFileBytes = 1_048_576;
    private const int MaxGraphBytes = 8 * MaxFileBytes;
    // Closed compiler-owned v1 stdlib registry. Do not use the caller's
    // source roots, working directory, PATH, classpath or CLR type identity
    // to resolve a privileged Oreslang library import.
    private const string CanonicalNumericKey = "@ores.core/numeric.ores";
    private const string CanonicalNumericSpecifier = "ores.core/numeric";
    private const string CanonicalNumericResource = "Ores.Net.Compiler.Core.CanonicalNumeric.ores";
    private const string CanonicalNumericSha256 =
        "3d1d7b77068dd02b3d60b30b72f29a4537a933134ca79e1e81df096dc56f5f33";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private sealed record Unit(string PhysicalPath, string Identity, string Source,
        SourceUnitIdentity Witness, IReadOnlyList<SourceImport> Imports);

    public static SourceModuleCompilation Compile(string entrypoint, IReadOnlyList<string> sourceRoots)
    {
        ArgumentNullException.ThrowIfNull(entrypoint);
        ArgumentNullException.ThrowIfNull(sourceRoots);
        if (sourceRoots.Count is < 1 or > 16) throw new FormatException("Explicit source roots required (maximum 16)");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = sourceRoots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).ToArray();
        if (roots.Distinct(comparer).Count() != roots.Length) throw new FormatException("Duplicate source root");
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) throw new FormatException("Source root does not exist");
            // The final root directory may be real while an ancestor is a
            // symlink/junction. Admission must walk the entire supplied path.
            RejectReparseAncestors(root);
        }
        var units = new Dictionary<string, Unit>(comparer);
        var states = new Dictionary<string, int>(comparer);
        // A single graph must be portable between case-sensitive and
        // case-insensitive target filesystems; fail closed on aliasing.
        var portableIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<Unit>();
        int totalBytes = 0;
        string entry = AdmitPath(Path.GetFullPath(entrypoint));
        Visit(entry, 0);

        var methods = new Dictionary<string, StructuredFunction>(StringComparer.Ordinal);
        var order = new List<string>();
        var parsed = new Dictionary<string, StructuredLinkedUnit>(comparer);
        StructuredProgram? rootProgram = null;
        foreach (var unit in ordered)
        {
            var bindings = new Dictionary<string, StructuredFunction>(StringComparer.Ordinal);
            var importedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var import in unit.Imports)
            {
                if (!importedNames.Add(import.Name)) throw new FormatException($"Duplicate import binding '{import.Name}' in {unit.Identity}");
                string targetPath = Resolve(unit.PhysicalPath, import.Specifier);
                var target = parsed[targetPath];
                if (import.Kind == "fnc")
                {
                    if (!target.Exports.TryGetValue(import.Name, out var declaration))
                        throw new FormatException($"Import '{import.Name}' does not name a public function in {units[targetPath].Identity}");
                    if (!bindings.TryAdd(import.Name, declaration)) throw new FormatException("Duplicate imported callable");
                }
                else
                {
                    if (target.ModuleName != import.Name)
                        throw new FormatException($"Module import '{import.Name}' does not match declared module in {units[targetPath].Identity}");
                    foreach (var exported in target.Exports)
                        if (!bindings.TryAdd(import.Name + "." + exported.Key, exported.Value))
                            throw new FormatException("Duplicate qualified imported callable");
                }
            }
            StructuredLinkedUnit linked;
            try
            {
                linked = OresStructuredParser.ParseLinked(unit.Source,
                    new LinkedModuleContext(unit.Identity, comparer.Equals(unit.PhysicalPath, entry),
                        bindings, methods, order));
            }
            catch (FormatException failure)
            { throw new FormatException($"{unit.Identity}: {failure.Message}", failure); }
            parsed.Add(unit.PhysicalPath, linked);
            methods = new Dictionary<string, StructuredFunction>(linked.Program.Functions!, StringComparer.Ordinal);
            order = new List<string>(linked.Program.FunctionOrder!);
            if (comparer.Equals(unit.PhysicalPath, entry)) rootProgram = linked.Program;
        }
        var witnesses = ordered.Select(u => u.Witness).OrderBy(w => w.Path, StringComparer.Ordinal).ToArray();
        var graphText = string.Concat(witnesses.Select(w => w.Path + "\0" + w.Sha256 + "\n"));
        return new SourceModuleCompilation(OresStructuredCfgLowering.LowerModule(rootProgram!),
            Array.AsReadOnly(witnesses), Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(graphText))));

        void Visit(string path, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("Source graph depth exceeds 32");
            if (states.TryGetValue(path, out int state))
            {
                if (state == 1) throw new FormatException("Cyclic typed module linking requires recursive ABI lowering");
                return;
            }
            if (units.Count >= MaxUnits) throw new FormatException("Source graph exceeds 64 units");
            string identity = Identity(path);
            if (!portableIdentities.Add(identity))
                throw new FormatException("Case-insensitive source identity collision");
            byte[] bytes = ReadBounded(path);
            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > MaxGraphBytes) throw new FormatException("Source graph exceeds 8 MiB");
            string source;
            try { source = Utf8.GetString(bytes); }
            catch (DecoderFallbackException failure) { throw new FormatException("Source is not strict UTF-8", failure); }
            if (source.Contains('\0') || source.StartsWith('\uFEFF')) throw new FormatException("Source NUL/BOM is not admitted");
            var unit = new Unit(path, identity, source,
                new SourceUnitIdentity(identity, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length),
                OresStructuredParser.ReadLinkedImports(source));
            units.Add(path, unit);
            states.Add(path, 1);
            foreach (var import in unit.Imports) Visit(Resolve(path, import.Specifier), depth + 1);
            states[path] = 2;
            ordered.Add(unit);
        }

        string Resolve(string importer, string specifier)
        {
            // The import name itself is a closed canonical key. No ambient
            // source path may shadow it, even with a matching module name.
            if (specifier == CanonicalNumericSpecifier) return CanonicalNumericKey;
            if (specifier.StartsWith("ores.", StringComparison.Ordinal))
                throw new FormatException("Unknown compiler-owned Oreslang library module");
            if (importer == CanonicalNumericKey)
                throw new FormatException("Canonical core cannot import project or hosted modules");
            if (!(specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal)) ||
                specifier.Length > 4096 || specifier.EndsWith('/') || specifier.Contains("//", StringComparison.Ordinal) ||
                specifier.Any(c => char.IsControl(c) || c is '\\' or ':'))
                throw new FormatException("Module imports require explicit local ./ or ../ source paths");
            string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(importer)!, specifier));
            if (Path.GetExtension(path).Length == 0) path += ".ores";
            return AdmitPath(path);
        }

        string AdmitPath(string path)
        {
            if (Path.GetExtension(path) != ".ores" || !File.Exists(path)) throw new FormatException("Source import is missing or not .ores");
            int owner = RootIndex(path);
            if (owner < 0) throw new FormatException("Source import escapes explicit source roots");
            string cursor = roots[owner];
            foreach (string component in Path.GetRelativePath(cursor, path).Split(Path.DirectorySeparatorChar))
            {
                // On Unix, literal backslash and control characters can be
                // valid filenames. They cannot enter a graph identity that
                // normalizes backslash to '/' and frames witnesses with LF.
                if (component.Any(c => char.IsControl(c) ||
                    (Path.DirectorySeparatorChar == '/' && c == '\\')))
                    throw new FormatException("Unsupported source path identity character");
                cursor = Path.Combine(cursor, component);
                RejectReparse(cursor);
                if (Directory.Exists(cursor) &&
                    (ProjectRootSourceResolver.MarkerExistsNoFollow(Path.Combine(cursor, ".ores.pkg.toml")) ||
                     ProjectRootSourceResolver.MarkerExistsNoFollow(Path.Combine(cursor, ".oreslangc.cfg.toml"))))
                    throw new FormatException("Nested package requires an explicit dependency source root");
            }
            return path;
        }

        int RootIndex(string path)
        {
            int selected = -1;
            for (int i = 0; i < roots.Length; i++)
            {
                string relative = Path.GetRelativePath(roots[i], path);
                if (relative == "." || relative == ".." || Path.IsPathRooted(relative) ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                if (selected < 0 || roots[i].Length > roots[selected].Length) selected = i;
            }
            return selected;
        }

        string Identity(string path)
        {
            if (path == CanonicalNumericKey) return CanonicalNumericKey;
            int owner = RootIndex(path);
            return owner.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                Path.GetRelativePath(roots[owner], path).Replace('\\', '/');
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("Symlink/reparse source paths are not admitted");
    }

    private static void RejectReparseAncestors(string path)
    {
        // Reject an alias in any ancestor of the configured source root, not
        // only a reparse point on the root itself. Never resolve a symlink
        // and silently replace the caller's declared authority boundary.
        for (string? cursor = Path.GetFullPath(path); cursor is not null;
             cursor = Path.GetDirectoryName(cursor))
            RejectReparse(cursor);
    }

    private static byte[] ReadBounded(string path)
    {
        // Load the real .ores source embedded at build time into the AOT
        // compiler. The runtime never executes a C# replacement of this API.
        // Exact hash admission catches package/resource mismatch before any
        // parse, typing or CIL emission, including during Native AOT use.
        using Stream stream = path == CanonicalNumericKey
            ? typeof(OresSourceModuleCompiler).Assembly.GetManifestResourceStream(CanonicalNumericResource)
                ?? throw new FormatException("Pinned canonical Oreslang library resource is missing")
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[MaxFileBytes + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxFileBytes) throw new FormatException("Source exceeds 1 MiB UTF-8 parser limit");
        byte[] source = bytes.AsSpan(0, count).ToArray();
        if (path == CanonicalNumericKey &&
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(source)),
                CanonicalNumericSha256, StringComparison.Ordinal))
            throw new FormatException("Pinned canonical Oreslang core source digest mismatch");
        return source;
    }
}
