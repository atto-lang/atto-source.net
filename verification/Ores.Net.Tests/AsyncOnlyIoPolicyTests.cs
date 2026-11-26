internal static class AsyncOnlyIoPolicyTests
{
    public static void Run()
    {
        // A prefix match must not confuse a scalar mailbox ResultValue
        // field with Task<T>.Result. Preserve all other conservative checks.
        foreach (var blocked in new[] { "task.Result", "task.Result;", "task.Result.ToString()", "task.Result /* comment */" })
            if (!ContainsForbidden(blocked, ".Result"))
                throw new Exception("Async-only Result control missed: " + blocked);
        foreach (var allowed in new[] { "ticket.ResultValue", "ticket.ResultValue = 42", "ticket.Result_1" })
            if (ContainsForbidden(allowed, ".Result"))
                throw new Exception("Async-only Result prefix false positive: " + allowed);
        var root = FindRepositoryRoot();
        foreach (var relative in new[] { "runtime", "host" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, relative), "*.cs",
                         SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(path);
                foreach (var forbidden in Forbidden)
                {
                    if (ContainsForbidden(source, forbidden))
                        throw new Exception($"FAILED: blocking runtime/host I/O token '{forbidden}' in {Path.GetRelativePath(root, path)}");
                }
            }
        }
    }

    private static bool ContainsForbidden(string source, string forbidden)
    {
        if (forbidden != ".Result")
            return source.Contains(forbidden, StringComparison.Ordinal);
        // Match the complete member identifier, including escaped or Unicode
        // spellings conservatively: only ordinary ASCII identifier suffixes
        // can establish that this literal token is a different name.
        int start = 0;
        while (true)
        {
            int match = source.IndexOf(forbidden, start, StringComparison.Ordinal);
            if (match < 0) return false;
            int end = match + forbidden.Length;
            if (end == source.Length ||
                !(char.IsAsciiLetterOrDigit(source[end]) || source[end] == '_'))
                return true;
            start = end;
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName,
                    "runtime", "Ores.Net.Runtime", "RuntimeHost.cs")))
                return current.FullName;
            current = current.Parent;
        }
        throw new Exception("FAILED: could not locate repository root for async-only I/O policy");
    }

    private static readonly string[] Forbidden =
    {
        "Console.Write(",
        "Console.WriteLine(",
        "Console.Out.Write(",
        "Console.Out.WriteLine(",
        "Console.Error.Write(",
        "Console.Error.WriteLine(",
        "Console.Read(",
        "Console.ReadLine(",
        "Console.In.Read(",
        "Console.In.ReadLine(",
        "File.ReadAll",
        "File.WriteAll",
        "File.AppendAll",
        "File.Open(",
        "File.OpenRead(",
        "File.OpenWrite(",
        "Socket.Connect(",
        "Socket.Accept(",
        "Socket.Receive(",
        "Socket.Send(",
        "HttpClient.Send(",
        ".Result",
        ".Wait(",
        "GetAwaiter().GetResult()",
        "Thread.Sleep(",
        "Task.Run("
    };
}
