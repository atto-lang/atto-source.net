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


## Draft scalar expression sequence parity

The direct CIL candidate branch now lowers `seq(a, b, c)` and
`(> a, b, c)` as bounded, left-to-right `int64` expression sequences.
Earlier expressions generate IL followed by `pop`; the final expression
supplies the return value. `seq(value)` is permitted. Nested sequences,
precedence and explicit function-call arguments remain distinct from
tuple syntax. The `seq` identifier is reserved, never dispatched as a
user-defined function.

No expression member may be a binding/statement in this experimental
single-expression compiler. Maximum 16 members and existing expression
depth/source budgets apply. Empty/leading/trailing commas, literal tuple
parentheses, and `seq(a; b)` fail closed with typed diagnostics.

`scripts/test-cil-guest.sh` builds a generated CIL guest linked to the
CoreCLR and NativeAOT hosts, tests nested/call-argument/singleton
sequences, and confirms runtime exceptions from *non-final* checked
integer overflow and division by zero. It also rejects malformed
sequences without publishing a `.dll` artifact.

**Evidence status:** tests are submitted but require execution by a
runner with .NET SDK/NativeAOT prerequisites. Do not mark cross-backend
parity `passed` from a new PR, a host-only test, or an Actions job
with zero executed steps. All actors, ownership, suspend, `defer`, and
versioned comma-body syntax remain unsupported by this CIL subset.
