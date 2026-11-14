# Experimental C# and F# source emission from verified OresIR

The .NET backend's shipping compiler emits CIL directly, and strict Windows
Native AOT publication remains unchanged. Two **opt-in developer commands**
also emit human-readable managed-language source from the *same verified,
single-function checked-i64* OresIR:

```pwsh
dotnet run --project compiler/Ores.Net.Compiler -c Release -- emit-csharp examples/hello.ores --out artifacts/OresProgram.cs
dotnet run --project compiler/Ores.Net.Compiler -c Release -- emit-fsharp examples/hello.ores --out artifacts/OresProgram.fs
```

The outputs define the identical statically callable ABI,
`Oreslang.Generated.OresProgram.Run() : long`, and neither exporter ever
executes or compiles code at runtime. The source commands parse with the
shipping scalar parser, lower to verified OresIR, then emit a fixed vocabulary
of C# or F# constructs. They **do not** decompile CIL: the common source is
verified OresIR. Unknown opcodes, noncontiguous SSA, wrong ABI, and unknown
source syntax must fail before a generated file can be published. A 1 MiB
input bound, 16,384 SSA-instruction bound, and 2 MiB output bound apply.

## Why two source outputs?

C# and F# each compile to ECMA-335 CIL with their respective .NET compilers.
Native AOT then consumes IL, not C# or F# text. Using CIL as an intermediate
artifact to *derive* source would require a separate decompiler and discard
source-level type information. Prefer:

```text
Oreslang (.ores) → parser → verified OresIR
                               ├─→ CIL (shipping) → .NET Native AOT
                               ├─→ C# source → Roslyn → CIL → optional Native AOT
                               └─→ F# source → F# compiler → CIL → optional Native AOT
```

## Arithmetic semantics and limits

- Every `long` binary C# expression is wrapped in `checked(...)`.
- F# source imports `Microsoft.FSharp.Core.Operators.Checked`, overriding
  its otherwise *unchecked* default arithmetic.
- Literal `-9223372036854775808` emits `long.MinValue` / `System.Int64.MinValue`
  so host-language literal parsing cannot alter the value.
- Operations execute in verified SSA order, not as a folded source expression.
  Overflow in a non-returned intermediate computation is not elided by source
  generation. A verifier-owned SSA local has one value and one operation.
- Internal checked i64 division follows CLR signed division traps. **Oreslang
  source** `int / int` is still rejected because the normative result is
  `float`, and a C# or F# integer `/` would be semantically incorrect.
- Actors, channels, closures, user functions, heap ownership, FFI, async, imports,
  strings, templates, tuples, and the experimental structured CFG parser are
  *not* admitted through these commands.

## Compile-time verification

Run the managed unit/conformance harness:

```pwsh
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
```

Generate source and compile *each* through its own language compiler:

```pwsh
./verification/assert-managed-source.ps1
```

The smoke test produces separate `OresGenerated.dll` assemblies in
`artifacts/source-smoke/csharp` and `artifacts/source-smoke/fsharp`.
These are intermediate managed build artifacts, **not standalone releases**.
The normal Native AOT release script continues to use the verified direct-CIL
backend. Do not claim Native AOT compatibility, execution parity, or released
Windows binaries until each emitted assembly is linked via
`host/Ores.Net.NativeHost`, published, and executed with exact-head CI
evidence. In particular, F# brings an FSharp.Core dependency that must be
included in Native AOT/trimming analysis rather than assumed safe.

## Required follow-up gates

1. Compile both source outputs and inspect CIL for checked `add.ovf`,
   `sub.ovf`, `mul.ovf` and signed `div` where applicable.
2. Execute a positive and negative parity corpus, including `long.MinValue`,
   `long.MaxValue`, overflow, divide-by-zero (internal IR only), and
   exception-to-host-exit-code mapping, against the independent OresIR
   reference evaluator and the direct-CIL target.
3. Publish both generated assemblies through an exact-head, Windows Native AOT
   host build, and verify absence of JIT/runtime-loading/managed sidecars.
4. Only then expand the common verifier and all three emitters together to
   locals, control flow, typed aggregates, functions, and later actors.

Until those gates pass, these are **experimental developer-facing
transpilation targets**, not Oreslang language or runtime parity claims.
