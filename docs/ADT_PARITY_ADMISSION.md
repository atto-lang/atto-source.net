# ADT parity — .NET Native AOT admission boundary (2026-11-01)

The production `OresParser` compiles a narrow i64-only source subset. It
does not implement nominal enum declarations, closed cases, payload patterns,
finite-domain exhaustive matching, Option/Result or actor-wire ADT semantics.
Its `enum` token is deliberately rejected with a specific unsupported ABI
diagnostic. Source admission is not semantic ADT parity.

The new `AdtSourceAdmissionTests.Run()` is invoked by the existing .NET
verification executable and checks **16 nominal ADT source variants**, **7**
unsupported non-enum ADT source shapes and **4** comment/trivia controls.
It is not a compiler-backed positive guest execution suite.

Target parity: 36-case versioned Oreslang source corpus in
`oreslang-platform-conformance` PR #270. No Java/LLVM/.NET/Wasm/Forge
backend is confirmed for that exact matrix. To claim .NET parity,
implement compiler-owned nominal ID resolution, tag and payload type checks,
closed exhaustiveness and ownership-safe CIL/Native AOT lowering, then execute
positive and semantic negative cases on a verified exact compiler tree.
Do not count generic `FormatException`, zero-step Actions or host-only tests
as certified guest semantics. Keep draft until reviewed, stepful CI runs.

## Structured and linked NativeAOT ADT admission follow-up

The `OresStructuredParser` is a separate CFG/SSA entrypoint from
`OresParser`. The tokenized structured lexer now rejects executable
`Option`, `Result`, `Some`, `None`, `Ok`, `Err` and `trap` with the
same stable unsupported-feature diagnostic. That gate also applies to
linked-unit import discovery, while quoted strings, nested comments,
and longer identifiers are not promoted to ADT syntax.

`OptionResultTrapAdmissionTests` checks the real structured parser, not
a regular-expression scanner or fake scalar evaluator. No tagged CIL
layout, case payload, borrow ownership, exhaustive match, actor wire
encoding or Native AOT guest execution has been implemented by this
hardening. A compiler's unsupported-feature refusal must not be counted
as a passing semantic-negative fixture in the canonical `.atto` ADT
matrix. Keep `confirmedBackends=[]` until compiler-backed evidence is
available on the exact source head with nonzero Actions steps.
