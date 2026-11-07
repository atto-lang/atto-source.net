# NativeAOT compiler-owned canonical Core numeric v2

This extension embeds the existing **unchanged** `numeric.atto` file into
the .NET compiler assembly and resolves six `atto.core/numeric`/
`ores.core/numeric` alias spellings to one canonical unit
`@atto.core/numeric.atto`. A user project file is never consulted for a
compiler-owned import.

The structured parser now admits the canonical `if CONDITION; do ...
fi` syntax directly into typed branch AST/CFG, with nested branch
scopes, early returns and definite-return analysis. Signed i64 greater
than is a new typed operator through CFG verification, CIL `cgt`
emission, and the independent reference evaluator. Existing brace
if/else and stage-1 identity syntax remain intact.

This is authored compiler implementation, **not** verified NativeAOT
execution. The currently blocked CI must compile the canonical numeric
fixtures, verify source/graph/compiler/artifact digests, run Linux and
Windows native guests, and reject shadowed or forged imports before
claiming parity.

## Real executable CIL numeric differential regression group

The module-linking self-test now emits actual typed CIL and runs its
verification interpreter for 12 canonical numerical operations:
min/max (including equality), clamp low/high/midpoint, reversed
bounds, equal bounds and extreme signed values. Six alias probes
check the same embedded numeric source witness, and an ambient
project shadow returning 99 must not change the CIL result 42.
Seven negative namespace/path spellings, an invalid module alias
and an unclosed do/fi scope must fail. The checked CIL verifier
test interpreter now understands the signed `cgt` opcode, matching
the compiler and independent CFG reference evaluator.

These authored tests are NOT hosted execution results; the native
Linux/Windows runner still needs to allocate and run them.

## Linux/Windows exact source-head NativeAOT numeric 42-to-43 proof

The existing `source-modules-native-aot.yml` job now has a
compiler-backed canonical numeric stage-2 lane for Linux and
Windows. It builds the canonical imported `clamp_int(50,0,42)`
and an immutable `clamp_int(50,0,43)` mutation, rechecks both
two-unit source graph inventories and UTF-8 byte hashes, recomputes
their graph SHA-256 digests, compiles separate CIL and NativeAOT
outputs, and requires observed results 42 and 43. Their graph,
CIL and native artifact digests must differ; the pinned compiler
binary SHA is rechecked before/after. Each receipt records the
immutable commit/tree, exact library/entry Git blobs, compiler
binary hash, target RID, graph/CIL/native digest, and result.

These are source-authored CI checks, **NOT** observed NativeAOT
execution as long as GitHub runner allocation remains blocked.

## Stable CFG opcode identity

`GreaterThanI64` was added **after**, rather than between,
the existing CFG/AST enum cases. That avoids renumbering
`Argument`, `Call`, `NotEqualI64` or earlier structured
operators in any existing serialized or compared compiler evidence.
A new opcode's value remains assigned only to the new semantics.

## Exact-head source blob read-back and Windows LF canonicalization

The Linux/Windows NativeAOT source workflow now verifies that the
**actual working-tree byte content** of each identity/numeric guest
and its canonical library resource has the exact Git object ID
recorded at `HEAD:path`, using `git hash-object --no-filters`
before compilation and after executing the native guest. This
prevents a modified working-tree source from being paired with an
untouched committed source blob in a receipt. The repository now
forces `*.atto text eol=lf` on checkout, including Windows, so
the byte-level attestation is portable rather than accidentally
rejecting legitimate Windows CRLF conversion. Graph, CIL,
compiler-binary and native-artifact SHA-256 checks remain separate.
No hosted run of these new checks is yet confirmed.
