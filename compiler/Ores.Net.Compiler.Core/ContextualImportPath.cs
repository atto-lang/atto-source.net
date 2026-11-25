using System;
using System.Collections.Generic;

namespace Ores.Net.Compiler;

/// <summary>Compiler-side logical namespace normalization; does not confer export or package authority.</summary>
public static class ContextualImportPath
{
    private const int MaxComponents = 8;
    private const int MaxPath = MaxComponents * 65;
    // Same bounded import-v2 vocabulary as Rust's static graph, not runtime reflection.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "abstract", "actor", "arr", "as", "async", "await", "begin", "block", "break", "catch", "cb", "class", "conforms", "const", "continue", "contract", "cpu", "declare", "def", "default", "defer", "define", "do", "done", "durable", "elif", "else", "elseif", "end", "enum", "eq", "extends", "false", "fi", "finally", "fnc", "for", "from", "generator", "gpu", "guard", "if", "impl", "implements", "import", "interface", "is", "isoactor", "let", "loop", "match", "matches", "maybe", "module", "mtd", "mut", "namespace", "nb", "neq", "new", "nlex", "null", "obj", "of", "over", "panic", "persistent", "pipe", "pkg", "private", "pub", "pure", "quantum", "raise", "readch", "recover", "return", "routine", "rpipe", "rt", "select", "self", "shared", "spawn", "static", "stop", "struct", "super", "suspends", "switch", "sync", "task", "then", "throw", "trait", "trap", "true", "try", "type", "typeof", "types", "unsafe", "untrusted", "use", "using", "val", "void", "while", "writech", "yield" };

    public static string Normalize(string path, string? currentNamespace)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPath) throw Invalid();
        string[] parts = path.Split('.');
        if (parts.Length < 2 || parts.Length > MaxComponents) throw Invalid();
        var result = new List<string>(MaxComponents);
        string root = parts[0];
        if (root != "pkg")
        {
            if (root != "self" && root != "super") throw Invalid();
            if (string.IsNullOrEmpty(currentNamespace) || currentNamespace.Length > MaxPath) throw Invalid();
            string[] current = currentNamespace.Split('.');
            if (current.Length > MaxComponents) throw Invalid();
            // Check complete namespace before removal of parent components.
            foreach (string part in current)
            {
                if (!Valid(part)) throw Invalid();
                result.Add(part);
            }
        }
        int offset = 1;
        if (root == "super")
        {
            int parents = 1;
            while (offset < parts.Length && parts[offset] == "super") { parents++; offset++; }
            if (parents > result.Count || offset == parts.Length) throw Invalid();
            result.RemoveRange(result.Count - parents, parents);
        }
        if (result.Count + parts.Length - offset > MaxComponents) throw Invalid();
        for (; offset < parts.Length; offset++)
        {
            if (!Valid(parts[offset])) throw Invalid();
            result.Add(parts[offset]);
        }
        return string.Join(".", result);
    }

    private static bool Alpha(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    internal static bool Valid(string p)
    {
        if (p.Length is < 1 or > 64 || !(Alpha(p[0]) || p[0] == '_')
            || p.StartsWith("__atto_", StringComparison.Ordinal) || Reserved.Contains(p)) return false;
        foreach (char c in p)
            if (!(Alpha(c) || c == '_' || c is >= '0' and <= '9')) return false;
        return true;
    }
    private static FormatException Invalid() => new("E_IMPORT_PATH: invalid contextual namespace path");
}
