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
                if (!failure.Message.Contains("NAMESPACE_RUNTIME_NATIVEAOT_UNSUPPORTED",
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
