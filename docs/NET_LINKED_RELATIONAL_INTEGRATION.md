# Linked module + signed i64 comparisons (integration of PRs #381 and #383)

This separate integration branch is based on PR #381, with PR #383's signed
`<=` and `>=` parser, checked constant expressions, SSA/CFG verification,
signed `clt`/`cgt` and inversion CIL, and test-only interpreter reconciled
without overwriting module resolution, embedded Core imports, or the real
Native AOT module path.

The canonical four-file source graph in `examples/modules/atto-relational-*.atto`
performs nested imported-module calls, `<`, `>`, `<=`, `>=`,
signed i64 extrema, and checked arithmetic. It returns 42. Mutating the
transitive imported arithmetic module to `n * 2 + 1` produces 43. This
is **not** a branch-local fallback, host reimplementation or parser-only
fixture: both cases compile through `compile-modules` to distinct typed CIL,
and require actual Linux and Windows Native AOT execution in the source-owned
workflow.

Tests check linked CIL execution (verification-only interpreter), the signed
CIL opcode shape, source unit byte hashes, graph identity changing on
transitive semantics mutation, private root-bound imports and wrong operand
types. The workflow additionally verifies actual checked-out Git blob IDs,
the compiler's source witness, SHA-256 graph reconstruction, generated CIL
hashes, native PE/ELF executable digests, output 42/43, distinct output
images and exact source head/tree/run provenance.

**Security and status:** branch is an integration candidate only. Both
predecessor PRs remain independently reviewed drafts. Never label test-only
CIL as executed guest Native AOT. Hosted source-org CI with zero job steps
is NOT_EXECUTED, not passing. Do not export private source to unauthorized
runner organizations, merge, or certify five-backend parity without real
exact-head Linux/Windows guest execution, independent review, and
Java/LLVM same-source comparisons.
