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

## Merge-order reconciliation with structured Option<int> (#428)

The experimental `check-structured` / `compile-structured` lane in PR #428
has verified closed `Option<int>` (tag: Bool, payload: signed i64),
`Some`/`None`, tag queries and exact tagged equality. Consequently,
blanket lexer denial of `Option`, `Some` and `None` in independent
PR #468 **must not** be merged unchanged on top of #428. The production
`OresParser` continues to reject all ADT source. The experimental
structured lexer refuses `Result`, `Ok`, and `Err` with the same stable
unsupported-feature diagnostic. The candidate phase-one `trap expression`
frontend only admits a synchronous checked `int` payload and lowers it as a
real typed `Some(int)` through the existing verified `Option<int>` SSA/CIL
path. Its normal outcome is `Some(value)`; terminal checked arithmetic
panic still bypasses `trap`. `trap fnc`, bool/Option payloads, `Result`,
nested sums, ordinary-throw catching, and suspended expressions remain
explicitly unsupported.

The structured lexer regression checks both outcomes: actual closed
`Option<int>` source is admitted, but unsupported `Result`/trap source
never reaches verified CFG/CIL, even in the presence of comments or longer
identifiers. This is an implementation-layer boundary, **not** a claim of
NativeAOT executed guest conformance; exact-head stepful tests and reviewer
approval remain mandatory.


## Cross-backend trap native guest candidate (November 2026)

Identical Git blobs of `trap-int-native42.atto` are pinned in the Java JUnit,
LLVM Clang AOT and .NET NativeAOT candidate branches. The guest must return
signed integer 42 through the single `Option<int>` `Some(42)` layer. Checked
overflow must terminate rather than manufacture `None`. This candidate
subset is **not** nested `Option<Result<int,String>>` or ordinary-error
unwinding parity: those are rejected in the .NET compiler until the typed
nested sum, ownership and guest exception ABI are implemented and verified.
