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
`const` is an immutable binding, **not** an instruction to eagerly execute every initializer at compile time. Statically evaluable pure scalar expressions may be folded using checked signed-i64 operations. Runtime-dependent expressions retain their typed SSA initializer and cannot be rebound.

`let x = 7` remains explicitly rebindable, and `val x = 7` remains fixed. Only `int` and `bool` are currently admitted by the .NET structured scalar subset.

A checked `as` identity cast for `int` and `bool` emits no conversion, reinterprets no pointer, and does not grant mutability. Unrelated casts such as `true as int` are rejected at source admission. `as?`, nominal/dynamic casts, generic/ADT/Option type inference, and `string` are **unsupported** in the .NET scalar bootstrap and must fail closed.

## Verification scope

`StructuredSourceTests` checks AST binding type/mutability, runtime initializer preservation, negative misuse, chained casts and scalar inference.
`StructuredCfgLoweringTests` checks emitted CIL against a separate CFG reference evaluator and enforces deterministic PE bytes for checked cases.
These tests require actual tool execution; merely committing them does not count as a passed test.

The legacy arithmetic `OresParser` remains a separate minimal compiler. This is not a claim that every .NET CLI/AOT path supports arbitrary Java Oreslang source or that .NET has full Java casting parity.
Check exact-head NativeAOT and executed steps plus independent review before merge. A source-org zero-step Actions failure is runner admission, not evidence of typechecker behavior.
