# Atto .NET: disjoint class-field loan admission (draft, reject-only)

**State: AUTHORING_ONLY / NOT_EXECUTED. This is not field-sensitive borrow support.**
This proposal ports four source fixtures byte-for-byte from [atto-source.rs PR #477](https://github.com/atto-lang/atto-source.rs/pull/477) at source HEAD `4a623a2b41dc29d08ab15753011f6cfb85f6106e`. Rust #477 itself is stacked on [#476](https://github.com/atto-lang/atto-source.rs/pull/476); neither its merge status nor its guest evidence automatically certifies .NET.

| .NET fixture | Exact Rust Git blob SHA | Rust expectation |
| --- | --- | --- |
| `pass/two-disjoint-writers.atto` | `b22a031b37d9722b8a7d20b48e81136ec4552099` | distinct mutable field places write through |
| `pass/int-write-through.atto` | `24a84848cb97bf3e72d5fe329e4b74b4b5826fa9` | mutable field alias is not a snapshot |
| `fail/overlap-owner-read.atto` | `ef941b1713711291cf17e7fbb250794bd7b0013f` | whole/overlapping owner read is denied |
| `fail/owner-take.atto` | `f9df5b86a8d0ffdcdc357146ff05da7bc49dc2a3` | moving the borrowed owner is denied |

## .NET admission contract

`DisjointFieldPlaceAdmissionTests.Run()` runs as part of the ordinary `Ores.Net.Tests` test executable. It requires **both** `OresParser` and `OresStructuredParser` to reject all four fixtures with `FormatException`. The two Rust-positive cases intentionally remain **negative on .NET**, because .NET has not proved compiler-issued owner/field-slot provenance or typed class field write-through. The two Rust-negative cases do not prove `E_BORROW` diagnostics in .NET; current admission is only conservative rejection.

Never enable these positives simply by accepting syntax. Actual .NET parity requires a checked per-field loan checker, compiler-owned resolved field identities, NLL/last-use and branch-join proofs, source-owner lifetime, re-evaluation of owner state after RHS, conservative opaque receiver effects, and verified CIL/NativeAOT field loads/stores. Explicitly reject aliases, nested/dynamic fields, actor sealed state, cross-tenant references, overlapping exclusive loans, and untrusted field tokens until separately proven.

## Required test evidence and release gates

1. Independently check all four file blobs in this branch against the Rust source SHAs, and pin the source Git tree and exact .NET PR head.
2. Run real managed .NET tests with assigned runners and nonempty steps. The test suite must actually invoke both parsers; any unexpected parse acceptance fails.
3. Keep `GUEST=NOT_EXECUTED` on this PR. No CIL generated from these fixtures is expected or authorized.
4. For a future positive-loan feature PR, require a distinct compiler/codegen implementation, CoreCLR and Linux/Windows NativeAOT execution, negative counterexamples, and independent security/ownership review.
5. This branch does not change private CI export allowlists. Until protected source policy explicitly authorizes a reviewed destination and exact snapshot, do not mirror private Git objects to another organization. A zero-step GitHub job is NOT_EXECUTED.

## Cross-compiler scope

- Rust #476/#477: narrow scalar class-root field-loan profile, including compiler-owned write-through.
- Java [#977](https://github.com/atto-lang/atto-source.java/pull/977): Send/Sync and disjoint data-region planning are different from per-field mutable source borrow lowering.
- LLVM [#812](https://github.com/atto-lang/atto-source.llvm/pull/812): readonly structural field-offset witness is not a writable class-field loan.
- .NET: **negative admission only**, no feature parity assertion.
