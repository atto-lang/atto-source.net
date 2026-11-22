using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Ores.Net.Compiler;

// Executed by the existing test assembly; emitted CIL is generated from .atto.
internal static class CoreMathHashCellSourceSelfTest
{
    [ModuleInitializer]
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "atto-core-three-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string guest = Path.Combine(root, "main.atto");
            File.WriteAllText(guest, """
                import module math from "atto.core/math";
                import module hash from "atto.core/hash";
                import module cell from "atto.core/cell";
                pub fnc main(): int {
                  if math.min_int(7, 2) != 2; do return 1; fi
                  if math.max_int(4, 9) != 9; do return 2; fi
                  if math.signum_int(-19) != -1; do return 3; fi
                  if math.signum_int(-9223372036854775808) != -1; do return 13; fi
                  if math.signum_int(9223372036854775807) != 1; do return 14; fi
                  if math.contains_closed_int(5, 10, 2); do return 4; fi
                  if hash.step_byte(17, 42) != 569; do return 5; fi
                  if hash.two_bytes(42, 7) != 17646; do return 6; fi
                  if hash.step_byte(17, 256) != -1; do return 7; fi
                  if hash.from_bool(true) != 528; do return 8; fi
                  if !cell.can_borrow_read(3, false); do return 9; fi
                  if cell.can_borrow_write(3, false); do return 10; fi
                  if cell.readers_after_acquire(65535, false) != -1; do return 11; fi
                  if cell.readers_after_release(1, false) != 0; do return 12; fi
                  return 42;
                }
                """);
            var compiled = OresSourceModuleCompiler.Compile(guest, new[] { root });
            if (compiled.Sources.Count != 4)
                throw new InvalidOperationException("Core must link exactly three canonical modules");
            foreach (var name in new[] { "math", "hash", "cell" })
                if (compiled.Sources.Count(x => x.Path == "@atto.core/" + name + ".atto") != 1)
                    throw new InvalidOperationException("Missing compiler-owned " + name + " source identity");
            string dll = Path.Combine(root, "CoreThree.dll");
            CilEmitter.EmitLibrary(compiled.Module, dll);
            using (var image = AssemblyDefinition.ReadAssembly(dll))
            {
                var program = image.MainModule.Types.Single(t => t.Name == "OresProgram");
                var run = program.Methods.Single(m => m.Name == "Run");
                if (CfgCilTestInterpreter.Evaluate(run) != 42)
                    throw new InvalidOperationException("Compiled .atto Core guest did not return 42");
            }
            // Canonical and legacy import spellings must resolve the SAME
            // authenticated pinned Core unit, not two executable copies.
            File.WriteAllText(guest,
                "import module math from \"ores.core/math\"; " +
                "pub fnc main():int{return math.signum_int(-1)+43;}");
            var legacy = OresSourceModuleCompiler.Compile(guest, new[] { root });
            if (legacy.Sources.Count != 2 ||
                legacy.Sources.Count(x => x.Path == "@atto.core/math.atto") != 1)
                throw new InvalidOperationException("Alias changed compiler-owned Core identity");
            string legacyDll = Path.Combine(root, "CoreLegacy.dll");
            CilEmitter.EmitLibrary(legacy.Module, legacyDll);
            using (var legacyAssembly = AssemblyDefinition.ReadAssembly(legacyDll))
            {
                var program = legacyAssembly.MainModule.Types.Single(t => t.Name == "OresProgram");
                if (CfgCilTestInterpreter.Evaluate(program.Methods.Single(m => m.Name == "Run")) != 42)
                    throw new InvalidOperationException("Compiled legacy Core import did not return 42");
            }
            // A source-relative import may not shadow a compiler-owned Core
            // namespace merely by declaring the same logical name.
            File.WriteAllText(Path.Combine(root, "math.atto"),
                "use namespace atto.core.math; pub fnc min_int(int a,int b):int{return a;}");
            File.WriteAllText(guest,
                "import module math from \"./math.atto\"; pub fnc main():int{return math.min_int(2,4);}");
            try
            {
                OresSourceModuleCompiler.Compile(guest, new[] { root });
                throw new InvalidOperationException("Namespace-spoofed project file admitted");
            }
            catch (FormatException failure)
            {
                if (!failure.Message.Contains("Core namespace requires authenticated",
                    StringComparison.Ordinal)) throw;
            }
            // Out-of-range signed i64 literals must not reach CIL emission.
            File.WriteAllText(guest,
                "import module math from \"atto.core/math\"; " +
                "pub fnc main():int{return math.signum_int(9223372036854775808);}");
            try
            {
                OresSourceModuleCompiler.Compile(guest, new[] { root });
                throw new InvalidOperationException("Out-of-range i64 literal admitted");
            }
            catch (FormatException)
            {
                // Any parser-specific checked literal diagnostic is acceptable;
                // never interpret it as a successful generated guest.
            }

            // A project-local namespace must never be able to impersonate a
            // compiler-owned Core module, regardless of its declared name.
            File.WriteAllText(guest,
                "use namespace atto.core.math; pub fnc main():int{return 42;}");
            try
            {
                OresSourceModuleCompiler.Compile(guest, new[] { root });
                throw new InvalidOperationException("Project-local Core namespace spoofed compiler authority");
            }
            catch (FormatException failure)
            {
                if (!failure.Message.Contains("Core namespace requires authenticated Core import provenance",
                    StringComparison.Ordinal)) throw;
            }
            foreach (string invalid in new[] { "atto.core/../hash", "atto.core/hash.atto.ores", "atto.core/missing" })
            {
                File.WriteAllText(guest, "import module hash from \"" + invalid + "\"; pub fnc main():int{return 42;}");
                try
                {
                    OresSourceModuleCompiler.Compile(guest, new[] { root });
                    throw new InvalidOperationException("Untrusted core specifier admitted: " + invalid);
                }
                catch (FormatException failure)
                {
                    if (!failure.Message.Contains("unknown compiler-owned atto.core", StringComparison.Ordinal))
                        throw;
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
