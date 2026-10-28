using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

internal static class AsyncIoBoundarySelfTest
{
    private static readonly (Regex Pattern, string Why)[] Forbidden =
    {
        (new Regex(@"\bFile\.(?:ReadAllBytes|ReadAllText|ReadAllLines|WriteAllBytes|WriteAllText|WriteAllLines|AppendAllText|AppendAllLines|OpenRead|OpenWrite)\s*\(",
            RegexOptions.CultureInvariant), "synchronous System.IO.File data-plane API"),
        (new Regex(@"\bnew\s+FileStream\s*\(", RegexOptions.CultureInvariant),
            "FileStream construction requires an explicitly reviewed async provider"),
        (new Regex(@"\.(?:Receive|ReceiveFrom|Send|SendTo|Accept|Connect)\s*\(",
            RegexOptions.CultureInvariant), "synchronous socket data-plane API"),
        (new Regex(@"\bConsole\.(?:Read|ReadLine|Write|WriteLine)\s*\(",
            RegexOptions.CultureInvariant), "synchronous console I/O"),
        (new Regex(@"\bThread\.Sleep\s*\(", RegexOptions.CultureInvariant),
            "thread parking is not an async I/O implementation"),
        (new Regex(@"\.GetAwaiter\(\)\.GetResult\(\)", RegexOptions.CultureInvariant),
            "sync-over-async wait"),
        (new Regex(@"\bTask\.WaitAll\s*\(|\bTask\.WaitAny\s*\(",
            RegexOptions.CultureInvariant), "blocking task wait"),
        (new Regex(@"\bHttpClient\.Send\s*\(", RegexOptions.CultureInvariant),
            "synchronous HTTP transport"),
    };

    [ModuleInitializer]
    public static void Run()
    {
        // Socket syntax and actor-mailbox admission share the name Send.
        // Permit only the reviewed, statically typed ActorRuntime mailbox site;
        // keep every other synchronous transport spelling under the same rule.
        const string actor = "internal ActorMailbox Mailbox { get; } void Dispatch() { cell.Mailbox.Send(destination, data); }";
        const string socket = "Socket socket; socket.Send(data);";
        if (Forbidden.Any(r => r.Pattern.IsMatch(ReviewedMailboxCode(actor, "ActorRuntime.cs"))))
            throw new Exception("FAILED: bounded actor mailbox mistaken for socket I/O");
        if (!Forbidden.Any(r => r.Pattern.IsMatch(ReviewedMailboxCode(socket, "ActorRuntime.cs"))) ||
            !Forbidden.Any(r => r.Pattern.IsMatch(ReviewedMailboxCode(actor + socket, "ActorRuntime.cs"))) ||
            !Forbidden.Any(r => r.Pattern.IsMatch(ReviewedMailboxCode(
                actor.Replace("ActorMailbox", "Socket"), "ActorRuntime.cs"))) ||
            !Forbidden.Any(r => r.Pattern.IsMatch(ReviewedMailboxCode(actor, "OtherRuntime.cs"))))
            throw new Exception("FAILED: socket boundary exception became broader than reviewed mailbox site");
        string root = FindRepositoryRoot();
        string runtime = Path.Combine(root, "runtime", "Ores.Net.Runtime");
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
            code = ReviewedMailboxCode(code, Path.GetFileName(path));
            foreach (var rule in Forbidden)
            {
                if (rule.Pattern.IsMatch(code))
                    throw new Exception($"FAILED: {Path.GetRelativePath(root, path)} contains {rule.Why}");
            }
        }

        if (scanned == 0)
            throw new Exception("FAILED: async-I/O boundary audit scanned no .NET runtime sources");
    }

    private static string ReviewedMailboxCode(string code, string fileName)
    {
        if (fileName != "ActorRuntime.cs" ||
            !Regex.IsMatch(code, @"\binternal\s+ActorMailbox\s+Mailbox\s*\{\s*get\s*;\s*\}",
                RegexOptions.CultureInvariant) ||
            Regex.IsMatch(code, @"\bSocket\b|\bSystem\s*\.\s*Net\b", RegexOptions.CultureInvariant))
            return code;
        // No file-wide exemption: only this concrete bounded-memory call is
        // removed from this lexical socket audit. All other calls still scan.
        return Regex.Replace(code, @"\bcell\s*\.\s*Mailbox\s*\.\s*Send\s*\(",
            "VerifiedActorMailboxAdmission(", RegexOptions.CultureInvariant);
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
        throw new Exception("FAILED: could not locate .NET repository root for async-I/O audit");
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
                if (c == '\n')
                {
                    lineComment = false;
                    output.Append('\n');
                }
                continue;
            }
            if (blockComment)
            {
                if (c == '*' && next == '/')
                {
                    blockComment = false;
                    i++;
                }
                continue;
            }
            if (literal)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote) literal = false;
                else if (c == '\n' && quote != '\'')
                    throw new Exception("FAILED: malformed runtime source literal during async-I/O audit");
                continue;
            }

            if (c == '/' && next == '/')
            {
                lineComment = true;
                i++;
            }
            else if (c == '/' && next == '*')
            {
                blockComment = true;
                i++;
            }
            else if (c == '"' || c == '\'')
            {
                literal = true;
                quote = c;
            }
            else output.Append(c);
        }

        if (blockComment || literal)
            throw new Exception("FAILED: unterminated runtime comment/literal during async-I/O audit");
        return output.ToString();
    }
}
