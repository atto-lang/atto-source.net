internal static class AsyncOnlyIoPolicyTests
{
    public static void Run()
    {
        // Match the complete Result member, not identifiers such as ResultValue.
        // This remains a conservative lexical policy, not a semantic C# proof.
        foreach (var allowed in new[] { "ticket.ResultValue", "entry.Results", "ticket.Result_Value" })
            if (ContainsForbidden(allowed, ".Result"))
                throw new Exception("FAILED: async-only policy rejected nonblocking result member: " + allowed);
        foreach (var denied in new[] { "task.Result", "task . Result", "task.@Result", "task.Result.ToString()", "task?.Result" })
            if (!ContainsForbidden(denied, ".Result"))
                throw new Exception("FAILED: async-only policy missed Result access: " + denied);

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
        if (forbidden == ".Result")
            return System.Text.RegularExpressions.Regex.IsMatch(source,
                @"\.\s*@?Result(?![\p{L}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}])",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return source.Contains(forbidden, StringComparison.Ordinal);
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
