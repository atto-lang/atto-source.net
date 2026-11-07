# NativeAOT canonical AttoLang module provenance hardening

The same host project previously published two different generated CIL graphs in a single build workspace, both returning 42. A stale NativeAOT build could therefore look plausible. CI now validates every compiler-reported .atto source SHA-256 and byte length, verifies four canonical source identities and graph SHA, pins the generated CIL digest and requires the canonical and legacy CIL builds to differ. A `dotnet clean` runs before each NativeAOT publish, and the receipts record graph, CIL, native executable and exact source IDs.

This is a proposed executable CI gate, not a completed test. A zero-step GitHub job means runner admission failure. The compiler-owned `atto.core` namespace is still not implemented in this .NET path.

## Workflow parse corruption fix — 2026-11-07

The earlier workflow edit accidentally interpreted JavaScript replacement
tokens like `$'` inside PowerShell regex literals. This inserted a copy
of the remaining workflow and truncated the canonical source-witness check.
We rebuilt this workflow from the last intact Git blob and reapplied the
witness/hash/clean gates with literal-safe replacement, checking for exactly
one canonical, legacy and overflow job step. The new feature-branch push
trigger allows the source-owned workflow to attempt running without relying
solely on a PR-workflow file that is not yet merged to `main`.
The previous `jobs=[]` Actions runs are classified as workflow admission/
configuration failures, not runner allocation or native test failures.

## Independent compiler source-graph SHA-256 verification

The NativeAOT source-local workflow now verifies both the four
individual exact `.atto` source bytes *and* the aggregate graph
digest reported by the compiler. The witness must have exactly the
expected four `0/atto-*.atto` source identities, sorted using ordinal
case-sensitive ordering with no duplication. PowerShell reconstructs
the linker preimage as `path + NUL + lowercase SHA256 + LF` for each
unit, SHA-256 hashes the UTF-8 bytes, and compares that value to the
compiler's `graph_sha256`. A syntactically valid but forged graph digest
cannot become a NativeAOT source receipt. The following source-owned
NativeAOT execution and CIL byte hashes remain separate required checks.
No hosted success is claimed while GitHub jobs have zero executed steps.
