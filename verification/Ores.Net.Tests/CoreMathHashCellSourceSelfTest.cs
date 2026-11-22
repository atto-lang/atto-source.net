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
                  // The new operations are policy only; loan issuance and
                  // owner identity must still be checked by the compiler.
                  if cell.valid_loan_state(1, true); do return 15; fi
                  if !cell.valid_loan_state(0, true); do return 16; fi
                  if !cell.can_owner_read(1, false); do return 17; fi
                  if cell.can_owner_read(0, true); do return 18; fi
                  if cell.can_owner_write(1, false); do return 19; fi
                  if !cell.can_owner_write(0, false); do return 20; fi
                  if cell.can_move_owner(0, true) || cell.can_move_owner(1, false); do return 21; fi
                  if !cell.can_move_owner(0, false); do return 22; fi
                  if cell.can_suspend_unpinned(1, false) || cell.can_suspend_unpinned(0, true); do return 23; fi
                  if !cell.can_suspend_unpinned(0, false); do return 24; fi
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
            // Canonical static namespace import spelling is explicitly
            // whitelisted for Core builtins and still compiles REAL CIL.
            File.WriteAllText(guest, """
                import * as m from "core.math";
                import * as h from "core.hash";
                import * as c from "core.cell";
                pub fnc main():int {
                  if m.min_int(3,9) != 3; do return 1; fi
                  if h.step_byte(17,42) != 569; do return 2; fi
                  if !c.can_borrow_read(3,false); do return 3; fi
                  if c.can_move_owner(3,false); do return 4; fi
                  if !c.can_suspend_unpinned(0,false); do return 5; fi
                  return 42;
                }
                """);
            var namespaced = OresSourceModuleCompiler.Compile(guest, new[] { root });
            if (namespaced.Sources.Count != 4 ||
                namespaced.Sources.Count(s => s.Path.StartsWith("@atto.core/",
                    StringComparison.Ordinal)) != 3)
                throw new InvalidOperationException("Canonical Core imports lost checked source identity");
            foreach (var unit in compiled.Sources.Where(s => s.Path.StartsWith("@atto.core/",
                StringComparison.Ordinal)))
            {
                var found = namespaced.Sources.SingleOrDefault(s => s.Path == unit.Path);
                if (found is null || found.Sha256 != unit.Sha256 || found.Utf8Bytes != unit.Utf8Bytes)
                    throw new InvalidOperationException("Canonical import drifted immutable Core bytes");
            }
            string namespaceDll = Path.Combine(root, "CoreNamespace.dll");
            CilEmitter.EmitLibrary(namespaced.Module, namespaceDll);
            using (var namespaceAssembly = AssemblyDefinition.ReadAssembly(namespaceDll))
            {
                var program = namespaceAssembly.MainModule.Types.Single(t => t.Name == "OresProgram");
                if (CfgCilTestInterpreter.Evaluate(program.Methods.Single(m => m.Name == "Run")) != 42)
                    throw new InvalidOperationException("Canonical namespace CIL guest did not execute");
            }
            // Two aliases of one canonical Core library must not double-link
            // the compiler-owned source, or permit a shadowed helper slot.
            File.WriteAllText(guest,
                "import * as a from \"core.math\"; import * as b from \"core.math\"; " +
                "pub fnc main():int{return a.signum_int(42)-b.signum_int(42)+42;}");
            var duplicateAliases = OresSourceModuleCompiler.Compile(guest, new[] { root });
            if (duplicateAliases.Sources.Count != 2 ||
                duplicateAliases.Sources.Count(s => s.Path == "@atto.core/math.atto") != 1)
                throw new InvalidOperationException("Core wildcard aliases duplicate source identities");
            string duplicateDll = Path.Combine(root, "CoreTwoAliases.dll");
            CilEmitter.EmitLibrary(duplicateAliases.Module, duplicateDll);
            using (var aliasAssembly = AssemblyDefinition.ReadAssembly(duplicateDll))
            {
                var program = aliasAssembly.MainModule.Types.Single(t => t.Name == "OresProgram");
                if (CfgCilTestInterpreter.Evaluate(program.Methods.Single(m => m.Name == "Run")) != 42)
                    throw new InvalidOperationException("Canonical Core alias calls returned different semantics");
            }
            // Wildcard form has no authority to search filesystem, packages,
            // network, or unsupported std/alloc namespace roots.
            foreach (string denied in new[] {
                "./math.atto", "std.fs", "alloc.heap", "core.missing",
                "core.math/../hash", "core.math.atto", "pkg.core.math"
            })
            {
                File.WriteAllText(guest,
                    "import * as x from \"" + denied + "\"; pub fnc main():int{return 42;}");
                try
                {
                    OresSourceModuleCompiler.Compile(guest, new[] { root });
                    throw new InvalidOperationException("Untrusted wildcard resolved: " + denied);
                }
                catch (FormatException failure)
                {
                    if (!failure.Message.Contains("E_CORE_NAMESPACE_IMPORT_AUTHORITY",
                        StringComparison.Ordinal)) throw;
                }
            }
            File.WriteAllText(guest,
                "import * as a from \"core.math\"; import * as a from \"core.hash\"; " +
                "pub fnc main():int{return 42;}");
            try
            {
                OresSourceModuleCompiler.Compile(guest, new[] { root });
                throw new InvalidOperationException("Duplicate Core namespace alias admitted");
            }
            catch (FormatException failure)
            {
                if (!failure.Message.Contains("Duplicate import binding",
                    StringComparison.Ordinal)) throw;
            }

            // Canonical .atto.pkg.toml is a real package boundary; a
            // manifest under an importer-selected root needs its own source
            // root authority before even reading the target source bytes.
            var child = Path.Combine(root, "nested");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "foreign.atto"),
                "define pub module foreign as pub fnc answer():int{return 42;} end");
            string nestedManifest = Path.Combine(child, ".atto.pkg.toml");
            File.WriteAllText(nestedManifest, "[package]\nname='foreign'\nversion='1.0.0'");
            File.WriteAllText(guest,
                "import module foreign from \"./nested/foreign.atto\"; " +
                "pub fnc main():int{return foreign.answer();}");
            try
            {
                OresSourceModuleCompiler.Compile(guest, new[] { root });
                throw new InvalidOperationException("Canonical nested package boundary bypassed");
            }
            catch (FormatException failure)
            {
                if (!failure.Message.Contains("Nested package requires an explicit dependency source root",
                    StringComparison.Ordinal)) throw;
            }
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(nestedManifest);
                File.CreateSymbolicLink(nestedManifest,
                    Path.Combine(root, "nonexistent-canonical-pkg.toml"));
                try
                {
                    OresSourceModuleCompiler.Compile(guest, new[] { root });
                    throw new InvalidOperationException("Dangling canonical manifest symlink bypassed boundary");
                }
                catch (FormatException failure)
                {
                    if (!failure.Message.Contains("Nested package requires an explicit dependency source root",
                        StringComparison.Ordinal)) throw;
                }
                File.Delete(nestedManifest);
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
            // The pinned Core Cell policy does NOT authorize ownership
            // operations. The production structured compiler must reject the
            // canonical bang and legacy rt syntax before CIL emission.
            string deniedCellDll = Path.Combine(root, "ForbiddenCoreCellLoan.dll");
            foreach (var denied in new (string expression, string witness)[] {
                ("borrow!owner", "OWNERSHIP_BANG_BACKEND_UNSUPPORTED"),
                ("take!owner", "OWNERSHIP_BANG_BACKEND_UNSUPPORTED"),
                ("share!owner", "OWNERSHIP_BANG_BACKEND_UNSUPPORTED"),
                ("copy!owner", "OWNERSHIP_BANG_BACKEND_UNSUPPORTED"),
                ("mut borrow!owner", "OWNERSHIP_BANG_BACKEND_UNSUPPORTED"),
                ("rt borrow owner", "Ownership operation 'rt borrow'"),
                ("rt mut borrow owner", "Ownership operation 'rt mut borrow'"),
                ("rt take owner", "Ownership operation 'rt take'")
            }) {
                File.WriteAllText(guest,
                    "import module cell from \"atto.core/cell\"; " +
                    "pub fnc main():int{val int owner=42;return " + denied.expression + ";}");
                try {
                    var invalidLoan = OresSourceModuleCompiler.Compile(guest, new[] { root });
                    // Never allow an unauthorized ownership expression to
                    // produce a supposedly validated CIL artifact.
                    CilEmitter.EmitLibrary(invalidLoan.Module, deniedCellDll);
                    throw new InvalidOperationException(
                        "Unproven Core Cell ownership operation compiled: " + denied.expression);
                }
                catch (FormatException failure) {
                    if (!failure.Message.Contains(denied.witness, StringComparison.Ordinal))
                        throw;
                }
                if (File.Exists(deniedCellDll))
                    throw new InvalidOperationException("Rejected Core Cell source emitted a CIL DLL");
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
