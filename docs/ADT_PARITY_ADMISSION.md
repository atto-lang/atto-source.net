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
structured lexer refuses `Result`, `Ok`, `Err` and `trap` with the
same stable unsupported-feature diagnostic, in addition to rejecting
unsupported generic Option shapes and unsafe operations during typing.

The structured lexer regression checks both outcomes: actual closed
`Option<int>` source is admitted, but unsupported `Result`/trap source
never reaches verified CFG/CIL, even in the presence of comments or longer
identifiers. This is an implementation-layer boundary, **not** a claim of
NativeAOT executed guest conformance; exact-head stepful tests and reviewer
approval remain mandatory.

## Linked-unit compiler admission (stacked follow-up)

The public linked-unit compiler route tokenizes source before resolving imports
and lowering verified CFG. Tests cover unsupported Result, Ok, Err, and trap
forms in this route; only the bounded closed Option<int> subset is admitted,
and nested source comments remain inert. This preserves Option tag/payload SSA
work from #428 while ensuring alternate module entrypoints fail closed.

This follow-up intentionally does not reintroduce #468's former blanket lexer
rejection of Option, Some, or None. The shipping scalar compiler still rejects
those constructs. Tests are definitions until run against this exact branch head;
no NativeAOT guest result is inferred from earlier host-only carrier receipts.

## Canonical .atto source-linking compatibility

The linked source compiler now admits canonical .atto entrypoints and imports,
while keeping .ores files compatible for existing graphs. For extensionless
local imports, it selects the only existing candidate; if both foo.atto and
foo.ores exist it rejects the path as ambiguous, requiring an explicit suffix.
The established source-root ownership, strict UTF-8, no-reparse/symlink,
per-file byte budget and bounded graph checks still apply after resolution.
The linked ADT regression covers a .atto entry, canonical .atto helper import,
ambiguous dual-suffix import rejection, and .ores fallback. These tests are
not executed evidence until a trusted runner executes the exact new branch.

Extensionless dual-candidate existence uses the existing no-follow project
marker probe rather than File.Exists alone, so a dangling .atto symlink is
not silently skipped in favor of a .ores file. The POSIX test creates such a
dangling candidate and requires the same ambiguity rejection; Windows tests
avoid creating symlinks that may require privileges. Authorized imports still
pass the existing explicit reparse-point rejection.
