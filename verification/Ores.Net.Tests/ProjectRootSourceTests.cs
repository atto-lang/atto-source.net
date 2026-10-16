using Ores.Net.Compiler;

internal static class ProjectRootSourceTests
{
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new Exception("project-root import contract: " + reason);
    }

    private static void Reject(Action operation, string reason)
    {
        try { operation(); }
        catch (FormatException) { return; }
        throw new Exception("unsafe project-root source admitted: " + reason);
    }

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ores-net-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            string app = Path.Combine(root, "app");
            string source = Path.Combine(app, "src", "answer.ores");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "pub fnc answer():int{return 42;}\n");
            Reject(() => ProjectRootSourceResolver.Resolve(app, "+/src/answer"),
                   "package root without manifest");
            File.WriteAllText(Path.Combine(app, ".ores.pkg.toml"), "schema_version = \"1\"\n");
            Check(ProjectRootSourceResolver.Resolve(app, "+/src/answer") == Path.GetFullPath(source),
                  "inferred extension");
            Check(ProjectRootSourceResolver.Resolve(app, "+/src/answer.ores") ==
                  Path.GetFullPath(source), "exact extension");
            foreach (var bad in new[] {
                "", "+", "+src/answer", "@/", "@/src/answer", "+/", "+//src/answer",
                "+/src//answer", "+/src/./answer", "+/src/../src/answer",
                "+/src/answer/", "+/src\\answer", "+/src:answer",
                "+/missing", "+/src/answer.java", "+/src/answer\0evil"
            })
                Reject(() => ProjectRootSourceResolver.Resolve(app, bad), bad);
            Reject(() => ProjectRootSourceResolver.Resolve(app, "+/" + new string('x', 4096)),
                   "oversized rooted import");

            // Symlinks and Windows reparse points are not silently admitted.
            var outsider = Path.Combine(root, "external.ores");
            File.WriteAllText(outsider, "pub fnc external():int{return 1;}\n");
            var alias = Path.Combine(app, "src", "external.ores");
            try
            {
                File.CreateSymbolicLink(alias, outsider);
                Reject(() => ProjectRootSourceResolver.Resolve(app, "+/src/external"),
                       "foreign source symlink");
            }
            catch (UnauthorizedAccessException) { /* Windows without symlink privilege */ }
            catch (PlatformNotSupportedException) { /* symlink-unavailable host */ }
            catch (IOException) { /* filesystem does not permit symlink creation */ }
            // A sibling file cannot be smuggled via a rooted source prefix.
            var sibling = Path.Combine(root, "other", "answer.ores");
            Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
            File.WriteAllText(sibling, "pub fnc answer():int{return 0;}\n");
            Reject(() => ProjectRootSourceResolver.Resolve(app, "+/../other/answer"),
                   "sibling package crossing");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
