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

## Integrated immutable locals and functional pipelines

This profile adds the checked `|>` and `<|` call lowering, bounded immutable locals, and colon-return inline bodies from PR #16 while retaining main's match, namespace, division-proof, actor-state rejection and explicit stdout admission. Local slots 0–3 are disjoint from match subject slots 4–20. Unsafe Unicode format/control characters are rejected; visible Unicode prose is permitted only in line comments. The PE content ID is a hash of ordered serialized blobs. Output publication requires a pre-existing trusted parent directory and refuses overwriting any existing destination. Callers must deliberately remove their own previous output before compilation; rejection never deletes an existing DLL.

Historical pipeline PR coverage and scope:



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

### Reproducible PE content identity

The emitted DLL's PE content identifier is derived from SHA-256 over the ordered serialization blobs (not a time-based stamp). The existing replay test recompiles identical Atto source to a separate fresh destination and compares complete DLL SHA-256 values. Existing destinations remain protected; this change does not relax output or source admission. Reproducibility is scoped to the pinned compiler/toolchain and options, not a blanket cross-SDK reproducibility guarantee.

## Ordered scalar sequences (stacked PR #20)

`seq(a,b,c)` and `(> a,b,c)` evaluate 1–16 signed-i64 expressions in order; IL `pop` discards each non-tail value, never its execution. All members must be int, including singleton sequences. Existing checked-division admission remains mandatory: a literal zero divisor anywhere, including a discarded prefix, fails before emission. Checked arithmetic overflow in a prefix still traps during actual execution. This branch preserves the parent pipeline, match, stdout and deterministic no-clobber publication implementation. The older sequence PR's 14 pipeline fixtures are byte-identical to the parent; its 13 unique sequence fixtures are retained unchanged. Run `scripts/test-seq-cil-guest.sh` in addition to the three parent suites. No mutable bindings, actor ownership or general task semantics are implied.
