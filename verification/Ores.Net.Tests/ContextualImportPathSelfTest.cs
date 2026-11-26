using System;
using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

// Executed by the existing compiler verification process, not a guest/AOT receipt.
internal static class ContextualImportPathSelfTest
{
    [ModuleInitializer]
    internal static void Run()
    {
        (string? Current, string Path, string Expected)[] accepted =
        {
            (null, "pkg.config", "config"),
            ("network.http", "self.utils", "network.http.utils"),
            ("network.http", "super.config", "network.config"),
            ("network.http", "super.super.common", "common"),
            (null, "pkg.a.b.c.d.e.f.g", "a.b.c.d.e.f.g"),
            ("network.http", "pkg.config", "config")
        };
        foreach (var c in accepted)
            if (ContextualImportPath.Normalize(c.Path,c.Current) != c.Expected)
                throw new InvalidOperationException("contextual import positive failed: " + c.Path);
        (string? Current, string Path)[] rejected =
        {
            ("network.", "super.config"),
            ("network.__atto_hidden", "super.config"),
            ("network.super", "super.config"),
            ("network.bad-name", "super.config"),
            ("a.b.c.d.e.f.g.h.i", "super.config"),
            ("network..http", "super.config"),
            ("network.http", "pkg"),
            ("network.http", "self"),
            ("network.http", "super"),
            ("network.http.client", "super.super"),
            (null, "pkg.shared"),
            ("network.shared", "super.config"),
            (null, "self.utils"),
            (null, "super.config"),
            ("network.http", "super.super.super.shared"),
            ("network.http", "self.super.config"),
            ("network.http", "pkg.a.b.c.d.e.f.g.h"),
            ("network.http", "pkg.é"),
            ("network.http", "pkg.config."),
            ("network.http", "pkg.__atto_reserved"),
            (null,"pkg." + new string('a',521))
        };
        foreach (var c in rejected)
        {
            bool denied = false;
            try { _ = ContextualImportPath.Normalize(c.Path,c.Current); }
            catch (FormatException e) { denied = e.Message.StartsWith("E_IMPORT_PATH", StringComparison.Ordinal); }
            if (!denied) throw new InvalidOperationException("invalid contextual import admitted: " + c.Path);
        }
        Console.WriteLine("Static import path: 6 positives and 21 denials; resolver only");
    }
}
