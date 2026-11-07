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

## Adversarial hardening addendum

The integration fixture now exports `at_most` in addition to `at_least` so
signed `<=` is exercised independently of the nested positive branch in the
original four-file example. The C# self-test compiles and interprets **14**
parameterized imported `.atto` comparison probes with true and false
outcomes, signed minima/maxima, equality and negative boundary pairs. It also
rejects bool/i64 call mismatches, malformed chained comparisons, and missing
qualified imported functions. The CIL opcode-shape check now requires
contiguous compare/zero/equals inversion **within one MethodDef**; patterns
spanning distinct method bodies cannot produce a false pass.

The executable Native AOT verifier denies receipts without source-owned
`GITHUB_REPOSITORY`/`GITHUB_JOB`, positive run ID/attempt, and an OS matching
the target RID. It rejects reused temp directories and symlinked
`artifacts/` roots, deletes any prior target binary, and requires a new
publish artifact. The source-module CI workflow independently parses the
PowerShell script and verifies that a spoofed job identity is rejected before
compilation. These are authored regression gates, **not execution results**.
