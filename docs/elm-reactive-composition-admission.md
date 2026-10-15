# .NET Native AOT — Elm-inspired composition admission

Status: **draft negative source-compiler gate**. The Oreslang CIL and
structured source compiler paths currently support deliberately constrained
checked scalar/control-flow subsets, not generic `Option<T>`, `Result<T,E>`,
`Future<T>`, `Stream<T>`, `Observable<T>`, `Channel<T>`,
closures or typed Oreslang actor supervision.

`ElmCompositionAdmissionTests` loads six source cases from a copied JSON
corpus, validates an executable-grammar scalar control, and demands
`FormatException` with a diagnostic from both source parser paths. The
tests execute in the existing `Ores.Net.Tests` verification application.
Success means unsupported source is rejected; no managed helper's behavior
is evidence of guest code generation or Windows Native AOT parity.

The canonical Oreslang fnc and method identifiers are snake_case
(`flat_map`, `and_then`, `map_err`, `filter_map`,
`on_success`). Do not expose camelCase as new public language spelling.

Implement native composition in later stages only after typed sum payload
layout, checked pattern matching, immutable source effects, affine transfer,
continuation ownership, structured cancellation, and explicit `Stream` /
`Observable` / `Channel` backpressure/cardinality contracts.
A host .NET task or `IObservable<T>` is not automatically an Oreslang
`Future<T>` or `Observable<T>`.

See common-source #36, Java #1017/#222, LLVM #413, RX #12. Real release
validation still requires Native AOT published compiler and executable; unit
tests cannot substitute for it.
