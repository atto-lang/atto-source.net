# Implicit-const scalar v1 — exact-source candidate lane

**Status: CANDIDATE / COMPILER PARITY NOT VERIFIED.** These are staged identical
`.atto` inputs for Java/Graal, LLVM and .NET structured compiler verification.
The presence of a file, source-only check or failing pre-run GitHub Actions job
is not executable parity evidence.

| Input | Expected target |
| --- | --- |
| `literal-equivalence.atto` | Executed result 42 |
| `const-reference.atto` | Executed result 42, prior immutable const resolved |
| `const-arithmetic.atto` | Executed result 42, checked compile-time arithmetic |
| `bool-local.atto` | Executed result 42, typed boolean declaration |
| `runtime-val-let.atto` | Executed result 42, runtime helper under val and let |
| `implicit-nonconst.atto` | Reject E_NON_CONST_INITIALIZER before executable output |
| `explicit-nonconst.atto` | Reject E_NON_CONST_INITIALIZER before executable output |
| `const-mut-scalar.atto` | Reject E_CONST_MUT_UNSUPPORTED |
| `const-overflow.atto` | Reject E_CONST_OVERFLOW before executable output |
| `implicit-rebind.atto` | Reject E_IMMUTABLE_REBIND before executable output |
| `explicit-rebind.atto` | Reject E_IMMUTABLE_REBIND before executable output |

Eleven fixture blob SHAs MUST match exactly across the three compiler repositories
at the tested immutable source heads. The companion exact-head CI workflow in
each repository is the intended compiler execution gate, not a proof until it
actually runs the compiler and collects real execution/diagnostic evidence.

Compiler diagnostic IDs in these fixtures are mapped to language-spec semantic
categories: `E_NON_CONST_INITIALIZER` → `non_const_initializer`;
`E_IMMUTABLE_REBIND` → `immutable_reassignment`;
`E_CONST_MUT_UNSUPPORTED` → `invalid_const_mut_target` (scalar only);
`E_CONST_OVERFLOW` → checked `const_eval_overflow`. Unsupported global or
aggregate `const mut` remains outside this contract.

**Boundaries:** this is the *scalar* subset only. Java has legacy aggregate
`const mut` = mutable reference behavior to migrate independently; LLVM and
.NET lack verified fresh owned mutable constant aggregate materialization.
Unqualified generic, nominal or aggregate locals, formatter idempotence, full
AOT isolation and cross-actor owned object transport are not covered here.
Native LLVM O0/O2, Java actual linked guest, and .NET NativeAOT execution all
need separate stepful receipts. Keep compiler code host-specific and portable
library behavior in canonical AttoLang `.atto` sources.
