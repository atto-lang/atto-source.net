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
snake_case names; : int and -> int returns; bounded sequential statements:
up to 63 `stdio.println(int)` statements followed by one final `return` or
`^` statement. Guarded `return match` and `^ match` support literal,
binding and wildcard patterns and boolean comparison guards. A `match`
subject is evaluated once and each arm binding is lexical to its guard/body.

Only trusted process-local CLI sessions may bind `stdio.println(int)`
to `System.Console.WriteLine(long)`: this requires the explicit
`--allow-stdio` compiler argument. Without that argument the compiler
rejects output with `E_PERMISSION` and emits no DLL. This flag is a
**host authorization request, not a sandbox boundary**: a multi-tenant
host must never permit untrusted tenant-supplied flags to grant ambient
process stdout. Untrusted TenantVM actors must instead use a host-owned,
capability-checked channel and have no direct Console reference.

Other unsupported forms fail closed: arbitrary host I/O, booleans as
values, floats, strings, local bindings, actors, ownership/borrowing,
imports, async, exceptions, recursion, dynamic interop, and more complex
function statements. Limits: 64 KiB UTF-8 source, 8192 tokens, 64
functions, 64 function statements, 8 args per function, 32 expression
nesting levels and a bounded 16-level match nesting profile.

On Linux with .NET 8 SDK and NativeAOT prerequisites execute:
    bash scripts/test-cil-guest.sh
    bash scripts/test-match-cil-guest.sh
    bash scripts/test-stdio-cil-guest.sh

For a trusted standalone compilation:
    dotnet run --project compiler/Atto.Cil.Compiler -- \
      original.atto artifacts/AttoGuest.dll --allow-stdio

The stdout test executes **unmodified** canonical guarded return/caret
programs through compiled CIL on CoreCLR and NativeAOT, requiring
exact stdout bytes `2\\n2\\n`, exit 0 and empty stderr.
Both original sources are SHA pinned to the shared conformance corpus.

This compiles real .atto to artifacts/AttoGuest.dll, executes positive guest
programs with exit code 42 on the managed CLR and Linux NativeAOT,
and confirms negatives produce typed diagnostics without output artifacts.

The source organization's GitHub Actions may fail before runner admission;
zero-step failures are not evidence of compiler failure. For private
alternate-org test carriers use exact Git-tree SHA and per-blob verification,
never a cross-org token, and require nonempty actual executed test steps.

Oreslang .NET architecture is consulted for compatibility but its compiler
and runtime results cannot be counted as AttoLang executable parity.

## Division and signed minimum alignment (2026-11-20)

This branch's guarded-return matcher is **integer-only**. The accepted AttoLang rule is that `int / int` produces `float`. Earlier code on this branch emitted signed-integer CIL `div`, which would incorrectly truncate `5 / 2` to `2`. The source semantic checker now rejects every `/` expression with `E_DIVISION_FLOAT_RESULT` before a DLL is emitted, and the emitter has an independent defense. `scripts/test-match-cil-guest.sh` asserts the diagnostic, nonzero compiler status, and zero output artifact. The exact signed minimum `-9223372036854775808` is also parsed as a single i64 constant, consistent with the independent compiler branch.

This is **not** floating-point execution support in the guarded-match branch. [PR #191](https://github.com/atto-lang/atto-source.net/pull/191) owns bounded binary64 lowering; its type checker and guest fixtures must be integrated into this branch's match/stdio parser before either line of work is released together. Do not merge this draft in a way that restores integer div semantics or discards the other PR's tests.

The direct-CIL workflow installs the root-required .NET 10 SDK alongside the net8 runtime and verifies the exact checkout head. CoreCLR/NativeAOT tests are **NOT_EXECUTED** at this changed head until a real runner performs the jobs. Existing source-export security holds remain in force.
