using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

internal static class BlockingWaitBoundarySelfTest
{
    private static readonly Regex[] Forbidden =
    {
        new(@"\.Result\b", RegexOptions.CultureInvariant),
        new(@"\.Wait\s*\(", RegexOptions.CultureInvariant),
        new(@"\bTask\.(?:WaitAll|WaitAny)\s*\(", RegexOptions.CultureInvariant),
        new(@"\bThread\.Join\s*\(", RegexOptions.CultureInvariant),
        new(@"\.WaitOne\s*\(", RegexOptions.CultureInvariant),
        new(@"\bMonitor\.Wait\s*\(", RegexOptions.CultureInvariant),
        new(@"\bSocket\.Poll\s*\(|\.Poll\s*\([^\n;]*SelectMode\.", RegexOptions.CultureInvariant),
        new(@"\bDns\.(?:GetHostAddresses|GetHostEntry|GetHostName)\s*\(", RegexOptions.CultureInvariant),
        new(@"\bProcess\.WaitForExit\s*\(", RegexOptions.CultureInvariant),
    };

    [ModuleInitializer]
    public static void Run()
    {
        string root = FindRepositoryRoot();
        string runtime = Path.Combine(root, "runtime");
        int scanned = 0;
        foreach (string path in Directory.EnumerateFiles(runtime, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) ||
                path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                continue;

            scanned++;
            string code = StripCommentsAndLiterals(File.ReadAllText(path, Encoding.UTF8));
            foreach (Regex rule in Forbidden)
            {
                if (rule.IsMatch(code))
                    throw new Exception(
                        $"FAILED: {Path.GetRelativePath(root, path)} contains a blocking wait/DNS primitive forbidden at the async-only runtime boundary: {rule}");
            }
        }

        if (scanned == 0)
            throw new Exception("FAILED: blocking-wait async-I/O audit scanned no .NET runtime sources");

        string allowed = StripCommentsAndLiterals(
            "await stream.ReadAsync(buffer, token); await socket.ConnectAsync(endpoint, token); await task.WaitAsync(token);");
        if (Forbidden.Any(rule => rule.IsMatch(allowed)))
            throw new Exception("FAILED: blocking-wait audit rejected async-only sample");
    }

    private static string FindRepositoryRoot()
    {
        foreach (string origin in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(Path.GetFullPath(origin));
            for (int depth = 0; dir is not null && depth < 16; depth++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "runtime", "Ores.Net.Runtime",
                        "Ores.Net.Runtime.csproj")))
                    return dir.FullName;
            }
        }
        throw new Exception("FAILED: could not locate .NET repository root for blocking-wait audit");
    }

    private static string StripCommentsAndLiterals(string source)
    {
        var output = new StringBuilder(source.Length);
        bool lineComment = false, blockComment = false, literal = false, escaped = false;
        char quote = '\0';
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (lineComment)
            {
                if (c == '\n') { lineComment = false; output.Append('\n'); }
                continue;
            }
            if (blockComment)
            {
                if (c == '*' && next == '/') { blockComment = false; i++; }
                continue;
            }
            if (literal)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote) literal = false;
                continue;
            }
            if (c == '/' && next == '/') { lineComment = true; i++; }
            else if (c == '/' && next == '*') { blockComment = true; i++; }
            else if (c == '"' || c == '\'') { literal = true; quote = c; }
            else output.Append(c);
        }
        if (blockComment || literal)
            throw new Exception("FAILED: unterminated runtime comment/literal during blocking-wait audit");
        return output.ToString();
    }
}
