# Canonical Oreslang numeric source — executable .NET CIL slice

This draft is stacked on source module linker PR #229. It does **not**
turn the .NET backend into a general Oreslang compiler.

## Source of truth

`library/ores.core/numeric.ores` is a byte-identical vendor copy of
`oreslang-common-source/library/ores.core/numeric.ores` (draft PR #179),
originally shipped as Java/Graal
`src/main/resources/dev/oreslang/core/numeric.ores`.
The canonical source is written in Oreslang and contains
`min_int`, `max_int`, and `clamp_int`.

SHA-256: `3d1d7b77068dd02b3d60b30b72f29a4537a933134ca79e1e81df096dc56f5f33`

Git blob SHA-1: `5ced991f1d49b3c2a60fdd72b9003c0a4d18cd72`

The independent 24-case signed-i64 reference is vendored unchanged into
`verification/contracts/core-math-i64.json`. This file is an **oracle**,
not a replacement native compiler implementation.

## Compilation and safeguards

`OresSourceModuleCompiler` consumes the actual `.ores` file using the
existing source graph resolver. This patch admits two missing constructs
for the canonical source: the structured Oreslang
`if condition; do ... [else ...] fi` block and checked signed-i64
greater-than comparisons. Both lower into the same verified CFG as
existing brace-form conditions. Greater-than swaps only already-evaluated
SSA register IDs in a `LessThanI64` instruction; the original left and
right source operands still evaluate exactly once, left-to-right.

Run the sample with the *unmodified* numeric module:

```sh
dotnet run -c Release --project compiler/Ores.Net.Compiler -- \
  compile-modules examples/canonical-numeric/main.ores --root . \
  --out artifacts/canonical/OresGenerated.dll
```

The example must return `10` when linked into the existing Native AOT host.
The additional `CanonicalNumericSourceSelfTest` runs all 24 official
vectors through the actual linker, CFG verifier, CIL emitter and bounded
CIL interpreter; it rejects source drift, missing canonical dependency,
and unexpected nominal source injection.

`.github/workflows/canonical-core-native-aot.yml` independently publishes
the compiler itself with Native AOT, compiles the canonical module,
publishes the statically linked executable and checks its actual output
on Linux x64 and Windows x64, with exact-head and byte-hash receipts.

## Evidence limits

The presence of authored tests or successful source contract validation
is **not** evidence of runnable guest code. Treat zero-step GitHub Actions
failures as `NOT_EXECUTED`. Passing the CIL interpreter only proves the
tested compiler/emitter subset; Native AOT execution requires its own
finished CI jobs and exact-head receipts.

The source namespace and imported module API are still loaded through
a caller-authorized relative file path. Reserved system-library module
resolution, actor ownership, OS-free linking, general `Option/Result`,
string/collection imports, and broad .NET language parity are **not**
implemented here. No C# numeric algorithm, reflection/JIT, dynamic
assembly loading, or loose host fallback is introduced.
