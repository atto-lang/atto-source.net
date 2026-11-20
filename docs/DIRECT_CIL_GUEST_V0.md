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

Bounded functional-pipeline extension (proposed, not yet guest-verified):
- Forward application `value |> function |> next` is left-associated and
  lowered into ordinary checked function calls.
- Reverse application `function <| value` and `f <| g <| value` is
  right-associated and uses the same checked call path.
- Name-first `: int -> expression;` is a single-expression implicit return.
  Existing `: int { return expression; }` remains an explicit-return body.
- Braced bodies can declare at most four immutable `val name = expression;`
  bindings before the final explicit `return`; each initializer is evaluated
  once, in source order, into an ECMA-335 int64 local with a declared local
  signature. Local forward references and name shadowing fail closed.
- Mixing `<|` and `|>` requires parentheses. Dynamic callees and implicit
  actor/stream pipelines remain unsupported.

Other unsupported forms still fail closed: booleans, floats, strings, mutable
bindings, arbitrary control flow, actors, ownership/borrowing, I/O, imports,
async, exceptions, recursion and dynamic interop. Limits: 64 KiB UTF-8 source,
8192 tokens, 64 functions, 8 args per function, 4 immutable locals per
function and 32 expression nesting levels.

The real guest matrix is under `fixtures/cil/pipelines/`: the two exact
83-result programs, 42-result right association and explicitly grouped
directions, plus ten negative cases. The test script checks deterministic
generated CIL, executes all four positives under CoreCLR, publishes both
83-result programs into NativeAOT hosts, and requires rejected sources to
leave no DLL. **Authored tests are not execution evidence** until exact-head
CI logs show nonzero steps and actual successful guest exits.

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


## Checked signed-i64 and output publication hardening

The bounded direct-CIL frontend now admits the signed minimum
`-9223372036854775808` without ever parsing a nonrepresentable *positive*
64-bit literal. It rejects `-9223372036854775809` and reserved
authority/effect identifiers such as `unsafe` at source admission.

Guest DLL output is **never replaced or deleted by the compiler**. It
validates and emits the entire checked CIL image before trying to publish,
requires an existing output directory, exclusively creates the temporary
path using `FileMode.CreateNew`, flushes the temporary image to disk,
and uses `File.Move(..., overwrite: false)` for final publication.
Compiler tests must intentionally choose fresh output paths for each
successful build. Invalid sources must leave an existing prior DLL
byte-identical; a competing temporary file must not be truncated.
These checks assume a trusted build directory and do **not** constitute
a TenantVM security boundary against adversarial directory replacement.

Added an independent, fixed `Atto.Cil.Value.Host` adapter that only
formats the **actual compiled CIL** `main(): int` return value. It can
therefore observe `int64::MIN` without squeezing the 64-bit result into an
8-bit OS exit status. The supplemental matrix requires CoreCLR and
NativeAOT minimum-value execution and requires an actual
`OverflowException` rather than wraparound for a checked `MAX+1`
pipeline. Twelve pipeline-negative fixtures, no-overwrite replay,
byte-preserving invalid-source refusal and temporary-path protection
are included in `scripts/test-cil-guest.sh`.

**Verification status:** these are committed compiler and test changes.
There is no `dotnet` SDK in the local execution environment used for
this implementation pass, and GitHub Actions job admission still fails.
The new CoreCLR and NativeAOT tests have **not executed**. The
conformance ledger must remain `not_executed` until an exact-head
runner provides real CLR/NativeAOT guest evidence. This remains a
restricted direct-CIL subset, not the production multi-tenant compiler.
