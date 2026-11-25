using System;
using System.IO;
using Ores.Net.Compiler;

// Real source module compiler admission, not NativeAOT guest execution.
internal static class ContextualImportParserSelfTest
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "atto-contextual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string main = Path.Combine(root, "main.atto");
            const string body = " fnc main()->int { return m.min_int(42,43); }";
            File.WriteAllText(main, "import * as m from \"atto.core/math\";" + body);
            int legacyCount = OresSourceModuleCompiler.Compile(main, new[] { root }).Sources.Count;
            foreach (string selector in new[] { "core.math", "'core.math'", "\"core.math\"" })
            {
                File.WriteAllText(main, "import namespace m from " + selector + ";" + body);
                if (OresSourceModuleCompiler.Compile(main, new[] { root }).Sources.Count != legacyCount)
                    throw new InvalidOperationException("Namespace-first changed the builtin source closure");
            }
            foreach (var pair in new (string Source, string Diagnostic)[]
            {
                ("import self.utils;", "E_IMPORT_PATH"),
                ("import super.config;", "E_IMPORT_PATH"),
                ("import pkg;", "E_IMPORT_PATH"),
                ("import pkg.config;", "authenticated package-export graph"),
                ("import namespace m from './core.math';", "manifest-verified multi-file package linker"),
                ("import namespace m from 'core.unknown';", "manifest-verified multi-file package linker"),
                ("import namespace m from 'core/math';", "manifest-verified multi-file package linker"),
                ("import namespace m from core.", "manifest-verified multi-file package linker"),
                ("import namespace m from", "manifest-verified multi-file package linker"),
                ("import pkg.", "E_IMPORT_PATH"),
                ("define alias a = b; define alias b = a; fnc main()->int { return 42; }", "E_IMPORT_CYCLE"),
                ("define alias a as b; define alias b as a; fnc main()->int { return 42; }", "E_IMPORT_CYCLE"),
                ("define alias a = b.child; define alias a as b; fnc main()->int { return 42; }", "E_IMPORT_CYCLE"),
                ("import fnc f from './missing.atto'; define alias a = a; fnc main()->int { return 42; }", "E_IMPORT_CYCLE"),
                ("define alias a = b; define alias a = c; fnc main()->int { return 42; }", "E_DUPLICATE"),
                ("define alias a = core.math; fnc main()->int { return 42; }", "authenticated static binding integration"),
            })
            {
                File.WriteAllText(main, pair.Source);
                bool denied = false;
                try { _ = OresSourceModuleCompiler.Compile(main, new[] { root }); }
                catch (FormatException e)
                {
                    if (!e.Message.Contains(pair.Diagnostic, StringComparison.Ordinal)) throw;
                    denied = true;
                }
                if (!denied) throw new InvalidOperationException("Unverified import admitted: " + pair.Source);
            }
            Console.WriteLine("Contextual import production parser: legacy + 3 builtin spellings, 16 diagnosed denials");
        }
        finally { Directory.Delete(root, true); }
    }
}
