internal static class AsyncOnlyIoPolicyTests
{
    public static void Run()
    {
        var root = FindRepositoryRoot();
        foreach (var relative in new[] { "runtime", "host" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, relative), "*.cs",
                         SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(path);
                foreach (var forbidden in Forbidden)
                {
                    if (source.Contains(forbidden, StringComparison.Ordinal))
                        throw new Exception($"FAILED: blocking runtime/host I/O token '{forbidden}' in {Path.GetRelativePath(root, path)}");
                }
            }
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
