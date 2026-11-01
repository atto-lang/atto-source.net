# .NET scalar binding, inference and cast parity (experimental)

Reference implementation: the Java/GraalVM Oreslang type checker.
Implementation: `compiler/Ores.Net.Compiler.Core/StructuredSource.cs` (structured parser + typed CFG/CIL bootstrap only).

## Canonical forms

```ores
const int x = 7;
const y = 7 as int;
const z = 7;
const w: int = 7;
```

All four yield a fixed, readonly scalar binding with static type `int`.
The declaration type checks the initializer; `as int` checks the expression before type inference; bare `const z` infers from the initializer *without* a cast.
`const` standalone requires a **compile-time-proven scalar initializer** (Java language compatibility). Statically evaluable scalar expressions are folded using checked signed-i64 operations; a runtime-dependent `const` initializer is rejected, not silently lowered into runtime SSA. A `val` or new type-first `int x = expression` / `bool x = expression` is readonly, single-assignment and permits checked runtime evaluation.

`let x = 7` remains explicitly rebindable, and `val x = 7` remains fixed. The new unqualified type-first spelling uses `Val` in the experimental scalar HIR because this first subset has no mutable referent; the general Java compiler must separately enforce its readonly-view ownership distinction. Only `int` and `bool` are currently admitted by the .NET structured scalar subset.

A checked `as` identity cast for `int` and `bool` emits no conversion, reinterprets no pointer, and does not grant mutability. Unrelated casts such as `true as int` are rejected at source admission. `as?`, nominal/dynamic casts, generic/ADT/Option type inference, and `string` are **unsupported** in the .NET scalar bootstrap and must fail closed.

## Verification scope

`StructuredSourceTests` checks AST binding type/mutability, runtime initializer preservation, negative misuse, chained casts and scalar inference.
`StructuredCfgLoweringTests` checks emitted CIL against a separate CFG reference evaluator and enforces deterministic PE bytes for checked cases.
These tests require actual tool execution; merely committing them does not count as a passed test.

The legacy arithmetic `OresParser` remains a separate minimal compiler. This is not a claim that every .NET CLI/AOT path supports arbitrary Java Oreslang source or that .NET has full Java casting parity.
Check exact-head NativeAOT and executed steps plus independent review before merge. A source-org zero-step Actions failure is runner admission, not evidence of typechecker behavior.

## Compatibility and negative controls

```ores
int answer = 7;          // readonly binding, compile-time literal
val int from_input = p;  // readonly runtime single-assignment
int inferred = p + 1;    // readonly runtime single-assignment
const illegal = p + 1;   // error: runtime expression, not a constant
```

The experimental `StructuredSourceTests` and `StructuredCfgLoweringTests`
now add type-first local accept/rebind/duplicate/type mismatch tests and
standalone runtime-`const` negatives. No source-level actor state, generics,
string values, non-Copy ownership, or public shipping CIL admission is claimed.
