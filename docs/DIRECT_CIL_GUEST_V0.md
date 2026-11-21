# First direct Atto to ECMA-335 CIL guest compiler

Experimental v0, not full AttoLang backend parity.

Native .atto functions are parsed, typed for a bounded int64-only subset,
and emitted as ECMA-335 CIL metadata and bytecode using System.Reflection.Metadata.
No Atto-to-C# code generation or Python. A fixed, non-guest C# process entry
bridge links the generated CIL DLL into CoreCLR and Linux NativeAOT.

Accepted subset:
    pub fnc add_one(int value): int { return value + 1; }
    pub fnc main(): int { return add_one(41); }

Signed 64-bit integer parameters and literals; unary minus; parentheses;
checked +, -, *; integer /; typed function calls including forward references;
snake_case names; one return expression per function; : int and -> int returns.

All unsupported forms fail closed: booleans, floats, strings, bindings,
multiple statements, actors, ownership and borrowing, I/O, imports, async,
exceptions, recursion and dynamic interop. Limits: 64 KiB UTF-8 source,
8192 tokens, 64 functions, 8 args per function, 32 expression nesting levels.

On Linux with .NET 8 SDK and NativeAOT prerequisites execute:
    bash scripts/test-cil-guest.sh

This compiles real .atto to artifacts/AttoGuest.dll, executes positive guest
programs with exit code 42 on the managed CLR and Linux NativeAOT,
and confirms negatives produce typed diagnostics without output artifacts.

The source organization's GitHub Actions may fail before runner admission;
zero-step failures are not evidence of compiler failure. For private
alternate-org test carriers use exact Git-tree SHA and per-blob verification,
never a cross-org token, and require nonempty actual executed test steps.

Oreslang .NET architecture is consulted for compatibility but its compiler
and runtime results cannot be counted as AttoLang executable parity.
